using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record SignupRequest(string CompanyName, string? Document, string OwnerName, string Email, string Password, string? TenantCode, string? Website);
public sealed record SignupVerifyRequest(Guid SignupId, string Code);
public sealed record SignupSettingsRequest(bool Enabled, string PlanCode, int TrialDays);

/// <summary>
/// Cadastro com teste grátis: a empresa só é criada depois que o dono confirma o código enviado por e-mail.
/// Proteções: limite por IP (rate limit "auth"), 3 tentativas por e-mail/hora, campo isca ("website") contra robôs,
/// senha guardada criptografada só durante a confirmação (30 min).
/// </summary>
public sealed partial class SignupService(AppDbContext db, TenantProvisioningService provisioning, PlatformSettingsService settings, EmailService email,
    IDataProtectionProvider dp, AuditService audit, TimeProvider clock, IHttpContextAccessor http, TenantContext tenantCtx)
{
    private IDataProtector Protector => dp.CreateProtector("Comercial.Signup.v1");
    public const int CodeMinutes = 30;

    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NonSlug();

    public static string Slug(string name)
    {
        var s = new string(name.Normalize(NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        s = NonSlug().Replace(s, "-").Trim('-');
        return s.Length > 30 ? s[..30].Trim('-') : s;
    }

    private static string Hash(Guid id, string code) => Hashing.Sha256Hex(id.ToString("N") + ":" + code.Trim());

    public async Task<object> StartAsync(SignupRequest r)
    {
        var cfg = await settings.SignupAsync();
        if (!cfg.Enabled) throw new DomainException("signup_disabled", "Cadastro pelo aplicativo está desativado. Fale com o fornecedor do sistema.", 403);
        if (!string.IsNullOrEmpty(r.Website)) throw new DomainException("invalid_request", "Requisição inválida."); // isca para robôs
        if (string.IsNullOrWhiteSpace(r.CompanyName) || r.CompanyName.Length > 200) throw new DomainException("invalid_name", "Informe o nome da loja.");
        if (string.IsNullOrWhiteSpace(r.OwnerName) || r.OwnerName.Length > 120) throw new DomainException("invalid_name", "Informe seu nome.");
        var mail = (r.Email ?? "").Trim().ToLowerInvariant();
        if (!System.Net.Mail.MailAddress.TryCreate(mail, out _) || mail.Length > 200) throw new DomainException("invalid_email", "E-mail inválido.");
        if (PasswordHasher.ValidatePolicy(r.Password ?? "") is { } pe) throw new DomainException("weak_password", pe);
        var doc = FiscalRules.Digits(r.Document);
        if (doc.Length > 0 && doc.Length is not (11 or 14)) throw new DomainException("invalid_document", "CPF (11 dígitos) ou CNPJ (14 dígitos).");
        if (doc.Length == 14 && !FiscalRules.IsCnpj(doc)) throw new DomainException("invalid_document", "CNPJ inválido.");
        var code = string.IsNullOrWhiteSpace(r.TenantCode) ? Slug(r.CompanyName) : r.TenantCode.Trim().ToLowerInvariant();
        if (code.Length < 3) code = (code + "-loja").Trim('-');
        var baseCode = code;
        for (var i = 2; await db.Tenants.AnyAsync(t => t.Code == code) || await db.PendingSignups.AnyAsync(p => p.TenantCode == code && p.ExpiresAt > clock.GetUtcNow()); i++)
            code = $"{baseCode[..Math.Min(baseCode.Length, 36)]}-{i}";
        var now = clock.GetUtcNow();
        if (await db.PendingSignups.CountAsync(p => p.Email == mail && p.CreatedAt > now.AddHours(-1)) >= 3)
            throw new DomainException("too_many_attempts", "Muitas tentativas com este e-mail. Tente de novo em uma hora.", 429);

        var verify = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var p = new PendingSignup
        {
            Email = mail, TenantCode = code, ExpiresAt = now.AddMinutes(CodeMinutes), CreatedAt = now,
            Ip = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
            PayloadProtected = Protector.Protect(JsonSerializer.Serialize(new { r.CompanyName, Document = doc, r.OwnerName, r.Password })),
        };
        p.CodeHash = Hash(p.Id, verify);
        db.PendingSignups.Add(p);
        email.Queue(null, mail, "Seu código para criar a conta", EmailService.Layout("Confirme seu e-mail",
            $"<p>Olá, {System.Net.WebUtility.HtmlEncode(r.OwnerName)}! Use o código abaixo para criar a conta da loja <b>{System.Net.WebUtility.HtmlEncode(r.CompanyName)}</b>:</p>" +
            $"<p style=\"font-size:30px;letter-spacing:8px;font-weight:bold\">{verify}</p><p>Ele vale por {CodeMinutes} minutos. Se não foi você, ignore este e-mail.</p>"), "signup");
        await db.SaveChangesAsync();
        return new { signupId = p.Id, tenantCode = code, expiresAt = p.ExpiresAt };
    }

    public async Task<object> VerifyAsync(SignupVerifyRequest r)
    {
        // Requisição anônima: a criação da empresa roda no escopo da plataforma (só depois do código validado abaixo).
        tenantCtx.SetPlatformScope();
        var now = clock.GetUtcNow();
        var p = await db.PendingSignups.FirstOrDefaultAsync(x => x.Id == r.SignupId && x.CompletedAt == null && x.ExpiresAt > now)
                ?? throw new DomainException("invalid_code", "Código inválido ou expirado. Faça o cadastro de novo.", 400);
        p.Attempts++;
        if (p.Attempts > 5 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p.CodeHash), Encoding.ASCII.GetBytes(Hash(p.Id, r.Code ?? ""))))
        {
            if (p.Attempts >= 5) p.ExpiresAt = now; // queima após 5 erros
            await db.SaveChangesAsync();
            throw new DomainException("invalid_code", "Código inválido ou expirado.", 400);
        }
        var cfg = await settings.SignupAsync();
        var payload = JsonSerializer.Deserialize<JsonElement>(Protector.Unprotect(p.PayloadProtected));
        var company = payload.GetProperty("CompanyName").GetString()!;
        var doc = payload.GetProperty("Document").GetString();
        var tenant = await provisioning.CreateAsync(new CreateTenantRequest(p.TenantCode, company, company, string.IsNullOrEmpty(doc) ? null : doc, cfg.PlanCode,
            new OwnerInput(p.Email, payload.GetProperty("OwnerName").GetString()!, payload.GetProperty("Password").GetString()!), "Matriz"));
        // Teste grátis: o período atual termina em N dias (a primeira fatura é gerada antes do fim).
        var sub = await db.Subscriptions.IgnoreQueryFilters().FirstAsync(s => s.TenantId == tenant.Id);
        sub.CurrentPeriodEnd = now.AddDays(cfg.TrialDays);
        p.CompletedAt = now;
        p.PayloadProtected = ""; // a senha não fica guardada
        audit.Add("tenant.self_signup", tenant.Id, nameof(Tenant), tenant.Id, new { p.TenantCode, plan = cfg.PlanCode, cfg.TrialDays, p.Ip });
        email.Simple(tenant.Id, p.Email, "Sua conta está pronta", "Bem-vindo(a)!",
            $"Sua loja foi criada com {cfg.TrialDays} dias de teste grátis. Para entrar no sistema use o código da empresa \"{p.TenantCode}\", seu e-mail e a senha que você escolheu.", "welcome_owner");
        await db.SaveChangesAsync();
        return new { tenantCode = p.TenantCode, trialEndsAt = sub.CurrentPeriodEnd };
    }
}

public sealed record TotpCodeRequest(string Code);

/// <summary>Verificação em duas etapas (app autenticador) para usuários das lojas. Opcional, recomendada para o dono.</summary>
public sealed class UserTwoFactorService(AppDbContext db, RequestActor actor, AuthService auth, AuditService audit, TimeProvider clock)
{
    private IDataProtector Protector => auth.TotpProtector;

    public async Task<object> StatusAsync()
    {
        var u = await db.Users.AsNoTracking().FirstAsync(x => x.Id == actor.UserId);
        return new { enabled = u.TotpEnabled };
    }

    public async Task<object> SetupAsync()
    {
        var u = await db.Users.FirstAsync(x => x.Id == actor.UserId);
        if (u.TotpEnabled) throw new DomainException("already_enabled", "A verificação em duas etapas já está ligada.", 409);
        var secret = Totp.NewSecret();
        u.TotpSecretProtected = Protector.Protect(secret);
        await db.SaveChangesAsync();
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == actor.TenantId);
        var label = Uri.EscapeDataString($"Sistema Comercial:{u.Email}");
        return new { secret, otpauth = $"otpauth://totp/{label}?secret={secret}&issuer={Uri.EscapeDataString("Sistema Comercial " + tenant.Code)}&digits=6&period=30" };
    }

    public async Task EnableAsync(TotpCodeRequest r)
    {
        var u = await db.Users.FirstAsync(x => x.Id == actor.UserId);
        if (u.TotpSecretProtected is null) throw new DomainException("setup_required", "Gere o código do autenticador primeiro.", 409);
        var step = Totp.Verify(Protector.Unprotect(u.TotpSecretProtected), r.Code, clock.GetUtcNow()) ?? throw new DomainException("invalid_code", "Código do autenticador incorreto.", 400);
        u.TotpEnabled = true; u.TotpLastStep = step;
        audit.Add("user.2fa_enabled", actor.TenantId, nameof(User), u.Id);
        await db.SaveChangesAsync();
    }

    public async Task DisableAsync(TotpCodeRequest r)
    {
        var u = await db.Users.FirstAsync(x => x.Id == actor.UserId);
        if (!u.TotpEnabled || u.TotpSecretProtected is null) return;
        if (Totp.Verify(Protector.Unprotect(u.TotpSecretProtected), r.Code, clock.GetUtcNow()) is null) throw new DomainException("invalid_code", "Código do autenticador incorreto.", 400);
        u.TotpEnabled = false; u.TotpSecretProtected = null; u.TotpLastStep = null;
        audit.Add("user.2fa_disabled", actor.TenantId, nameof(User), u.Id);
        await db.SaveChangesAsync();
    }

    /// <summary>Gestor remove a 2FA de um funcionário que perdeu o celular (fica na auditoria; sessões dele são encerradas).</summary>
    public async Task ResetForUserAsync(Guid userId, UserService users)
    {
        await users.RevokeSessionsAsync(userId); // também valida se o gestor pode gerenciar este usuário
        var u = await db.Users.FirstAsync(x => x.Id == userId);
        u.TotpEnabled = false; u.TotpSecretProtected = null; u.TotpLastStep = null;
        audit.Add("user.2fa_reset", actor.TenantId, nameof(User), u.Id);
        await db.SaveChangesAsync();
    }
}

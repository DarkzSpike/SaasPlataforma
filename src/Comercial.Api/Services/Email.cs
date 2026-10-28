using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record SmtpConfig(string Host, int Port, bool Ssl, string? User, string? Password, string From, string FromName);

/// <summary>Transporte de e-mail (substituível nos testes).</summary>
public interface IEmailTransport
{
    Task SendAsync(SmtpConfig? config, EmailMessage message, CancellationToken ct);
}

/// <summary>
/// SMTP configurado no painel (Gmail, Outlook, Brevo, SES, etc.). Sem SMTP em desenvolvimento, grava o e-mail como
/// arquivo .eml em dev-data/emails (dá para abrir no Outlook/Thunderbird) — nunca "finge" que enviou em produção.
/// </summary>
public sealed class SmtpEmailTransport(IHostEnvironment env, IConfiguration config) : IEmailTransport
{
    public async Task SendAsync(SmtpConfig? cfg, EmailMessage m, CancellationToken ct)
    {
        using var mail = new MailMessage
        {
            From = new MailAddress(cfg?.From ?? "nao-responda@localhost", cfg?.FromName ?? "Sistema Comercial"),
            Subject = m.Subject, Body = m.HtmlBody, IsBodyHtml = true, BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8,
        };
        mail.To.Add(m.To);
        // Anexo gerado pelo próprio servidor (pacote do contador); sem o arquivo, o envio falha e fica registrado.
        if (m.AttachmentPath is { } path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("anexo não encontrado no servidor");
            mail.Attachments.Add(new Attachment(path, "application/zip") { Name = m.AttachmentName ?? Path.GetFileName(path) });
        }
        using var client = new SmtpClient();
        if (cfg is null)
        {
            if (!env.IsDevelopment()) throw new InvalidOperationException("smtp_not_configured");
            var dir = config["Email:PickupDirectory"] ?? Path.Combine(env.ContentRootPath, "dev-data", "emails");
            Directory.CreateDirectory(dir);
            client.DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory;
            client.PickupDirectoryLocation = dir;
        }
        else
        {
            client.Host = cfg.Host; client.Port = cfg.Port; client.EnableSsl = cfg.Ssl; client.Timeout = 20_000;
            if (!string.IsNullOrEmpty(cfg.User)) client.Credentials = new NetworkCredential(cfg.User, cfg.Password);
        }
        await client.SendMailAsync(mail, ct);
    }
}

/// <summary>Coloca e-mails na fila (gravados com a transação de origem) e monta os modelos em português.</summary>
public sealed class EmailService(AppDbContext db)
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string Layout(string title, string bodyHtml) =>
        $"""
        <div style="font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:auto;color:#1f2937">
          <h2 style="color:#0f766e">{E(title)}</h2>
          {bodyHtml}
          <p style="color:#6b7280;font-size:12px;margin-top:32px">Mensagem automática do Sistema Comercial. Não responda este e-mail.</p>
        </div>
        """;

    public void Queue(Guid? tenantId, string to, string subject, string html, string kind) =>
        db.EmailMessages.Add(new EmailMessage { TenantId = tenantId, To = to.Trim(), Subject = subject, HtmlBody = html, Kind = kind });

    public void PasswordReset(Guid tenantId, string to, string name, string tenantCode, string code, int minutes) =>
        Queue(tenantId, to, "Código para redefinir sua senha", Layout("Redefinição de senha",
            $"<p>Olá, {E(name)}.</p><p>Use o código abaixo no sistema (empresa <b>{E(tenantCode)}</b>) para criar uma nova senha:</p>" +
            $"<p style=\"font-size:28px;letter-spacing:6px;font-weight:bold\">{E(code)}</p>" +
            $"<p>Ele vale por {minutes} minutos e só pode ser usado uma vez. Se não foi você, ignore este e-mail — sua senha continua a mesma.</p>"), "password_reset");

    public void Welcome(Guid tenantId, string to, string name, string tenantCode, string tradeName, string createdBy) =>
        Queue(tenantId, to, $"Seu acesso ao sistema de {tradeName}", Layout("Bem-vindo(a)!",
            $"<p>Olá, {E(name)}. {E(createdBy)} criou seu acesso ao sistema da loja <b>{E(tradeName)}</b>.</p>" +
            $"<p>Para entrar: código da empresa <b>{E(tenantCode)}</b>, seu e-mail <b>{E(to)}</b> e a senha que você recebeu de quem criou a conta.</p>" +
            "<p>Esqueceu a senha? Use \"Esqueci minha senha\" na tela de entrada.</p>"), "welcome");

    public void Simple(Guid? tenantId, string to, string subject, string title, string text, string kind) =>
        Queue(tenantId, to, subject, Layout(title, $"<p>{E(text)}</p>"), kind);
}

/// <summary>Envia a fila de e-mails com novas tentativas (1, 5, 15, 60 min; desiste após 6).</summary>
public sealed class EmailSender(AppDbContext db, PlatformSettingsService settings, IEmailTransport transport, TimeProvider clock, ILogger<EmailSender> log)
{
    private static readonly int[] BackoffMinutes = [1, 5, 15, 60, 240];

    public async Task<int> SendPendingAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var batch = await db.EmailMessages.Where(m => m.Status == EmailStatus.Pending && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
            .OrderBy(m => m.CreatedAt).Take(50).ToListAsync(ct);
        if (batch.Count == 0) return 0;
        var cfg = await settings.SmtpAsync();
        var sent = 0;
        foreach (var m in batch)
        {
            m.Attempts++;
            try
            {
                await transport.SendAsync(cfg, m, ct);
                m.Status = EmailStatus.Sent; m.SentAt = clock.GetUtcNow(); m.LastError = null;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                m.LastError = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
                if (m.Attempts > BackoffMinutes.Length) m.Status = EmailStatus.Failed;
                else m.NextAttemptAt = now.AddMinutes(BackoffMinutes[m.Attempts - 1]);
                log.LogWarning("Falha ao enviar e-mail {Kind} (tentativa {N}): {Error}", m.Kind, m.Attempts, m.LastError);
            }
            await db.SaveChangesAsync(ct);
        }
        return sent;
    }
}

public sealed class EmailJob(IServiceProvider sp, IConfiguration config, ILogger<EmailJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("Email:AutoRun", true)) return;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { return; }
            try
            {
                using var scope = sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
                await scope.ServiceProvider.GetRequiredService<EmailSender>().SendPendingAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Falha no envio de e-mails"); }
        }
    }
}

public sealed record ForgotPasswordRequest(string TenantCode, string Email);
public sealed record ResetPasswordWithCodeRequest(string TenantCode, string Email, string Code, string NewPassword);

/// <summary>
/// "Esqueci minha senha": código de 8 caracteres por e-mail (só o hash fica no banco), 30 min, uso único, 5 tentativas.
/// A resposta é sempre a mesma, exista ou não o e-mail — não revela quem tem conta.
/// </summary>
public sealed class PasswordResetService(AppDbContext db, TenantContext tenantCtx, PasswordHasher hasher, AuthService auth, AuditService audit,
    EmailService email, TimeProvider clock)
{
    public const int ValidityMinutes = 30;
    public const int MaxAttempts = 5;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // sem 0/O/1/I

    private static string NewCode() => new(RandomNumberGenerator.GetItems(Alphabet.AsSpan(), 8));
    private static string Hash(Guid userId, string code) => Hashing.Sha256Hex(userId.ToString("N") + ":" + code.Trim().ToUpperInvariant().Replace("-", ""));

    private async Task<(Tenant Tenant, User User)?> FindAsync(string tenantCode, string mail)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Code == (tenantCode ?? "").Trim().ToLower());
        if (tenant is null || tenant.Status == TenantStatus.Cancelled) return null;
        tenantCtx.SetTenant(tenant.Id);
        var e = (mail ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == e);
        return user is null ? null : (tenant, user);
    }

    public async Task RequestAsync(ForgotPasswordRequest r)
    {
        var found = await FindAsync(r.TenantCode, r.Email);
        if (found is null) { hasher.Verify("x", hasher.Hash("y")); return; } // mesmo tempo de resposta
        var (tenant, user) = found.Value;
        if (!user.IsActive) return;
        var now = clock.GetUtcNow();
        // No máximo 3 pedidos por hora por usuário (evita usar o sistema para lotar a caixa de alguém).
        if (await db.PasswordResetTokens.CountAsync(t => t.UserId == user.Id && t.CreatedAt > now.AddHours(-1)) >= 3) return;
        foreach (var old in await db.PasswordResetTokens.Where(t => t.UserId == user.Id && t.UsedAt == null).ToListAsync()) old.UsedAt = now; // invalida códigos anteriores
        var code = NewCode();
        db.PasswordResetTokens.Add(new PasswordResetToken { UserId = user.Id, CodeHash = Hash(user.Id, code), CreatedAt = now, ExpiresAt = now.AddMinutes(ValidityMinutes) });
        email.PasswordReset(tenant.Id, user.Email, user.DisplayName, tenant.Code, code[..4] + "-" + code[4..], ValidityMinutes);
        audit.Add("auth.password_reset_requested", tenant.Id, nameof(User), user.Id);
        await db.SaveChangesAsync();
    }

    public async Task ResetAsync(ResetPasswordWithCodeRequest r)
    {
        var invalid = new DomainException("invalid_code", "Código inválido ou expirado. Peça um novo.", 400);
        if (PasswordHasher.ValidatePolicy(r.NewPassword ?? "") is { } pe) throw new DomainException("weak_password", pe);
        var found = await FindAsync(r.TenantCode, r.Email) ?? throw invalid;
        var (tenant, user) = found;
        var now = clock.GetUtcNow();
        var token = await db.PasswordResetTokens.Where(t => t.UserId == user.Id && t.UsedAt == null && t.ExpiresAt > now)
            .OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync() ?? throw invalid;
        token.Attempts++;
        var ok = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token.CodeHash), Encoding.ASCII.GetBytes(Hash(user.Id, r.Code ?? "")));
        if (!ok)
        {
            if (token.Attempts >= MaxAttempts) token.UsedAt = now; // queima o código após 5 erros
            audit.Add("security.password_reset_failed", tenant.Id, nameof(User), user.Id, new { token.Attempts }, "denied");
            await db.SaveChangesAsync();
            throw invalid;
        }
        token.UsedAt = now;
        user.PasswordHash = hasher.Hash(r.NewPassword!);
        user.FailedLoginCount = 0; user.LockoutUntil = null;
        await auth.RevokeUserSessionsAsync(user.Id, "password_reset");
        audit.Add("auth.password_reset", tenant.Id, nameof(User), user.Id);
        email.Simple(tenant.Id, user.Email, "Sua senha foi alterada", "Senha alterada",
            "A senha da sua conta acabou de ser alterada. Se não foi você, fale imediatamente com o responsável pela loja.", "password_changed");
        await db.SaveChangesAsync();
    }
}

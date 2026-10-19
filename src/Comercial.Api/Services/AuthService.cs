using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Comercial.Shared.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record AdminLoginRequest(string Email, string Password, string TotpCode);

public sealed class AuthService(
    AppDbContext db, TenantContext tenantCtx, PasswordHasher hasher, TokenService tokens, AuditService audit,
    RequestActor actor, IDataProtectionProvider dp, TimeProvider clock, IConfiguration config)
{
    public int MaxFailedAttempts => config.GetValue("Security:MaxFailedLogins", 5);
    public TimeSpan LockoutDuration => TimeSpan.FromMinutes(config.GetValue("Security:LockoutMinutes", 15));
    private static string? _dummyHash;

    private static DomainException Invalid() => new("invalid_credentials", "Credenciais inválidas.", 401);

    public async Task<TokenResponse> LoginAsync(LoginRequest r)
    {
        var now = clock.GetUtcNow();
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Code == (r.TenantCode ?? "").Trim().ToLower());
        if (tenant is null || tenant.Status == TenantStatus.Cancelled) { BurnTime(r.Password); throw Invalid(); }

        tenantCtx.SetTenant(tenant.Id);
        actor.TenantId = tenant.Id;
        var email = (r.Email ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null) { BurnTime(r.Password); throw Invalid(); }

        if (user.LockoutUntil > now)
        {
            await audit.WriteNowAsync("security.login_locked", tenant.Id, nameof(User), user.Id);
            throw new DomainException("account_locked", "Conta temporariamente bloqueada por tentativas inválidas.", 423);
        }
        if (!user.IsActive || !hasher.Verify(r.Password ?? "", user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedAttempts) { user.LockoutUntil = now + LockoutDuration; user.FailedLoginCount = 0; }
            audit.Add("security.login_failed", tenant.Id, nameof(User), user.Id, new { locked = user.LockoutUntil > now }, "denied");
            await db.SaveChangesAsync();
            throw Invalid();
        }

        // Verificação em duas etapas: senha certa + código do autenticador (sem reaproveitar o mesmo código).
        if (user.TotpEnabled && user.TotpSecretProtected is not null)
        {
            if (string.IsNullOrWhiteSpace(r.TotpCode)) throw new DomainException("totp_required", "Digite o código do aplicativo autenticador.", 401);
            var step = Totp.Verify(TotpProtector.Unprotect(user.TotpSecretProtected), r.TotpCode, now);
            if (step is null || step <= user.TotpLastStep)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= MaxFailedAttempts) { user.LockoutUntil = now + LockoutDuration; user.FailedLoginCount = 0; }
                audit.Add("security.login_totp_failed", tenant.Id, nameof(User), user.Id, null, "denied");
                await db.SaveChangesAsync();
                throw new DomainException("invalid_totp", "Código do autenticador incorreto.", 401);
            }
            user.TotpLastStep = step;
        }

        if (r.DeviceId is { } did && !await db.Devices.AnyAsync(d => d.Id == did && d.Status == DeviceStatus.Active))
        {
            await audit.WriteNowAsync("security.login_revoked_device", tenant.Id, nameof(Device), did);
            throw new DomainException("device_not_active", "Dispositivo não ativo para esta empresa.", 401);
        }

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = now;
        var (session, refresh) = NewSession(user, r.DeviceId, Guid.NewGuid(), now);
        actor.Type = ActorType.User; actor.UserId = user.Id;
        audit.Add("auth.login", tenant.Id, nameof(User), user.Id, new { r.DeviceId });
        await db.SaveChangesAsync();
        return new TokenResponse(tokens.IssueTenantAccess(user.Id, tenant.Id, session.Id, r.DeviceId), refresh, tokens.TenantAccessMinutes * 60);
    }

    /// <summary>
    /// Rotação de refresh token. Se um token já utilizado/revogado for reapresentado,
    /// toda a família de sessões é revogada (indício de roubo de token).
    /// </summary>
    public async Task<TokenResponse> RefreshAsync(string refreshToken)
    {
        var now = clock.GetUtcNow();
        var hash = Hashing.Sha256Hex(refreshToken ?? "");
        UserSession? s;
        using (tenantCtx.PlatformLookup())
            s = await db.UserSessions.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.RefreshTokenHash == hash);
        if (s is null) throw Invalid();
        tenantCtx.SetTenant(s.TenantId);
        actor.TenantId = s.TenantId;

        if (s.RevokedAt is not null)
        {
            var family = await db.UserSessions.Where(x => x.FamilyId == s.FamilyId && x.RevokedAt == null).ToListAsync();
            foreach (var f in family) { f.RevokedAt = now; f.RevokedReason = "refresh_reuse_detected"; }
            audit.Add("security.refresh_reuse", s.TenantId, nameof(UserSession), s.FamilyId, new { revoked = family.Count }, "denied");
            await db.SaveChangesAsync();
            throw Invalid();
        }
        if (s.ExpiresAt < now) throw Invalid();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == s.UserId);
        if (user is null || !user.IsActive) throw Invalid();
        if (s.DeviceId is { } did && !await db.Devices.AnyAsync(d => d.Id == did && d.Status == DeviceStatus.Active)) throw Invalid();

        var (next, refresh) = NewSession(user, s.DeviceId, s.FamilyId, now);
        s.RevokedAt = now;
        s.RevokedReason = "rotated";
        s.ReplacedBySessionId = next.Id;
        await db.SaveChangesAsync();
        return new TokenResponse(tokens.IssueTenantAccess(user.Id, user.TenantId, next.Id, s.DeviceId), refresh, tokens.TenantAccessMinutes * 60);
    }

    public async Task LogoutAsync()
    {
        var s = await db.UserSessions.FirstOrDefaultAsync(x => x.Id == actor.SessionId);
        if (s is null) return;
        s.RevokedAt = clock.GetUtcNow();
        s.RevokedReason = "logout";
        audit.Add("auth.logout", actor.TenantId, nameof(UserSession), s.Id);
        await db.SaveChangesAsync();
    }

    public async Task<int> RevokeUserSessionsAsync(Guid userId, string reason)
    {
        var now = clock.GetUtcNow();
        var list = await db.UserSessions.Where(x => x.UserId == userId && x.RevokedAt == null).ToListAsync();
        foreach (var s in list) { s.RevokedAt = now; s.RevokedReason = reason; }
        return list.Count;
    }

    private (UserSession, string) NewSession(User user, Guid? deviceId, Guid family, DateTimeOffset now)
    {
        var refresh = Hashing.NewOpaqueToken();
        var s = new UserSession
        {
            TenantId = user.TenantId, UserId = user.Id, FamilyId = family, DeviceId = deviceId,
            RefreshTokenHash = Hashing.Sha256Hex(refresh), CreatedAt = now, ExpiresAt = now.AddDays(tokens.RefreshDays),
        };
        db.UserSessions.Add(s);
        return (s, refresh);
    }

    /// <summary>Executa um hash para igualar o tempo de resposta quando usuário/empresa não existem.</summary>
    private void BurnTime(string? password)
    {
        _dummyHash ??= hasher.Hash("dummy-password-1");
        hasher.Verify(password ?? "", _dummyHash);
    }

    // ---------------- Administração do SaaS (MFA obrigatório) ----------------

    public IDataProtector TotpProtector => dp.CreateProtector("admin-totp-v1");

    public async Task<string> AdminLoginAsync(AdminLoginRequest r)
    {
        var now = clock.GetUtcNow();
        var admin = await db.AdminUsers.FirstOrDefaultAsync(a => a.Email == (r.Email ?? "").Trim().ToLower());
        if (admin is null) { BurnTime(r.Password); throw Invalid(); }
        if (admin.LockoutUntil > now) throw new DomainException("account_locked", "Conta temporariamente bloqueada.", 423);

        var step = hasher.Verify(r.Password ?? "", admin.PasswordHash) && admin.IsActive
            ? Totp.Verify(TotpProtector.Unprotect(admin.TotpSecretProtected), r.TotpCode, now) : null;
        actor.Type = ActorType.Admin; actor.AdminId = admin.Id;
        if (step is null || step <= admin.LastTotpStep) // código inválido ou reutilizado
        {
            admin.FailedLoginCount++;
            if (admin.FailedLoginCount >= MaxFailedAttempts) { admin.LockoutUntil = now + LockoutDuration; admin.FailedLoginCount = 0; }
            audit.Add("security.admin_login_failed", null, nameof(AdminUser), admin.Id, null, "denied");
            await db.SaveChangesAsync();
            throw Invalid();
        }
        admin.LastTotpStep = step.Value;
        admin.FailedLoginCount = 0;
        admin.LockoutUntil = null;
        audit.Add("admin.login", null, nameof(AdminUser), admin.Id);
        await db.SaveChangesAsync();
        return tokens.IssueAdminAccess(admin.Id, admin.Role);
    }

    /// <summary>Reautenticação para operações críticas: exige código TOTP atual.</summary>
    public async Task RequireAdminReauthAsync(string? totpCode)
    {
        var admin = await db.AdminUsers.AsNoTracking().FirstAsync(a => a.Id == actor.AdminId);
        if (Totp.Verify(TotpProtector.Unprotect(admin.TotpSecretProtected), totpCode, clock.GetUtcNow()) is null)
        {
            await audit.WriteNowAsync("security.admin_reauth_failed", null, nameof(AdminUser), admin.Id);
            throw new DomainException("reauth_required", "Confirme com o código do autenticador (MFA).", 401);
        }
    }
}

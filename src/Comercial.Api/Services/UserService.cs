using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Modules;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record CreateUserRequest(string Email, string DisplayName, string Password, List<string> Roles, Guid? StoreId);
public sealed record SetRolesRequest(List<string> Roles);
public sealed record PermissionGrantInput(string Permission, bool Granted);
public sealed record MobileAccessRequest(bool Allowed);
public sealed record CreateDeviceRequest(string Name, string Platform, Guid? StoreId);
public sealed record ResetPasswordRequest(string NewPassword);

public sealed class UserService(AppDbContext db, RequestActor actor, AuditService audit, PasswordHasher hasher, AuthService auth, FeatureAuthorization features,
    EmailService mailer)
{
    private void EnsureCanAssign(IEnumerable<string> roles)
    {
        foreach (var r in roles)
        {
            if (!Roles.All.Contains(r)) throw new DomainException("invalid_role", $"Papel inválido: {r}");
            // Impede escalada: ninguém concede papel acima do próprio; "owner" só por owner.
            if (Roles.Level(r) > actor.MaxRoleLevel || (r == Roles.Owner && !actor.Roles.Contains(Roles.Owner)))
                throw new DomainException("privilege_escalation", "Não é permitido conceder papel superior ao seu.", 403);
        }
    }

    private async Task<User> GetManageableAsync(Guid id)
    {
        var u = await db.Users.Include(x => x.Roles).FirstOrDefaultAsync(x => x.Id == id) ?? throw new DomainException("not_found", "Usuário não encontrado.", 404);
        if (u.Roles.Count > 0 && u.Roles.Max(r => Roles.Level(r.Role)) > actor.MaxRoleLevel)
            throw new DomainException("privilege_escalation", "Não é permitido gerenciar usuário de nível superior.", 403);
        return u;
    }

    public async Task<User> CreateAsync(CreateUserRequest r)
    {
        EnsureCanAssign(r.Roles);
        if (PasswordHasher.ValidatePolicy(r.Password) is { } pe) throw new DomainException("weak_password", pe);
        var email = r.Email.Trim().ToLowerInvariant();
        if (!email.Contains('@') || email.Length > 200) throw new DomainException("invalid_email", "E-mail inválido.");
        if (await db.Users.AnyAsync(u => u.Email == email)) throw new DomainException("email_taken", "E-mail já cadastrado.", 409);
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Status != SubscriptionStatus.Cancelled);
        if (sub is not null && await db.Users.CountAsync(u => u.IsActive) >= sub.MaxUsers)
            throw new DomainException("user_limit", "Limite de usuários do plano atingido.", 409);
        if (r.StoreId is { } sid && !await db.Stores.AnyAsync(s => s.Id == sid)) throw new DomainException("store_not_found", "Loja não encontrada.", 404);

        var u = new User { Email = email, DisplayName = r.DisplayName.Trim(), PasswordHash = hasher.Hash(r.Password), StoreId = r.StoreId };
        foreach (var role in r.Roles.Distinct()) u.Roles.Add(new UserRole { UserId = u.Id, Role = role });
        db.Users.Add(u);
        audit.Add("user.created", actor.TenantId, nameof(User), u.Id, new { email, r.Roles, r.StoreId });
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == actor.TenantId);
        var creator = await db.Users.AsNoTracking().Where(x => x.Id == actor.UserId).Select(x => x.DisplayName).FirstOrDefaultAsync() ?? "O responsável pela loja";
        mailer.Welcome(actor.TenantId, email, u.DisplayName, tenant.Code, tenant.TradeName, creator);
        await db.SaveChangesAsync();
        return u;
    }

    public async Task SetRolesAsync(Guid id, SetRolesRequest r)
    {
        EnsureCanAssign(r.Roles);
        var u = await GetManageableAsync(id);
        if (u.Id == actor.UserId) throw new DomainException("self_change", "Não é permitido alterar os próprios papéis.", 403);
        var before = u.Roles.Select(x => x.Role).ToList();
        db.UserRoles.RemoveRange(u.Roles);
        foreach (var role in r.Roles.Distinct()) db.UserRoles.Add(new UserRole { UserId = u.Id, Role = role });
        audit.Add("user.roles_changed", actor.TenantId, nameof(User), u.Id, new { before, after = r.Roles });
        await db.SaveChangesAsync();
    }

    public async Task SetPermissionsAsync(Guid id, List<PermissionGrantInput> grants)
    {
        if (!(await features.ModulesAsync()).Contains(ModuleKeys.UsersAdvanced))
            throw new DomainException("module_not_enabled", "Permissões individuais exigem o módulo Usuários e Permissões Avançadas.", 403);
        var u = await GetManageableAsync(id);
        foreach (var g in grants)
        {
            if (!Permissions.All.Contains(g.Permission)) throw new DomainException("invalid_permission", $"Permissão inválida: {g.Permission}");
            if (g.Granted && !actor.Permissions.Contains(g.Permission))
                throw new DomainException("privilege_escalation", "Não é permitido conceder permissão que você não possui.", 403);
        }
        db.UserPermissionGrants.RemoveRange(db.UserPermissionGrants.Where(x => x.UserId == u.Id));
        foreach (var g in grants) db.UserPermissionGrants.Add(new UserPermissionGrant { UserId = u.Id, Permission = g.Permission, Granted = g.Granted });
        audit.Add("user.permissions_changed", actor.TenantId, nameof(User), u.Id, grants);
        await db.SaveChangesAsync();
    }

    /// <summary>Liga/desliga só a permissão do app do celular, mantendo as demais permissões do funcionário.</summary>
    public async Task SetMobileAccessAsync(Guid id, bool allowed)
    {
        var u = await GetManageableAsync(id);
        if (u.Id == actor.UserId) throw new DomainException("self_change", "Não é possível alterar o próprio acesso.", 403);
        if (allowed && !actor.Permissions.Contains(Permissions.MobileAccess))
            throw new DomainException("privilege_escalation", "Não é permitido conceder permissão que você não possui.", 403);
        db.UserPermissionGrants.RemoveRange(db.UserPermissionGrants.Where(x => x.UserId == u.Id && x.Permission == Permissions.MobileAccess));
        db.UserPermissionGrants.Add(new UserPermissionGrant { UserId = u.Id, Permission = Permissions.MobileAccess, Granted = allowed });
        audit.Add(allowed ? "user.mobile_access_granted" : "user.mobile_access_revoked", actor.TenantId, nameof(User), u.Id);
        await db.SaveChangesAsync();
    }

    public async Task SetActiveAsync(Guid id, bool active)
    {
        var u = await GetManageableAsync(id);
        if (u.Id == actor.UserId) throw new DomainException("self_change", "Não é permitido bloquear a si mesmo.", 403);
        u.IsActive = active;
        if (!active) await auth.RevokeUserSessionsAsync(u.Id, "user_blocked");
        audit.Add(active ? "user.reactivated" : "user.blocked", actor.TenantId, nameof(User), u.Id);
        await db.SaveChangesAsync();
    }

    /// <summary>Gestor define nova senha para o funcionário (ex.: esqueceu). Encerra as sessões abertas dele.</summary>
    public async Task ResetPasswordAsync(Guid id, ResetPasswordRequest r)
    {
        var u = await GetManageableAsync(id);
        if (u.Id == actor.UserId) throw new DomainException("self_change", "Para trocar a própria senha use a opção de perfil.", 403);
        if (PasswordHasher.ValidatePolicy(r.NewPassword) is { } pe) throw new DomainException("weak_password", pe);
        u.PasswordHash = hasher.Hash(r.NewPassword);
        u.FailedLoginCount = 0; u.LockoutUntil = null;
        await auth.RevokeUserSessionsAsync(u.Id, "password_reset_by_manager");
        audit.Add("user.password_reset", actor.TenantId, nameof(User), u.Id);
        await db.SaveChangesAsync();
    }

    public async Task<int> RevokeSessionsAsync(Guid id)
    {
        var u = await GetManageableAsync(id);
        var n = await auth.RevokeUserSessionsAsync(u.Id, "revoked_by_manager");
        audit.Add("user.sessions_revoked", actor.TenantId, nameof(User), u.Id, new { n });
        await db.SaveChangesAsync();
        return n;
    }

    public async Task<Device> ActivateDeviceAsync(CreateDeviceRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 80) throw new DomainException("invalid_name", "Nome do dispositivo inválido.");
        if (r.Platform is not ("windows" or "android" or "ios")) throw new DomainException("invalid_platform", "Plataforma inválida.");
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Status != SubscriptionStatus.Cancelled);
        var active = await db.Devices.CountAsync(d => d.Status == DeviceStatus.Active);
        if (sub is not null && active >= sub.MaxDevices) throw new DomainException("device_limit", "Limite de dispositivos do plano atingido.", 409);
        var d = new Device { Name = r.Name.Trim(), Platform = r.Platform, StoreId = r.StoreId };
        db.Devices.Add(d);
        audit.Add("device.activated", actor.TenantId, nameof(Device), d.Id, new { r.Name, r.Platform });
        await db.SaveChangesAsync();
        return d;
    }

    /// <summary>Revogação é definitiva para o Id: reativar exige cadastrar novo dispositivo.</summary>
    public async Task RevokeDeviceAsync(Guid id)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == id) ?? throw new DomainException("not_found", "Dispositivo não encontrado.", 404);
        if (d.Status == DeviceStatus.Revoked) return;
        d.Status = DeviceStatus.Revoked;
        d.RevokedAt = DateTimeOffset.UtcNow;
        var sessions = await db.UserSessions.Where(s => s.DeviceId == id && s.RevokedAt == null).ToListAsync();
        foreach (var s in sessions) { s.RevokedAt = DateTimeOffset.UtcNow; s.RevokedReason = "device_revoked"; }
        audit.Add("device.revoked", actor.TenantId, nameof(Device), d.Id, new { sessions = sessions.Count });
        await db.SaveChangesAsync();
    }
}

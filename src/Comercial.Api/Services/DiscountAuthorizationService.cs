using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record DiscountAuthorizationRequest(string ApproverEmail, string ApproverPassword, long DiscountCents, long SubtotalCents, string Reason);
public sealed record DiscountAuthorizationResponse(Guid Id, long MaxDiscountCents, DateTimeOffset ExpiresAt, string ApproverName);

/// <summary>
/// O proprietário (ou quem tiver sales.discount.authorize) digita e-mail e senha no PDV do operador para liberar
/// um desconto no total. Senha errada conta para o bloqueio da conta, como no login. Tudo auditado.
/// </summary>
public sealed class DiscountAuthorizationService(AppDbContext db, RequestActor actor, AuditService audit, PasswordHasher hasher, AuthService auth,
    TimeProvider clock)
{
    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(15);

    public async Task<DiscountAuthorizationResponse> AuthorizeAsync(DiscountAuthorizationRequest r)
    {
        if (r.SubtotalCents <= 0 || r.DiscountCents <= 0 || r.DiscountCents > r.SubtotalCents)
            throw new DomainException("invalid_discount", "Desconto inválido.");
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 3 || r.Reason.Length > 200)
            throw new DomainException("reason_required", "Informe o motivo do desconto.");
        var now = clock.GetUtcNow();
        var email = (r.ApproverEmail ?? "").Trim().ToLowerInvariant();
        var approver = await db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Email == email); // filtrado pela empresa
        var invalid = new DomainException("invalid_credentials", "E-mail ou senha do autorizador inválidos.", 401);
        if (approver is null) { hasher.Verify(r.ApproverPassword ?? "", hasher.Hash("x")); throw invalid; }
        if (approver.LockoutUntil > now) throw new DomainException("account_locked", "Conta do autorizador bloqueada temporariamente.", 423);
        if (!approver.IsActive || !hasher.Verify(r.ApproverPassword ?? "", approver.PasswordHash))
        {
            approver.FailedLoginCount++;
            if (approver.FailedLoginCount >= auth.MaxFailedAttempts) { approver.LockoutUntil = now + auth.LockoutDuration; approver.FailedLoginCount = 0; }
            audit.Add("security.discount_authorization_failed", actor.TenantId, nameof(User), approver.Id, new { requestedBy = actor.UserId, r.DiscountCents }, "denied");
            await db.SaveChangesAsync();
            throw invalid;
        }
        approver.FailedLoginCount = 0;
        var grants = await db.UserPermissionGrants.AsNoTracking().Where(g => g.UserId == approver.Id).Select(g => new { g.Permission, g.Granted }).ToListAsync();
        var perms = Permissions.Resolve(approver.Roles.Select(x => x.Role), grants.Select(g => (g.Permission, g.Granted)));
        if (!perms.Contains(Permissions.SalesDiscountAuthorize))
        {
            audit.Add("security.discount_authorization_denied", actor.TenantId, nameof(User), approver.Id, new { requestedBy = actor.UserId, r.DiscountCents }, "denied");
            await db.SaveChangesAsync();
            throw new DomainException("approver_not_allowed", "Este usuário não pode autorizar descontos. Peça ao proprietário.", 403);
        }
        var a = new DiscountAuthorization
        {
            ApproverUserId = approver.Id, RequestedByUserId = actor.UserId!.Value, MaxDiscountCents = r.DiscountCents, SubtotalCents = r.SubtotalCents,
            Reason = r.Reason.Trim(), CreatedAt = now, ExpiresAt = now + Validity,
        };
        db.DiscountAuthorizations.Add(a);
        audit.Add("sale.discount_authorized", actor.TenantId, nameof(DiscountAuthorization), a.Id,
            new { approver = approver.Id, requestedBy = actor.UserId, r.DiscountCents, r.SubtotalCents, percent = Math.Round(r.DiscountCents * 100m / r.SubtotalCents, 1), reason = a.Reason });
        await db.SaveChangesAsync();
        return new DiscountAuthorizationResponse(a.Id, a.MaxDiscountCents, a.ExpiresAt, approver.DisplayName);
    }
}

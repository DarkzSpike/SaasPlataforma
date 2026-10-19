using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

/// <summary>TotpCode (MFA) é necessário para usar a pré-autorização do proprietário (alterações sem pedir aprovação).</summary>
public sealed record StartSupportRequest(string Reason, int DurationMinutes, string? TotpCode = null);
public sealed record SupportPreAuthorizationRequest(List<string> Operations);
public sealed record ElevationRequestInput(string Reason, List<string> Operations, int DurationMinutes, string TotpCode);

/// <summary>
/// Sessões de suporte por empresa: consulta por padrão, com prazo; elevação exige motivo,
/// MFA do administrador e aprovação do proprietário da empresa; tudo auditado e revogável.
/// </summary>
public sealed class SupportService(AppDbContext db, RequestActor actor, AuditService audit, AuthService auth, TimeProvider clock, NotificationService notifications)
{
    public const int MaxSessionMinutes = 120;
    public const int MaxElevationMinutes = 30;

    public async Task<SupportSession> StartAsync(Guid tenantId, StartSupportRequest r)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId) ?? throw new DomainException("tenant_not_found", "Empresa não encontrada.", 404);
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length < 10) throw new DomainException("reason_required", "Motivo obrigatório (mínimo 10 caracteres).");
        if (r.DurationMinutes is < 5 or > MaxSessionMinutes) throw new DomainException("invalid_duration", $"Duração entre 5 e {MaxSessionMinutes} minutos.");
        var now = clock.GetUtcNow();
        var s = new SupportSession { TenantId = tenantId, AdminUserId = actor.AdminId!.Value, Reason = r.Reason, StartedAt = now, ExpiresAt = now.AddMinutes(r.DurationMinutes) };

        // Pré-autorização do proprietário + MFA do administrador: a sessão já começa com as alterações consentidas.
        var preauth = PreAuthorized(tenant);
        if (preauth.Count > 0 && !string.IsNullOrWhiteSpace(r.TotpCode))
        {
            await auth.RequireAdminReauthAsync(r.TotpCode);
            s.Mode = SupportMode.Elevated;
            s.AllowedOperations = string.Join(",", preauth);
            s.ElevationReason = "pré-autorizado pelo proprietário";
            s.ApprovedByTenantUserId = tenant.SupportPreAuthorizedByUserId;
            s.ElevatedUntil = s.ExpiresAt;
        }
        db.SupportSessions.Add(s);
        audit.Add("support.session_started", tenantId, nameof(SupportSession), s.Id, new { r.Reason, r.DurationMinutes, elevated = s.Mode == SupportMode.Elevated, s.AllowedOperations });
        await db.SaveChangesAsync();
        var scope = s.Mode == SupportMode.Elevated ? "Pode alterar: " + Describe(preauth) + " (autorização prévia sua). " : "Somente consulta. ";
        await notifications.CreateAsync(tenantId, "support.session_started", "Suporte técnico acessou sua loja",
            "Motivo: " + r.Reason + ". " + scope + "Acesso até " + s.ExpiresAt.ToOffset(TimeSpan.FromHours(-3)).ToString("HH:mm") + ". Você pode encerrar a qualquer momento em Suporte.",
            new { sessionId = s.Id });
        return s;
    }

    public async Task<SupportElevationRequest> RequestElevationAsync(Guid sessionId, ElevationRequestInput r)
    {
        await auth.RequireAdminReauthAsync(r.TotpCode);
        var s = await OwnActive(sessionId);
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length < 10) throw new DomainException("reason_required", "Motivo obrigatório.");
        if (r.Operations.Count == 0 || r.Operations.Any(o => !FeatureAuthorization.SupportElevatable.Contains(o)))
            throw new DomainException("operation_not_elevatable", "Operação não pode ser liberada ao suporte.");
        if (r.DurationMinutes is < 1 or > MaxElevationMinutes) throw new DomainException("invalid_duration", $"Elevação de até {MaxElevationMinutes} minutos.");
        var req = new SupportElevationRequest
        {
            SupportSessionId = s.Id, TenantId = s.TenantId, Reason = r.Reason, RequestedOperations = string.Join(",", r.Operations.Distinct()), DurationMinutes = r.DurationMinutes,
        };
        db.SupportElevationRequests.Add(req);
        audit.Add("support.elevation_requested", s.TenantId, nameof(SupportSession), s.Id, new { r.Reason, r.Operations, r.DurationMinutes });
        await db.SaveChangesAsync();
        await notifications.CreateAsync(s.TenantId, "support.elevation_requested", "Suporte pede permissão para alterar sua loja",
            "Pedido: " + Describe(r.Operations) + " por " + r.DurationMinutes + " min. Motivo: " + r.Reason + ". Aprove ou recuse em Suporte.",
            new { requestId = req.Id, sessionId = s.Id });
        return req;
    }

    /// <summary>Chamado pelo proprietário/usuário com permissão support.approve da empresa.</summary>
    public async Task<SupportSession> DecideElevationAsync(Guid requestId, bool approve)
    {
        var req = await db.SupportElevationRequests.FirstOrDefaultAsync(x => x.Id == requestId && x.TenantId == actor.TenantId)
                  ?? throw new DomainException("not_found", "Solicitação não encontrada.", 404);
        if (req.DecidedAt is not null) throw new DomainException("already_decided", "Solicitação já decidida.", 409);
        var s = await db.SupportSessions.FirstAsync(x => x.Id == req.SupportSessionId && x.TenantId == actor.TenantId);
        var now = clock.GetUtcNow();
        if (!s.IsActive(now)) throw new DomainException("session_inactive", "Sessão de suporte encerrada.", 409);

        req.DecidedAt = now; req.Approved = approve; req.DecidedByUserId = actor.UserId;
        if (approve)
        {
            s.Mode = SupportMode.Elevated;
            s.AllowedOperations = req.RequestedOperations;
            s.ElevationReason = req.Reason;
            s.ApprovedByTenantUserId = actor.UserId;
            var until = now.AddMinutes(req.DurationMinutes);
            s.ElevatedUntil = until < s.ExpiresAt ? until : s.ExpiresAt;
        }
        audit.Add(approve ? "support.elevation_approved" : "support.elevation_denied", s.TenantId, nameof(SupportSession), s.Id, new { req.RequestedOperations });
        await db.SaveChangesAsync();
        return s;
    }

    public async Task EndAsync(Guid sessionId, string reason, bool byTenant)
    {
        var s = byTenant
            ? await db.SupportSessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.TenantId == actor.TenantId)
            : await db.SupportSessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.AdminUserId == actor.AdminId);
        if (s is null) throw new DomainException("not_found", "Sessão não encontrada.", 404);
        if (s.EndedAt is not null) return;
        s.EndedAt = clock.GetUtcNow();
        s.EndedReason = reason;
        audit.Add(byTenant ? "support.session_revoked_by_tenant" : "support.session_ended", s.TenantId, nameof(SupportSession), s.Id, new { reason });
        await db.SaveChangesAsync();
    }

    public static readonly Dictionary<string, string> OperationNames = new()
    {
        [Permissions.ProductsWrite] = "cadastro de produtos", [Permissions.StockAdjust] = "ajuste de estoque",
        [Permissions.UsersManage] = "usuários", [Permissions.DevicesManage] = "terminais", [Permissions.SalesRead] = "consulta de vendas",
    };
    private static string Describe(IEnumerable<string> ops) => string.Join(", ", ops.Select(o => OperationNames.GetValueOrDefault(o, o)));

    private static List<string> PreAuthorized(Tenant t) => (t.SupportPreAuthorizedOperations ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries).Where(FeatureAuthorization.SupportElevatable.Contains).Distinct().ToList();

    /// <summary>Proprietário define o que o suporte pode alterar sem pedir aprovação (lista vazia = sempre pedir).</summary>
    public async Task<object> SetPreAuthorizationAsync(SupportPreAuthorizationRequest r)
    {
        var ops = (r.Operations ?? []).Distinct().ToList();
        if (ops.Any(o => !FeatureAuthorization.SupportElevatable.Contains(o))) throw new DomainException("operation_not_elevatable", "Operação não pode ser liberada ao suporte.");
        var t = await db.Tenants.FirstAsync(x => x.Id == actor.TenantId);
        t.SupportPreAuthorizedOperations = ops.Count == 0 ? null : string.Join(",", ops);
        t.SupportPreAuthorizedAt = ops.Count == 0 ? null : clock.GetUtcNow();
        t.SupportPreAuthorizedByUserId = ops.Count == 0 ? null : actor.UserId;
        audit.Add("support.preauthorization_changed", t.Id, nameof(Tenant), t.Id, new { operations = ops });
        await db.SaveChangesAsync();
        return GetPreAuthorization(t);
    }

    public static object GetPreAuthorization(Tenant t) => new
    {
        operations = PreAuthorized(t), at = t.SupportPreAuthorizedAt,
        available = FeatureAuthorization.SupportElevatable.Select(o => new { key = o, name = OperationNames.GetValueOrDefault(o, o) }),
    };

    private async Task<SupportSession> OwnActive(Guid id)
    {
        var s = await db.SupportSessions.FirstOrDefaultAsync(x => x.Id == id && x.AdminUserId == actor.AdminId);
        if (s is null || !s.IsActive(clock.GetUtcNow())) throw new DomainException("session_inactive", "Sessão de suporte inexistente ou expirada.", 409);
        return s;
    }
}

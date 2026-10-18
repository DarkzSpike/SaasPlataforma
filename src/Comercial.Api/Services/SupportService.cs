using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record StartSupportRequest(string Reason, int DurationMinutes);
public sealed record ElevationRequestInput(string Reason, List<string> Operations, int DurationMinutes, string TotpCode);

/// <summary>
/// Sessões de suporte por empresa: consulta por padrão, com prazo; elevação exige motivo,
/// MFA do administrador e aprovação do proprietário da empresa; tudo auditado e revogável.
/// </summary>
public sealed class SupportService(AppDbContext db, RequestActor actor, AuditService audit, AuthService auth, TimeProvider clock)
{
    public const int MaxSessionMinutes = 120;
    public const int MaxElevationMinutes = 30;

    public async Task<SupportSession> StartAsync(Guid tenantId, StartSupportRequest r)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId)) throw new DomainException("tenant_not_found", "Empresa não encontrada.", 404);
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length < 10) throw new DomainException("reason_required", "Motivo obrigatório (mínimo 10 caracteres).");
        if (r.DurationMinutes is < 5 or > MaxSessionMinutes) throw new DomainException("invalid_duration", $"Duração entre 5 e {MaxSessionMinutes} minutos.");
        var now = clock.GetUtcNow();
        var s = new SupportSession { TenantId = tenantId, AdminUserId = actor.AdminId!.Value, Reason = r.Reason, StartedAt = now, ExpiresAt = now.AddMinutes(r.DurationMinutes) };
        db.SupportSessions.Add(s);
        audit.Add("support.session_started", tenantId, nameof(SupportSession), s.Id, new { r.Reason, r.DurationMinutes });
        await db.SaveChangesAsync();
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

    private async Task<SupportSession> OwnActive(Guid id)
    {
        var s = await db.SupportSessions.FirstOrDefaultAsync(x => x.Id == id && x.AdminUserId == actor.AdminId);
        if (s is null || !s.IsActive(clock.GetUtcNow())) throw new DomainException("session_inactive", "Sessão de suporte inexistente ou expirada.", 409);
        return s;
    }
}

using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Comercial.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record OperatingCorrectionRequest(string IdempotencyKey, int CorrectedType, DateTimeOffset CorrectedLocalTime, string Justification);

public sealed record OperatingStatus(Guid StoreId, string State, DateTimeOffset? LastEventLocalTime, DateTimeOffset? LastEventReceivedAt,
    Guid? ResponsibleUserId, bool ClockSuspect, string Note);

/// <summary>Horários operacionais da loja — separados das sessões financeiras de caixa.</summary>
public sealed class OperatingService(AppDbContext db, RequestActor actor, AuditService audit, StockService stock, ScopedLookup lookup, TimeProvider clock)
{
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    public async Task<(OperatingEvent Event, bool Duplicate)> RecordAsync(OperatingEventRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.IdempotencyKey) || r.IdempotencyKey.Length > 80) throw new DomainException("idempotency_key_required", "Chave obrigatória.");
        if (r.Type is not ((int)OperatingEventType.Open or (int)OperatingEventType.Close)) throw new DomainException("invalid_type", "Tipo inválido.");
        var dup = await db.OperatingEvents.AsNoTracking().FirstOrDefaultAsync(e => e.IdempotencyKey == r.IdempotencyKey);
        if (dup is not null) return (dup, true);
        await stock.EnsureStoreAccessAsync(r.StoreId);
        // Regra: a loja só fecha depois que todos os caixas dela forem fechados.
        if (r.Type == (int)OperatingEventType.Close && await db.CashSessions.AnyAsync(c => c.StoreId == r.StoreId && c.Status == CashSessionStatus.Open))
            throw new DomainException("cash_open", "Feche todos os caixas desta loja antes de fechar a loja.", 409);

        var now = clock.GetUtcNow();
        // Online: horário local deve estar próximo do servidor. Offline: não pode estar no futuro.
        var suspect = r.Offline ? r.LocalTime > now + ClockTolerance : (r.LocalTime - now).Duration() > ClockTolerance;
        var e = new OperatingEvent
        {
            StoreId = r.StoreId, Type = (OperatingEventType)r.Type, LocalTime = r.LocalTime, ReceivedAt = now,
            UserId = actor.UserId!.Value, DeviceId = actor.DeviceId ?? r.DeviceId, Offline = r.Offline, ClockSuspect = suspect,
            IdempotencyKey = r.IdempotencyKey,
        };
        db.OperatingEvents.Add(e);
        audit.Add(e.Type == OperatingEventType.Open ? "store.opened" : "store.closed", actor.TenantId, nameof(OperatingEvent), e.Id,
            new { r.StoreId, r.LocalTime, r.Offline, suspect });
        await db.SaveChangesAsync();
        return (e, false);
    }

    /// <summary>Correção: preserva o evento original e cria um novo evento auditável.</summary>
    public async Task<OperatingEvent> CorrectAsync(Guid eventId, OperatingCorrectionRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Justification) || r.Justification.Length < 10) throw new DomainException("justification_required", "Justificativa obrigatória (mínimo 10 caracteres).");
        if (r.CorrectedType is not ((int)OperatingEventType.Open or (int)OperatingEventType.Close)) throw new DomainException("invalid_type", "Tipo inválido.");
        var original = await lookup.GetAsync<OperatingEvent>(eventId);
        if (original.Type == OperatingEventType.Correction) throw new DomainException("invalid_target", "Corrija o evento original.");
        var c = new OperatingEvent
        {
            StoreId = original.StoreId, Type = OperatingEventType.Correction, LocalTime = clock.GetUtcNow(), UserId = actor.UserId!.Value,
            DeviceId = actor.DeviceId, CorrectsEventId = original.Id, CorrectedType = (OperatingEventType)r.CorrectedType,
            CorrectedLocalTime = r.CorrectedLocalTime, Justification = r.Justification, IdempotencyKey = r.IdempotencyKey,
        };
        db.OperatingEvents.Add(c);
        audit.Add("store.event_corrected", actor.TenantId, nameof(OperatingEvent), original.Id, new { r.CorrectedType, r.CorrectedLocalTime, r.Justification });
        await db.SaveChangesAsync();
        return c;
    }

    public async Task<OperatingStatus> StatusAsync(Guid storeId)
    {
        await stock.EnsureStoreAccessAsync(storeId);
        var events = await db.OperatingEvents.AsNoTracking().Where(e => e.StoreId == storeId).ToListAsync();
        var corrections = events.Where(e => e.Type == OperatingEventType.Correction).GroupBy(e => e.CorrectsEventId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.ReceivedAt).Last());
        var effective = events.Where(e => e.Type != OperatingEventType.Correction)
            .Select(e => corrections.TryGetValue(e.Id, out var c)
                ? (Type: c.CorrectedType!.Value, Local: c.CorrectedLocalTime!.Value, e.ReceivedAt, e.UserId, e.ClockSuspect)
                : (Type: e.Type, Local: e.LocalTime, e.ReceivedAt, e.UserId, e.ClockSuspect))
            .OrderBy(e => e.Local).ToList();
        if (effective.Count == 0)
            return new OperatingStatus(storeId, "unknown", null, null, null, false, "Nenhum evento recebido pelo servidor.");
        var last = effective[^1];
        return new OperatingStatus(storeId, last.Type == OperatingEventType.Open ? "open" : "closed", last.Local, last.ReceivedAt, last.UserId, last.ClockSuspect,
            "Estado baseado nos eventos recebidos pelo servidor até " + last.ReceivedAt.ToString("u") + ". Eventos offline ainda não sincronizados não aparecem.");
    }
}

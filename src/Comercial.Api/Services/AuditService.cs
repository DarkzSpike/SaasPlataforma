using System.Text.Json;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed class AuditService(AppDbContext db, RequestActor actor)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Adiciona registro ao contexto; é gravado (com hash encadeado) no próximo SaveChanges.</summary>
    public void Add(string action, Guid? tenantId, string? entityType = null, object? entityId = null, object? data = null, string outcome = "ok")
    {
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorType = actor.Type,
            ActorId = actor.ActorId,
            SupportSessionId = actor.Support?.Id,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            DataJson = data is null ? null : JsonSerializer.Serialize(data, Json),
            Outcome = outcome,
            IpAddress = actor.Ip,
            OccurredAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>Grava imediatamente (usado para eventos de segurança mesmo quando a operação falha).</summary>
    public async Task WriteNowAsync(string action, Guid? tenantId, string? entityType = null, object? entityId = null, object? data = null, string outcome = "denied")
    {
        // Contexto separado do restante da operação para não gravar alterações parciais.
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.State != EntityState.Unchanged && e.Entity is not AuditLog).ToList())
            e.State = e.State == EntityState.Added ? EntityState.Detached : EntityState.Unchanged;
        Add(action, tenantId, entityType, entityId, data, outcome);
        await db.SaveChangesAsync();
    }

    /// <summary>Verifica a cadeia de hashes. Retorna o Id do primeiro registro inconsistente, ou null.</summary>
    public static async Task<(bool Ok, long? BrokenAt, int Count)> VerifyChainAsync(AppDbContext db)
    {
        var prev = "GENESIS";
        var count = 0;
        await foreach (var a in db.AuditLogs.AsNoTracking().OrderBy(a => a.Id).AsAsyncEnumerable())
        {
            count++;
            if (a.PreviousHash != prev || AppDbContext.ComputeAuditHash(a) != a.Hash) return (false, a.Id, count);
            prev = a.Hash;
        }
        return (true, null, count);
    }
}

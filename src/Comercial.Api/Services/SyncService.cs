using System.Text.Json;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Modules;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Comercial.Shared.Contracts;
using Comercial.Shared.Licensing;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

/// <summary>
/// Recebe a fila offline do dispositivo. Cada operação é validada no servidor como se fosse online,
/// processada de forma idempotente (dispositivo + chave) e o resultado é guardado para reenvios.
/// </summary>
public sealed class SyncService(
    AppDbContext db, RequestActor actor, IServiceProvider sp, FeatureAuthorization features,
    LicenseService license, TimeProvider clock, ILogger<SyncService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxBatch = 200;

    public async Task<SyncBatchResponse> ProcessAsync(SyncBatchRequest req)
    {
        if (actor.DeviceId is null || actor.DeviceId != req.DeviceId)
            throw new DomainException("device_mismatch", "Token não pertence a este dispositivo.", 403);
        if (req.Operations.Count > MaxBatch) throw new DomainException("batch_too_large", $"Máximo de {MaxBatch} operações por lote.");

        var now = clock.GetUtcNow();
        var skew = (req.DeviceClock - now).Duration() > OperatingService.ClockTolerance;
        var (_, sub, state) = await license.GetStateAsync(actor.TenantId);
        DateTimeOffset? licensedUntil = LicenseStates.AllowsNewOperations(state) ? null
            : state == LicenseStates.Restricted && sub is not null ? sub.CurrentPeriodEnd.AddDays(sub.GraceDays) : DateTimeOffset.MinValue;

        var results = new List<SyncOperationResult>();
        foreach (var op in req.Operations)
            results.Add(await ProcessOneAsync(req.DeviceId, op, now, licensedUntil));

        var device = await db.Devices.FirstAsync(d => d.Id == req.DeviceId);
        device.LastSyncAt = now;
        device.LastSeenAt = now;
        device.LastError = results.FirstOrDefault(r => r.Status == SyncStatus.Rejected)?.Error;
        await db.SaveChangesAsync();
        return new SyncBatchResponse(now, skew, results);
    }

    private async Task<SyncOperationResult> ProcessOneAsync(Guid deviceId, SyncOperation op, DateTimeOffset now, DateTimeOffset? licensedUntil)
    {
        if (string.IsNullOrWhiteSpace(op.IdempotencyKey) || op.IdempotencyKey.Length > 80)
            return new(op.IdempotencyKey, SyncStatus.Rejected, "idempotency_key_required", null);

        var prior = await db.SyncEvents.AsNoTracking().FirstOrDefaultAsync(e => e.DeviceId == deviceId && e.IdempotencyKey == op.IdempotencyKey);
        if (prior is not null)
            return new(op.IdempotencyKey, SyncStatus.Duplicate, prior.Status == SyncStatus.Rejected ? prior.ResultJson : null,
                prior.Status == SyncStatus.Applied && prior.ResultJson is not null ? JsonSerializer.Deserialize<JsonElement>(prior.ResultJson) : null);

        string status; string? error = null; object? result = null;
        try
        {
            if (op.LocalTime > now + OperatingService.ClockTolerance)
                throw new DomainException("clock_in_future", "Horário local no futuro: relógio do dispositivo inconsistente.");
            if (licensedUntil is { } lu && op.LocalTime > lu)
                throw new DomainException("license_restricted", "Operação registrada após o fim da validade da licença.");

            switch (op.Type)
            {
                case SyncTypes.SaleCreate:
                {
                    await RequireAsync(ModuleKeys.Pdv, Permissions.SalesCreate);
                    var r = op.Payload.Deserialize<CreateSaleRequest>(Json) ?? throw new DomainException("invalid_payload", "Payload inválido.");
                    if (r.IdempotencyKey != op.IdempotencyKey) throw new DomainException("key_mismatch", "Chave da operação difere da chave da venda.");
                    result = await sp.GetRequiredService<SalesService>().CreateAsync(r with { CreatedOffline = true, DeviceId = deviceId });
                    break;
                }
                case SyncTypes.OperatingEvent:
                {
                    await RequireAsync(ModuleKeys.StoreHours, Permissions.OperatingOpenClose);
                    var r = op.Payload.Deserialize<OperatingEventRequest>(Json) ?? throw new DomainException("invalid_payload", "Payload inválido.");
                    if (r.IdempotencyKey != op.IdempotencyKey) throw new DomainException("key_mismatch", "Chave da operação difere da chave do evento.");
                    var (e, _) = await sp.GetRequiredService<OperatingService>().RecordAsync(r with { Offline = true, DeviceId = deviceId });
                    result = new { e.Id, e.ClockSuspect };
                    break;
                }
                default:
                    throw new DomainException("unknown_type", "Tipo de operação desconhecido.");
            }
            status = SyncStatus.Applied;
        }
        catch (DomainException ex)
        {
            db.ChangeTracker.Clear();
            status = SyncStatus.Rejected;
            error = ex.Code;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            // Falha transitória/inesperada: não grava resultado, o cliente tentará novamente.
            db.ChangeTracker.Clear();
            log.LogError(ex, "Falha ao sincronizar operação {Key}", op.IdempotencyKey);
            return new(op.IdempotencyKey, "retry", "server_error", null);
        }

        var json = status == SyncStatus.Applied ? JsonSerializer.Serialize(result, Json) : error;
        db.SyncEvents.Add(new SyncEvent { DeviceId = deviceId, IdempotencyKey = op.IdempotencyKey, Type = op.Type, LocalTime = op.LocalTime, Status = status, ResultJson = json });
        await db.SaveChangesAsync();
        return new(op.IdempotencyKey, status, error, status == SyncStatus.Applied ? JsonSerializer.SerializeToElement(result, Json) : null);
    }

    private async Task RequireAsync(string module, string permission)
    {
        var d = await features.AuthorizeAsync(module, permission, write: false);
        if (!d.Allowed) throw new DomainException(d.Code, d.Message, d.Status);
    }
}

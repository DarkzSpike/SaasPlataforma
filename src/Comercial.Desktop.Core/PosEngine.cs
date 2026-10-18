using System.Text.Json;
using Comercial.Shared.Contracts;

namespace Comercial.Desktop.Core;

public sealed record CartLine(Guid ProductId, string Name, decimal Quantity, long UnitPriceCents)
{
    public long TotalCents => (long)decimal.Round(UnitPriceCents * Quantity, 0, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Regras do PDV no terminal. A venda é gravada primeiro na fila local (transação SQLite)
/// e depois sincronizada; o servidor revalida preços, permissões, módulo e licença.
/// </summary>
public sealed class PosEngine(LocalDatabase db, LicenseGuard license, TimeProvider clock)
{
    public const string PdvModule = "pdv";

    public string RecordSale(Guid storeId, Guid cashSessionId, IReadOnlyList<CartLine> cart, IReadOnlyList<SalePaymentRequest> payments, long saleDiscountCents = 0)
    {
        var now = clock.GetUtcNow();
        var check = license.Evaluate(now);
        if (!check.CanOperate) throw new InvalidOperationException("Licença não permite vender: " + check.Reason);
        if (!check.HasModule(PdvModule)) throw new InvalidOperationException("Módulo PDV não contratado.");
        if (cart.Count == 0) throw new InvalidOperationException("Carrinho vazio.");

        var key = "sale-" + Guid.NewGuid().ToString("N");
        var req = new CreateSaleRequest(key, storeId, cashSessionId, now,
            cart.Select(c => new SaleItemRequest(c.ProductId, c.Quantity, 0, c.UnitPriceCents)).ToList(),
            payments.ToList(), saleDiscountCents, null, true);
        db.Enqueue(key, SyncTypes.SaleCreate, JsonSerializer.Serialize(req, ApiClient.Json), now);
        return key;
    }

    public string RecordStoreEvent(Guid storeId, bool open)
    {
        var now = clock.GetUtcNow();
        var key = (open ? "open-" : "close-") + Guid.NewGuid().ToString("N");
        var req = new OperatingEventRequest(key, storeId, open ? 1 : 2, now, true);
        db.Enqueue(key, SyncTypes.OperatingEvent, JsonSerializer.Serialize(req, ApiClient.Json), now);
        return key;
    }
}

public sealed record SyncSummary(int Sent, int Applied, int Duplicates, int Rejected, int Retry, bool ClockSkew);

/// <summary>Envia a fila em ordem; reenvios são seguros (idempotência no servidor).</summary>
public sealed class SyncAgent(LocalDatabase db, ApiClient api, Guid deviceId, TimeProvider clock)
{
    public async Task<SyncSummary> RunOnceAsync(int batchSize = 50)
    {
        int sent = 0, applied = 0, dup = 0, rejected = 0, retry = 0; var skew = false;
        while (true)
        {
            var items = db.Pending(batchSize);
            if (items.Count == 0) break;
            var ops = items.Select(i => new SyncOperation(i.IdempotencyKey, i.Type, i.LocalTime, JsonSerializer.Deserialize<JsonElement>(i.PayloadJson))).ToList();
            var res = await api.SyncAsync(new SyncBatchRequest(deviceId, clock.GetUtcNow(), ops));
            skew |= res.ClockSkewDetected;
            sent += items.Count;
            var progressed = false;
            foreach (var r in res.Results)
            {
                switch (r.Status)
                {
                    case SyncStatus.Applied: applied++; db.MarkResult(r.IdempotencyKey, OutboxStatus.Synced, null); progressed = true; break;
                    case SyncStatus.Duplicate when r.Error is null: dup++; db.MarkResult(r.IdempotencyKey, OutboxStatus.Synced, null); progressed = true; break;
                    case SyncStatus.Rejected or SyncStatus.Duplicate: rejected++; db.MarkResult(r.IdempotencyKey, OutboxStatus.Rejected, r.Error); progressed = true; break;
                    default: retry++; break;
                }
            }
            if (!progressed) break; // falha transitória: tenta no próximo ciclo
        }
        db.Set("sync.last_run_utc", clock.GetUtcNow().ToString("O"));
        return new(sent, applied, dup, rejected, retry, skew);
    }
}

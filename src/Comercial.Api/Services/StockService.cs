using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed class StockService(AppDbContext db, RequestActor actor)
{
    /// <summary>
    /// Registra movimentação imutável e atualiza o saldo de forma atômica no banco
    /// (UPDATE ... SET qty = qty + delta), evitando perda de atualização concorrente.
    /// Deve ser chamado dentro de uma transação aberta pelo chamador.
    /// </summary>
    /// <summary>Movimenta o produto ou, se for kit, cada componente na proporção (venda, cancelamento, devolução).</summary>
    public async Task MoveExpandedAsync(Guid storeId, Guid productId, decimal delta, StockMovementType type, string? reason, Guid? referenceId, long unitCostCents)
    {
        var comps = await db.ProductComponents.AsNoTracking().Where(c => c.KitProductId == productId).ToListAsync();
        if (comps.Count == 0) { await MoveAsync(storeId, productId, delta, type, reason, referenceId, unitCostCents); return; }
        var ids = comps.Select(c => c.ComponentProductId).ToList();
        var costs = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.CostCents);
        foreach (var c in comps)
            await MoveAsync(storeId, c.ComponentProductId, delta * c.Quantity, type, reason is null ? "kit" : reason + " (kit)", referenceId, costs.GetValueOrDefault(c.ComponentProductId));
    }

    public async Task MoveAsync(Guid storeId, Guid productId, decimal delta, StockMovementType type, string? reason, Guid? referenceId, long unitCostCents)
    {
        // Lotes com validade: a venda consome primeiro o que vence antes (FEFO).
        if (type == StockMovementType.Sale && delta < 0)
        {
            var left = -delta;
            foreach (var b in await db.ProductBatches.Where(b => b.StoreId == storeId && b.ProductId == productId && b.QuantityRemaining > 0)
                         .OrderBy(b => b.ExpiresOn == null).ThenBy(b => b.ExpiresOn).ThenBy(b => b.CreatedAt).ToListAsync())
            {
                if (left <= 0) break;
                var take = Math.Min(left, b.QuantityRemaining);
                b.QuantityRemaining -= take;
                left -= take;
            }
        }
        db.StockMovements.Add(new StockMovement
        {
            StoreId = storeId, ProductId = productId, Quantity = delta, Type = type, Reason = reason,
            UserId = actor.UserId, ReferenceId = referenceId, UnitCostCents = unitCostCents,
        });
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var updated = await db.StockBalances
            .Where(b => b.StoreId == storeId && b.ProductId == productId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Quantity, b => b.Quantity + delta)
                                      .SetProperty(b => b.UpdatedAt, now)
                                      .SetProperty(b => b.Version, b => b.Version + 1));
        if (updated == 0)
        {
            db.StockBalances.Add(new StockBalance { StoreId = storeId, ProductId = productId, Quantity = delta, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
    }

    public async Task EnsureStoreAccessAsync(Guid storeId)
    {
        if (!await db.Stores.AnyAsync(s => s.Id == storeId && s.IsActive))
            throw new DomainException("store_not_found", "Loja não encontrada.", 404);
        if (actor.UserStoreId is { } restricted && restricted != storeId)
            throw new DomainException("store_scope", "Usuário sem acesso a esta loja.", 403);
    }
}

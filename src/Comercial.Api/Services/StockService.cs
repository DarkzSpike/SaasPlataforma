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
    public async Task MoveAsync(Guid storeId, Guid productId, decimal delta, StockMovementType type, string? reason, Guid? referenceId, long unitCostCents)
    {
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

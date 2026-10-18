using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record ReturnLineInput(Guid SaleItemId, decimal Quantity);
public sealed record CreateReturnRequest(List<ReturnLineInput> Items, string Reason, bool Restock = true, string RefundMethod = "cash");

/// <summary>
/// Devolução parcial ou total. O valor devolvido é proporcional ao que o cliente pagou pelo item
/// (inclui descontos do item e rateio do desconto da venda). Estoque volta se o produto estiver em condição de venda.
/// A nota fiscal de devolução depende da integração fiscal (não incluída).
/// </summary>
public sealed class ReturnService(AppDbContext db, RequestActor actor, AuditService audit, StockService stock, ScopedLookup lookup, SalesService sales)
{
    public static readonly string[] RefundMethods = ["cash", "original", "store_credit"];

    public async Task<SaleReturn> CreateAsync(Guid saleId, CreateReturnRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5) throw new DomainException("reason_required", "Informe o motivo da devolução.");
        if (!RefundMethods.Contains(r.RefundMethod)) throw new DomainException("invalid_refund_method", "Forma de reembolso inválida.");
        if (r.Items is null || r.Items.Count == 0) throw new DomainException("invalid_items", "Informe os itens devolvidos.");
        var sale = await lookup.GetAsync<Sale>(saleId, q => q.Include(s => s.Items));
        if (sale.Status != SaleStatus.Completed) throw new DomainException("sale_not_completed", "Venda cancelada não aceita devolução.", 409);

        var previous = await db.SaleReturnItems.AsNoTracking()
            .Where(i => db.SaleReturns.Where(x => x.SaleId == sale.Id).Select(x => x.Id).Contains(i.SaleReturnId))
            .GroupBy(i => i.SaleItemId).Select(g => new { g.Key, Qty = g.Sum(i => i.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Qty);
        var itemsTotal = sale.Items.Sum(i => i.TotalCents);
        var ret = new SaleReturn { SaleId = sale.Id, UserId = actor.UserId!.Value, Reason = r.Reason.Trim(), Restock = r.Restock, RefundMethod = r.RefundMethod };
        foreach (var line in r.Items)
        {
            var item = sale.Items.FirstOrDefault(i => i.Id == line.SaleItemId) ?? throw new DomainException("item_not_found", "Item não pertence à venda.", 404);
            var available = item.Quantity - previous.GetValueOrDefault(item.Id);
            if (line.Quantity <= 0 || line.Quantity > available)
                throw new DomainException("invalid_quantity", $"Quantidade inválida para {item.ProductName} (disponível para devolução: {available:0.###}).");
            // Valor pago pelo item, já com o rateio do desconto da venda (inclui pontos usados).
            var paidForItem = itemsTotal == 0 ? 0 : (decimal)item.TotalCents * sale.TotalCents / itemsTotal;
            var refund = (long)Math.Round(paidForItem * line.Quantity / item.Quantity);
            ret.Items.Add(new SaleReturnItem { SaleReturnId = ret.Id, SaleItemId = item.Id, ProductId = item.ProductId, Quantity = line.Quantity, RefundCents = refund });
            ret.RefundCents += refund;
            ret.CostCents += (long)Math.Round(item.UnitCostCents * line.Quantity);
        }

        CashSession? cash = null;
        if (r.RefundMethod == "cash" && ret.RefundCents > 0)
            cash = await db.CashSessions.FirstOrDefaultAsync(c => c.OperatorUserId == actor.UserId && c.Status == CashSessionStatus.Open && c.StoreId == sale.StoreId)
                   ?? throw new DomainException("no_open_cash", "Abra um caixa para devolver o valor em dinheiro.", 409);

        await using var tx = await db.Database.BeginTransactionAsync();
        db.SaleReturns.Add(ret);
        sale.ReturnedCents += ret.RefundCents;
        if (cash is not null)
            db.CashMovements.Add(new CashMovement { CashSessionId = cash.Id, Type = CashMovementType.SaleCancellation, AmountCents = -ret.RefundCents, UserId = actor.UserId.Value, ReferenceId = ret.Id, Reason = "Devolução: " + ret.Reason });
        var fraction = sale.TotalCents == 0 ? 1m : Math.Min(1m, (decimal)ret.RefundCents / sale.TotalCents);
        await sales.ReverseLoyaltyAsync(sale, fraction, "devolução", ret.Id.ToString("N"));
        audit.Add("sale.returned", actor.TenantId, nameof(Sale), sale.Id, new { returnId = ret.Id, ret.RefundCents, r.Restock, r.RefundMethod, items = ret.Items.Count, r.Reason });
        await db.SaveChangesAsync();
        if (r.Restock)
            foreach (var i in ret.Items)
            {
                var item = sale.Items.First(x => x.Id == i.SaleItemId);
                await stock.MoveAsync(sale.StoreId, i.ProductId, i.Quantity, StockMovementType.Return, "Devolução: " + ret.Reason, ret.Id, item.UnitCostCents);
            }
        await tx.CommitAsync();
        return ret;
    }
}

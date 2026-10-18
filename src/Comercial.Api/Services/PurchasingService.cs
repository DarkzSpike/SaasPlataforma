using System.Text.Json;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Modules;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record SupplierInput(string Name, string? Document, string? ContactName, string? Email, string? Phone, int LeadTimeDays, string? Notes);
public sealed record PurchaseLineInput(Guid ProductId, decimal Quantity, long UnitCostCents);
public sealed record CreatePurchaseRequest(Guid SupplierId, Guid StoreId, List<PurchaseLineInput> Items, string? Notes, DateOnly? ExpectedAt);
public sealed record ReceiveLineInput(Guid PurchaseItemId, decimal Quantity, long? UnitCostCents);
public sealed record ReceivePurchaseRequest(List<ReceiveLineInput> Items, string? InvoiceNumber, string? Notes, int? PayableDueInDays, bool AcceptExcess = false);
public sealed record CancelRequest(string Reason);

/// <summary>Pedidos de compra: rascunho → aprovado → recebido (parcial ou total). Recebimento gera entrada de estoque.</summary>
public sealed class PurchasingService(
    AppDbContext db, RequestActor actor, AuditService audit, StockService stock, ScopedLookup lookup, FeatureAuthorization features)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Validate(SupplierInput r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) throw new DomainException("invalid_name", "Nome do fornecedor inválido.");
        if (r.Document is { Length: > 20 }) throw new DomainException("invalid_document", "Documento inválido.");
        if (r.Email is { Length: > 200 } || r.Phone is { Length: > 40 }) throw new DomainException("invalid_contact", "Contato inválido.");
        if (r.LeadTimeDays is < 0 or > 365) throw new DomainException("invalid_lead_time", "Prazo de entrega inválido.");
    }

    public async Task<PurchaseOrder> CreateAsync(CreatePurchaseRequest r)
    {
        if (r.Items is null || r.Items.Count == 0 || r.Items.Count > 500) throw new DomainException("invalid_items", "Informe os itens do pedido.");
        if (r.Items.Any(i => i.Quantity <= 0 || i.Quantity > 1_000_000 || i.UnitCostCents < 0)) throw new DomainException("invalid_quantity", "Quantidade ou custo inválido.");
        if (r.Items.Select(i => i.ProductId).Distinct().Count() != r.Items.Count) throw new DomainException("duplicate_product", "Produto repetido no pedido.");
        var supplier = await lookup.GetAsync<Supplier>(r.SupplierId);
        if (!supplier.IsActive) throw new DomainException("supplier_inactive", "Fornecedor inativo.", 409);
        await stock.EnsureStoreAccessAsync(r.StoreId);
        var ids = r.Items.Select(i => i.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        if (products.Count != ids.Count) throw new DomainException("product_not_found", "Produto inexistente.", 404);

        for (var attempt = 0; ; attempt++)
        {
            var number = (await db.PurchaseOrders.MaxAsync(p => (int?)p.Number) ?? 0) + 1;
            var po = new PurchaseOrder
            {
                Number = number, SupplierId = supplier.Id, StoreId = r.StoreId, Notes = r.Notes, ExpectedAt = r.ExpectedAt,
                CreatedByUserId = actor.UserId!.Value,
            };
            foreach (var i in r.Items)
                po.Items.Add(new PurchaseItem { PurchaseOrderId = po.Id, ProductId = i.ProductId, ProductName = products[i.ProductId].Name, QuantityOrdered = i.Quantity, UnitCostCents = i.UnitCostCents });
            db.PurchaseOrders.Add(po);
            audit.Add("purchase.created", actor.TenantId, nameof(PurchaseOrder), po.Id, new { number, supplier = supplier.Name, items = po.Items.Count, total = po.Items.Sum(Total) });
            try { await db.SaveChangesAsync(); return po; }
            catch (DbUpdateException) when (attempt < 3) { db.ChangeTracker.Clear(); } // número concorrente: tenta o próximo
        }
    }

    public static long Total(PurchaseItem i) => (long)Math.Round(i.QuantityOrdered * i.UnitCostCents);

    public async Task<PurchaseOrder> ApproveAsync(Guid id)
    {
        var po = await lookup.GetAsync<PurchaseOrder>(id, q => q.Include(p => p.Items));
        if (po.Status != PurchaseStatus.Draft) throw new DomainException("invalid_status", "Somente pedidos em rascunho podem ser aprovados.", 409);
        po.Status = PurchaseStatus.Approved;
        po.ApprovedByUserId = actor.UserId;
        po.ApprovedAt = DateTimeOffset.UtcNow;
        audit.Add("purchase.approved", actor.TenantId, nameof(PurchaseOrder), po.Id, new { po.Number });
        await db.SaveChangesAsync();
        return po;
    }

    public async Task<PurchaseOrder> CancelAsync(Guid id, CancelRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5) throw new DomainException("reason_required", "Informe o motivo.");
        var po = await lookup.GetAsync<PurchaseOrder>(id);
        if (po.Status is PurchaseStatus.Received or PurchaseStatus.Cancelled or PurchaseStatus.PartiallyReceived)
            throw new DomainException("invalid_status", "Pedido não pode ser cancelado nesta situação.", 409);
        po.Status = PurchaseStatus.Cancelled;
        po.CancelReason = r.Reason.Trim();
        po.ClosedAt = DateTimeOffset.UtcNow;
        audit.Add("purchase.cancelled", actor.TenantId, nameof(PurchaseOrder), po.Id, new { po.Number, r.Reason });
        await db.SaveChangesAsync();
        return po;
    }

    /// <summary>
    /// Recebimento (parcial ou total): entrada de estoque, custo médio ponderado do produto, registro de divergências
    /// e, opcionalmente, conta a pagar ao fornecedor (se o módulo Contas estiver contratado).
    /// </summary>
    public async Task<PurchaseReceipt> ReceiveAsync(Guid id, ReceivePurchaseRequest r)
    {
        var po = await lookup.GetAsync<PurchaseOrder>(id, q => q.Include(p => p.Items));
        if (po.Status is not (PurchaseStatus.Approved or PurchaseStatus.PartiallyReceived))
            throw new DomainException("invalid_status", "Receba apenas pedidos aprovados.", 409);
        if (r.Items is null || r.Items.Count == 0) throw new DomainException("invalid_items", "Informe o que foi recebido.");
        var byId = po.Items.ToDictionary(i => i.Id);
        var divergences = new List<object>();
        foreach (var line in r.Items)
        {
            if (!byId.TryGetValue(line.PurchaseItemId, out var item)) throw new DomainException("item_not_found", "Item não pertence ao pedido.", 404);
            if (line.Quantity < 0 || line.Quantity > 1_000_000 || line.UnitCostCents < 0) throw new DomainException("invalid_quantity", "Quantidade ou custo inválido.");
            var pending = item.QuantityOrdered - item.QuantityReceived;
            if (line.Quantity > pending)
            {
                if (!r.AcceptExcess) throw new DomainException("excess_quantity", $"Quantidade recebida de {item.ProductName} maior que a pendente ({pending}). Confirme o excesso.", 409);
                divergences.Add(new { item.ProductName, type = "excesso", ordered = item.QuantityOrdered, pending, received = line.Quantity });
            }
            if (line.UnitCostCents is { } c && c != item.UnitCostCents)
                divergences.Add(new { item.ProductName, type = "custo", ordered = item.UnitCostCents, received = c });
        }

        await using var tx = await db.Database.BeginTransactionAsync();
        long total = 0;
        foreach (var line in r.Items.Where(l => l.Quantity > 0))
        {
            var item = byId[line.PurchaseItemId];
            var unit = line.UnitCostCents ?? item.UnitCostCents;
            total += (long)Math.Round(line.Quantity * unit);
            item.QuantityReceived += line.Quantity;

            // Custo médio ponderado com o saldo atual da loja (custo histórico das vendas futuras).
            var product = await db.Products.FirstAsync(p => p.Id == item.ProductId);
            var current = await db.StockBalances.Where(b => b.StoreId == po.StoreId && b.ProductId == item.ProductId).Select(b => (decimal?)b.Quantity).FirstOrDefaultAsync() ?? 0;
            var previousCost = product.CostCents;
            product.CostCents = current > 0
                ? (long)Math.Round((current * product.CostCents + line.Quantity * unit) / (current + line.Quantity))
                : unit;
            if (product.CostCents != previousCost) { product.Version++; product.UpdatedAt = DateTimeOffset.UtcNow; }
            await db.SaveChangesAsync();
            await stock.MoveAsync(po.StoreId, item.ProductId, line.Quantity, StockMovementType.PurchaseReceipt, $"Pedido de compra #{po.Number}", po.Id, unit);
        }
        po.Status = po.Items.All(i => i.QuantityReceived >= i.QuantityOrdered) ? PurchaseStatus.Received : PurchaseStatus.PartiallyReceived;
        if (po.Status == PurchaseStatus.Received) po.ClosedAt = DateTimeOffset.UtcNow;
        var receipt = new PurchaseReceipt
        {
            PurchaseOrderId = po.Id, UserId = actor.UserId!.Value, InvoiceNumber = r.InvoiceNumber, Notes = r.Notes, TotalCents = total,
            DivergenceJson = divergences.Count == 0 ? null : JsonSerializer.Serialize(divergences, Json),
        };
        db.PurchaseReceipts.Add(receipt);

        if (r.PayableDueInDays is { } days && total > 0 && (await features.ModulesAsync()).Contains(ModuleKeys.PayablesReceivables))
        {
            var supplierName = await db.Suppliers.Where(s => s.Id == po.SupplierId).Select(s => s.Name).FirstAsync();
            db.FinancialTitles.Add(new FinancialTitle
            {
                Kind = TitleKind.Payable, Description = $"Compra #{po.Number}" + (r.InvoiceNumber is null ? "" : $" — NF {r.InvoiceNumber}"),
                Category = FinanceCategories.Merchandise, Counterparty = supplierName, SupplierId = po.SupplierId, PurchaseOrderId = po.Id,
                DueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(Math.Clamp(days, 0, 365))), AmountCents = total,
                GroupId = Guid.NewGuid(), CreatedByUserId = actor.UserId.Value,
            });
        }
        audit.Add("purchase.received", actor.TenantId, nameof(PurchaseOrder), po.Id, new { po.Number, total, r.InvoiceNumber, divergences = divergences.Count, status = po.Status.ToString() });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return receipt;
    }

    /// <summary>Sugestão de reposição: produtos no mínimo ou abaixo, quantidade para chegar a 2× o mínimo.</summary>
    public async Task<List<object>> SuggestionsAsync(Guid storeId)
    {
        await stock.EnsureStoreAccessAsync(storeId);
        var rows = await (from p in db.Products.AsNoTracking()
                          where p.IsActive && p.MinStock > 0
                          join b in db.StockBalances.AsNoTracking().Where(b => b.StoreId == storeId) on p.Id equals b.ProductId into bs
                          from b in bs.DefaultIfEmpty()
                          select new { p.Id, p.Name, p.Sku, p.CostCents, p.MinStock, Quantity = b == null ? 0m : b.Quantity }).ToListAsync();
        return rows.Where(r => r.Quantity <= r.MinStock)
            .Select(r => (object)new { productId = r.Id, r.Name, r.Sku, current = r.Quantity, r.MinStock, suggested = Math.Max(1, Math.Ceiling(r.MinStock * 2 - r.Quantity)), unitCostCents = r.CostCents })
            .ToList();
    }
}

public static class FinanceCategories
{
    public const string Merchandise = "Compra de mercadoria";
    public static readonly string[] Expense = ["Aluguel", "Energia", "Água", "Internet/telefone", "Salários", "Impostos", "Taxas de cartão", "Manutenção", "Marketing", "Outras despesas"];
}

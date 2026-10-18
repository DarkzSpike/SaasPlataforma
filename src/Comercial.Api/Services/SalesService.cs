using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Modules;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Comercial.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record CancelSaleRequest(string Reason);

public sealed class SalesService(
    AppDbContext db, RequestActor actor, AuditService audit, StockService stock,
    ScopedLookup lookup, FeatureAuthorization features)
{
    public const int MaxItems = 500;

    public async Task<SaleResponse> CreateAsync(CreateSaleRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.IdempotencyKey) || r.IdempotencyKey.Length > 80)
            throw new DomainException("idempotency_key_required", "Chave de idempotência obrigatória (até 80 caracteres).");

        // 1) Idempotência: reenvio da mesma venda devolve o resultado original, sem duplicar.
        var existing = await db.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.IdempotencyKey == r.IdempotencyKey);
        if (existing is not null) return ToResponse(existing, duplicate: true);

        if (r.Items is null || r.Items.Count == 0 || r.Items.Count > MaxItems) throw new DomainException("invalid_items", "Venda sem itens ou com itens demais.");
        if (r.Payments is null || r.Payments.Count == 0 || r.Payments.Count > 10) throw new DomainException("invalid_payments", "Informe as formas de pagamento.");

        await stock.EnsureStoreAccessAsync(r.StoreId);
        var session = await lookup.GetAsync<CashSession>(r.CashSessionId);
        if (session.Status != CashSessionStatus.Open) throw new DomainException("cash_not_open", "Caixa não está aberto.", 409);
        if (session.StoreId != r.StoreId) throw new DomainException("cash_store_mismatch", "Caixa pertence a outra loja.", 409);
        if (session.OperatorUserId != actor.UserId) throw new DomainException("not_owner", "Caixa pertence a outro operador.", 403);

        // 2) Preços SEMPRE do cadastro do servidor.
        var ids = r.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id) && p.IsActive).ToDictionaryAsync(p => p.Id);
        if (products.Count != ids.Count) throw new DomainException("product_not_found", "Produto inexistente ou inativo.", 404);

        var sale = new Sale
        {
            StoreId = r.StoreId, CashSessionId = session.Id, OperatorUserId = actor.UserId!.Value,
            DeviceId = actor.DeviceId ?? r.DeviceId, IdempotencyKey = r.IdempotencyKey,
            LocalCreatedAt = r.LocalCreatedAt, CreatedOffline = r.CreatedOffline,
        };
        long subtotal = 0, itemDiscounts = 0;
        foreach (var i in r.Items)
        {
            var p = products[i.ProductId];
            if (i.Quantity <= 0 || i.Quantity > 100_000 || decimal.Round(i.Quantity, 3) != i.Quantity)
                throw new DomainException("invalid_quantity", "Quantidade inválida.");
            if (i.ClientUnitPriceCents is { } cp && cp != p.PriceCents)
            {
                await audit.WriteNowAsync("security.price_mismatch", actor.TenantId, nameof(Product), p.Id, new { clientPrice = cp, serverPrice = p.PriceCents, r.IdempotencyKey, r.CreatedOffline });
                throw new DomainException("price_mismatch", "Preço divergente do cadastro. Atualize o catálogo e revise a venda.", 409);
            }
            var gross = (long)decimal.Round(p.PriceCents * i.Quantity, 0, MidpointRounding.AwayFromZero);
            if (i.DiscountCents < 0 || i.DiscountCents > gross) throw new DomainException("invalid_discount", "Desconto inválido.");
            subtotal += gross;
            itemDiscounts += i.DiscountCents;
            sale.Items.Add(new SaleItem
            {
                SaleId = sale.Id, ProductId = p.Id, ProductName = p.Name, Quantity = i.Quantity, UnitPriceCents = p.PriceCents,
                UnitCostCents = p.CostCents, DiscountCents = i.DiscountCents, TotalCents = gross - i.DiscountCents,
            });
        }
        if (r.SaleDiscountCents < 0 || r.SaleDiscountCents > subtotal - itemDiscounts) throw new DomainException("invalid_discount", "Desconto inválido.");

        // 3) Descontos acima do limite do operador exigem permissão de gerente.
        var discount = itemDiscounts + r.SaleDiscountCents;
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == actor.TenantId);
        if (subtotal > 0 && discount * 100m / subtotal > tenant.MaxOperatorDiscountPercent && !actor.Permissions.Contains(Permissions.SalesDiscountHigh))
        {
            await audit.WriteNowAsync("security.discount_denied", actor.TenantId, "Sale", r.IdempotencyKey, new { discount, subtotal });
            throw new DomainException("discount_not_authorized", "Desconto acima do limite permitido para o operador.", 403);
        }
        sale.SubtotalCents = subtotal;
        sale.DiscountCents = discount;
        sale.TotalCents = subtotal - discount;

        // 4) Pagamentos: troco somente sobre dinheiro; cartões/PIX não podem exceder o total.
        long paid = 0, nonCash = 0, cashPaid = 0;
        foreach (var pay in r.Payments)
        {
            if (!Enum.IsDefined(typeof(PaymentMethod), pay.Method) || pay.AmountCents <= 0) throw new DomainException("invalid_payment", "Pagamento inválido.");
            var method = (PaymentMethod)pay.Method;
            paid += pay.AmountCents;
            if (method == PaymentMethod.Cash) cashPaid += pay.AmountCents; else nonCash += pay.AmountCents;
            sale.Payments.Add(new SalePayment { SaleId = sale.Id, Method = method, AmountCents = pay.AmountCents });
        }
        if (paid < sale.TotalCents) throw new DomainException("insufficient_payment", "Pagamento menor que o total.");
        if (nonCash > sale.TotalCents) throw new DomainException("overpayment", "Pagamento eletrônico maior que o total.");
        sale.ChangeCents = paid - sale.TotalCents;
        if (sale.ChangeCents > cashPaid) throw new DomainException("invalid_change", "Troco maior que o valor em dinheiro.");

        // 5) Gravação transacional: venda + itens + pagamentos + estoque + caixa + documento fiscal pendente.
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            db.Sales.Add(sale);
            var netCash = cashPaid - sale.ChangeCents;
            if (netCash != 0)
                db.CashMovements.Add(new CashMovement { CashSessionId = session.Id, Type = CashMovementType.CashSale, AmountCents = netCash, UserId = actor.UserId.Value, ReferenceId = sale.Id });
            if ((await features.ModulesAsync()).Contains(ModuleKeys.Fiscal))
                db.FiscalDocuments.Add(new FiscalDocument { SaleId = sale.Id, Model = FiscalModel.NFCe, Status = FiscalStatus.PendingTransmission, LastMessage = "Aguardando transmissão." });
            audit.Add("sale.created", actor.TenantId, nameof(Sale), sale.Id, new { sale.TotalCents, sale.DiscountCents, items = sale.Items.Count, r.CreatedOffline });
            await db.SaveChangesAsync();

            foreach (var item in sale.Items)
                await stock.MoveAsync(sale.StoreId, item.ProductId, -item.Quantity, StockMovementType.Sale, null, sale.Id, item.UnitCostCents);

            await tx.CommitAsync();
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            // Corrida entre duas requisições com a mesma chave: a restrição única decide.
            var winner = await db.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.IdempotencyKey == r.IdempotencyKey);
            if (winner is not null) return ToResponse(winner, duplicate: true);
            throw;
        }
        return ToResponse(sale, duplicate: false);
    }

    public async Task<Sale> CancelAsync(Guid saleId, CancelSaleRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5)
            throw new DomainException("reason_required", "Informe o motivo do cancelamento (mínimo 5 caracteres).");
        var sale = await lookup.GetAsync<Sale>(saleId, q => q.Include(s => s.Items).Include(s => s.Payments));
        if (sale.Status != SaleStatus.Completed) throw new DomainException("sale_not_completed", "Venda já cancelada.", 409);

        var fiscal = await db.FiscalDocuments.FirstOrDefaultAsync(f => f.SaleId == sale.Id);
        if (fiscal is { Status: FiscalStatus.Authorized })
            throw new DomainException("fiscal_cancel_required", "Documento fiscal autorizado: o cancelamento precisa do evento fiscal junto à SEFAZ (provedor fiscal).", 409);

        var netCash = sale.Payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.AmountCents) - sale.ChangeCents;
        CashSession? refundSession = null;
        if (netCash > 0)
        {
            refundSession = await db.CashSessions.FirstOrDefaultAsync(c => c.Id == sale.CashSessionId && c.Status == CashSessionStatus.Open)
                ?? await db.CashSessions.FirstOrDefaultAsync(c => c.OperatorUserId == actor.UserId && c.Status == CashSessionStatus.Open && c.StoreId == sale.StoreId)
                ?? throw new DomainException("no_open_cash", "Abra um caixa para devolver o valor em dinheiro.", 409);
        }

        await using var tx = await db.Database.BeginTransactionAsync();
        sale.Status = SaleStatus.Cancelled;
        sale.CancelledAt = DateTimeOffset.UtcNow;
        sale.CancelledByUserId = actor.UserId;
        sale.CancelReason = r.Reason.Trim();
        if (refundSession is not null)
            db.CashMovements.Add(new CashMovement { CashSessionId = refundSession.Id, Type = CashMovementType.SaleCancellation, AmountCents = -netCash, UserId = actor.UserId!.Value, ReferenceId = sale.Id, Reason = r.Reason });
        if (fiscal is { Status: FiscalStatus.PendingTransmission or FiscalStatus.Rejected })
        {
            fiscal.Status = FiscalStatus.Cancelled;
            fiscal.LastMessage = "Venda cancelada antes da autorização.";
        }
        audit.Add("sale.cancelled", actor.TenantId, nameof(Sale), sale.Id, new { r.Reason, sale.TotalCents });
        await db.SaveChangesAsync();
        foreach (var item in sale.Items)
            await stock.MoveAsync(sale.StoreId, item.ProductId, item.Quantity, StockMovementType.SaleCancellation, r.Reason, sale.Id, item.UnitCostCents);
        await tx.CommitAsync();
        return sale;
    }

    public static SaleResponse ToResponse(Sale s, bool duplicate) =>
        new(s.Id, s.IdempotencyKey, s.Status.ToString(), s.SubtotalCents, s.DiscountCents, s.TotalCents, s.ChangeCents, duplicate);
}

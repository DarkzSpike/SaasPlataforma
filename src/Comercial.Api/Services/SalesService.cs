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
    ScopedLookup lookup, FeatureAuthorization features, LoyaltyService loyalty, StockAlertService stockAlerts, TimeProvider clock,
    PromotionService promotions, CreditService credit)
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
        if (products.Values.FirstOrDefault(p => p.HasVariants) is { } parent)
            throw new DomainException("variant_required", $"\"{parent.Name}\" tem grade: escolha o tamanho/cor (a variação).", 409);
        // Promoções vigentes calculadas no servidor (o PDV usa o mesmo cálculo para mostrar o total).
        var rules = await promotions.ActiveRulesAsync();
        var promo = Comercial.Shared.Promotions.PromotionEngine.Apply(
            r.Items.Select(i => new Comercial.Shared.Promotions.PromoLine(i.ProductId, i.Quantity, products[i.ProductId].PriceCents)).ToList(), rules, clock.GetUtcNow(), r.CouponCode);
        if (!string.IsNullOrWhiteSpace(r.CouponCode))
        {
            var valid = rules.Any(x => x.Type == Comercial.Shared.Promotions.PromoType.Coupon && Comercial.Shared.Promotions.PromotionEngine.IsActive(x, clock.GetUtcNow())
                                       && string.Equals(x.CouponCode, r.CouponCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!valid) throw new DomainException("invalid_coupon", "Cupom inválido ou vencido.", 409);
        }

        var sale = new Sale
        {
            StoreId = r.StoreId, CashSessionId = session.Id, OperatorUserId = actor.UserId!.Value,
            DeviceId = actor.DeviceId ?? r.DeviceId, IdempotencyKey = r.IdempotencyKey,
            LocalCreatedAt = r.LocalCreatedAt, CreatedOffline = r.CreatedOffline,
        };
        long subtotal = 0, itemDiscounts = 0, promoTotal = 0;
        var lineIndex = 0;
        foreach (var i in r.Items)
        {
            var promoCents = promo[lineIndex++].DiscountCents;
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
            promoCents = Math.Min(promoCents, gross - i.DiscountCents);
            subtotal += gross;
            itemDiscounts += i.DiscountCents;
            promoTotal += promoCents;
            sale.Items.Add(new SaleItem
            {
                SaleId = sale.Id, ProductId = p.Id, ProductName = p.Name, Quantity = i.Quantity, UnitPriceCents = p.PriceCents,
                UnitCostCents = p.CostCents, DiscountCents = i.DiscountCents + promoCents, TotalCents = gross - i.DiscountCents - promoCents,
            });
        }
        if (r.SaleDiscountCents < 0 || r.SaleDiscountCents > subtotal - itemDiscounts - promoTotal) throw new DomainException("invalid_discount", "Desconto inválido.");
        sale.PromotionDiscountCents = promoTotal;
        sale.CouponCode = string.IsNullOrWhiteSpace(r.CouponCode) ? null : r.CouponCode.Trim().ToUpperInvariant();

        // 3) Desconto por item acima do limite do operador exige permissão de gerente (promoções cadastradas não contam).
        var discount = itemDiscounts + promoTotal + r.SaleDiscountCents;
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == actor.TenantId);
        if (subtotal > 0 && itemDiscounts * 100m / subtotal > tenant.MaxOperatorDiscountPercent && !actor.Permissions.Contains(Permissions.SalesDiscountHigh))
        {
            await audit.WriteNowAsync("security.discount_denied", actor.TenantId, "Sale", r.IdempotencyKey, new { discount, subtotal });
            throw new DomainException("discount_not_authorized", "Desconto acima do limite permitido para o operador.", 403);
        }
        // 3a) Desconto no TOTAL da venda: só com autorização do proprietário (senha dele no PDV) ou de quem tem a permissão.
        DiscountAuthorization? discountAuth = null;
        if (r.SaleDiscountCents > 0)
        {
            if (actor.Permissions.Contains(Permissions.SalesDiscountAuthorize)) sale.DiscountAuthorizedByUserId = actor.UserId;
            else
            {
                var now = clock.GetUtcNow();
                discountAuth = r.DiscountAuthorizationId is { } aid ? await db.DiscountAuthorizations.FirstOrDefaultAsync(x => x.Id == aid) : null;
                var valid = discountAuth is not null && discountAuth.UsedBySaleId is null && discountAuth.MaxDiscountCents >= r.SaleDiscountCents
                    && (now <= discountAuth.ExpiresAt || (r.CreatedOffline && r.LocalCreatedAt <= discountAuth.ExpiresAt));
                if (!valid)
                {
                    await audit.WriteNowAsync("security.discount_denied", actor.TenantId, "Sale", r.IdempotencyKey, new { r.SaleDiscountCents, subtotal, r.DiscountAuthorizationId });
                    throw new DomainException("discount_not_authorized", "Desconto no total da venda exige autorização do proprietário.", 403);
                }
                discountAuth!.UsedBySaleId = sale.Id;
                discountAuth.UsedAt = now;
                sale.DiscountAuthorizedByUserId = discountAuth.ApproverUserId;
            }
        }
        // 3c) PIX dinâmico do caixa: só conta como pago com cobrança aprovada pelo Mercado Pago, do mesmo valor, não usada.
        PosPixCharge? pixCharge = null;
        if (r.PixChargeId is { } pcid)
        {
            pixCharge = await db.PosPixCharges.FirstOrDefaultAsync(x => x.Id == pcid);
            var pixPaid = r.Payments.Where(p => p.Method == 4).Sum(p => p.AmountCents);
            if (pixCharge is not { Status: PixChargeStatus.Approved, UsedBySaleId: null } || pixCharge.AmountCents != pixPaid)
                throw new DomainException("pix_not_confirmed", "O PIX desta venda não foi confirmado pelo Mercado Pago.", 409);
            pixCharge.UsedBySaleId = sale.Id;
        }
        // 3b) Cliente (CRM) e resgate de pontos (fidelidade). Pontos não contam no limite de desconto do operador.
        var modules = await features.ModulesAsync();
        LoyaltyProgram? program = null;
        long loyaltyDiscount = 0;
        if (r.CustomerId is { } customerId)
        {
            if (!modules.Contains(ModuleKeys.Crm)) throw new DomainException("module_not_enabled", "Cadastro de clientes não contratado.", 403);
            var customer = await lookup.GetAsync<Customer>(customerId);
            if (!customer.IsActive) throw new DomainException("customer_inactive", "Cliente inativo.", 409);
            sale.CustomerId = customer.Id;
            if (modules.Contains(ModuleKeys.Loyalty)) program = await loyalty.ProgramAsync(actor.TenantId);
        }
        if (r.RedeemPoints != 0)
        {
            if (r.RedeemPoints < 0) throw new DomainException("invalid_points", "Quantidade de pontos inválida.");
            if (program is not { Enabled: true }) throw new DomainException("loyalty_unavailable", "Resgate de pontos indisponível (cliente ou fidelidade).", 409);
            if (r.RedeemPoints < program.MinPointsToRedeem) throw new DomainException("points_below_minimum", $"Resgate mínimo de {program.MinPointsToRedeem} pontos.");
            loyaltyDiscount = (long)r.RedeemPoints * program.CentsPerPoint;
            if (loyaltyDiscount > (subtotal - discount) * program.MaxRedeemPercent / 100)
                throw new DomainException("redeem_limit", $"Pontos podem pagar no máximo {program.MaxRedeemPercent}% da compra.");
            sale.LoyaltyPointsRedeemed = r.RedeemPoints;
        }
        sale.SubtotalCents = subtotal;
        sale.LoyaltyDiscountCents = loyaltyDiscount;
        sale.DiscountCents = discount + loyaltyDiscount;
        sale.TotalCents = subtotal - discount - loyaltyDiscount;

        // 3d) Orçamento convertido em venda.
        Quote? quote = null;
        if (r.QuoteId is { } qid)
        {
            quote = await lookup.GetAsync<Quote>(qid);
            if (quote.Status != QuoteStatus.Open) throw new DomainException("quote_not_open", "Orçamento já usado ou cancelado.", 409);
        }

        // 4) Pagamentos: troco somente sobre dinheiro; cartões/PIX não podem exceder o total.
        long paid = 0, nonCash = 0, cashPaid = 0;
        foreach (var pay in r.Payments)
        {
            if (!Enum.IsDefined(typeof(PaymentMethod), pay.Method) || pay.AmountCents <= 0) throw new DomainException("invalid_payment", "Pagamento inválido.");
            var method = (PaymentMethod)pay.Method;
            if (method == PaymentMethod.StoreCredit && r.CustomerId is null)
                throw new DomainException("customer_required", "Venda no fiado (crediário) exige cliente identificado.");
            paid += pay.AmountCents;
            if (method == PaymentMethod.Cash) cashPaid += pay.AmountCents; else nonCash += pay.AmountCents;
            sale.Payments.Add(new SalePayment { SaleId = sale.Id, Method = method, AmountCents = pay.AmountCents });
        }
        if (paid < sale.TotalCents) throw new DomainException("insufficient_payment", "Pagamento menor que o total.");
        if (nonCash > sale.TotalCents) throw new DomainException("overpayment", "Pagamento eletrônico maior que o total.");
        sale.ChangeCents = paid - sale.TotalCents;
        if (sale.ChangeCents > cashPaid) throw new DomainException("invalid_change", "Troco maior que o valor em dinheiro.");
        var onCredit = sale.Payments.Where(p => p.Method == PaymentMethod.StoreCredit).Sum(p => p.AmountCents);
        Customer? creditCustomer = null;
        if (onCredit > 0)
        {
            if (!modules.Contains(ModuleKeys.PayablesReceivables)) throw new DomainException("module_not_enabled", "Fiado exige o módulo Contas a pagar e receber.", 403);
            creditCustomer = await lookup.GetAsync<Customer>(sale.CustomerId!.Value);
            await credit.EnsureLimitAsync(creditCustomer, onCredit);
        }

        // 5) Gravação transacional: venda + itens + pagamentos + estoque + caixa + documento fiscal pendente + pontos.
        var pointsEarned = 0;
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            db.Sales.Add(sale);
            var netCash = cashPaid - sale.ChangeCents;
            if (netCash != 0)
                db.CashMovements.Add(new CashMovement { CashSessionId = session.Id, Type = CashMovementType.CashSale, AmountCents = netCash, UserId = actor.UserId.Value, ReferenceId = sale.Id });
            if (creditCustomer is not null) credit.AddReceivable(sale, creditCustomer, onCredit);
            if (quote is not null) { quote.Status = QuoteStatus.Converted; quote.SaleId = sale.Id; sale.QuoteId = quote.Id; }
            if (modules.Contains(ModuleKeys.Fiscal))
                db.FiscalDocuments.Add(new FiscalDocument { SaleId = sale.Id, Model = FiscalModel.NFCe, Status = FiscalStatus.PendingTransmission, LastMessage = "Aguardando transmissão." });
            audit.Add("sale.created", actor.TenantId, nameof(Sale), sale.Id, new { sale.TotalCents, sale.DiscountCents, items = sale.Items.Count, r.CreatedOffline, customer = sale.CustomerId is not null, sale.LoyaltyPointsRedeemed });
            await db.SaveChangesAsync(); // a gravação da auditoria serializa as transações: o saldo de pontos abaixo é consistente

            var now = clock.GetUtcNow();
            if (sale.LoyaltyPointsRedeemed > 0)
            {
                var balance = await loyalty.BalanceAsync(sale.CustomerId!.Value);
                if (balance.Available < sale.LoyaltyPointsRedeemed)
                    throw new DomainException("insufficient_points", $"Saldo de pontos insuficiente ({balance.Available}).", 409);
                db.LoyaltyTransactions.Add(new LoyaltyTransaction
                {
                    CustomerId = sale.CustomerId.Value, SaleId = sale.Id, Type = LoyaltyType.Redeem, Points = -sale.LoyaltyPointsRedeemed,
                    UniqueKey = $"redeem:{sale.Id}", Reason = "resgate na venda", CreatedAt = now,
                });
            }
            if (program is { Enabled: true } && LoyaltyService.PointsFor(sale.TotalCents, program) is var earned and > 0)
            {
                db.LoyaltyTransactions.Add(new LoyaltyTransaction
                {
                    CustomerId = sale.CustomerId!.Value, SaleId = sale.Id, Type = LoyaltyType.Earn, Points = earned, UniqueKey = $"earn:{sale.Id}",
                    Reason = "compra", CreatedAt = now, ExpiresAt = program.ExpirationDays > 0 ? now.AddDays(program.ExpirationDays) : null,
                });
                pointsEarned = earned;
            }
            await db.SaveChangesAsync();

            foreach (var item in sale.Items)
                await stock.MoveExpandedAsync(sale.StoreId, item.ProductId, -item.Quantity, StockMovementType.Sale, null, sale.Id, item.UnitCostCents);

            await tx.CommitAsync();
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            // Corrida entre duas requisições com a mesma chave: a restrição única decide.
            var winner = await db.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.IdempotencyKey == r.IdempotencyKey);
            if (winner is not null) return ToResponse(winner, duplicate: true);
            if (discountAuth is not null) throw new DomainException("discount_authorization_used", "Esta autorização de desconto já foi usada em outra venda.", 409);
            if (pixCharge is not null) throw new DomainException("pix_already_used", "Este PIX já foi usado em outra venda.", 409);
            throw;
        }
        await stockAlerts.CheckAsync(sale.StoreId, sale.Items.Select(i => i.ProductId));
        return ToResponse(sale, duplicate: false) with { PointsEarned = pointsEarned };
    }

    public async Task<Sale> CancelAsync(Guid saleId, CancelSaleRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5)
            throw new DomainException("reason_required", "Informe o motivo do cancelamento (mínimo 5 caracteres).");
        var sale = await lookup.GetAsync<Sale>(saleId, q => q.Include(s => s.Items).Include(s => s.Payments));
        if (sale.Status != SaleStatus.Completed) throw new DomainException("sale_not_completed", "Venda já cancelada.", 409);
        if (sale.ReturnedCents > 0) throw new DomainException("has_returns", "Venda com devolução parcial: use a devolução para os itens restantes.", 409);

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
        foreach (var title in await db.FinancialTitles.Where(x => x.SaleId == sale.Id && x.Status == TitleStatus.Open).ToListAsync())
        {
            title.Status = TitleStatus.Cancelled;
            title.CancelReason = "Venda cancelada: " + r.Reason.Trim();
        }
        if (await db.FinancialTitles.AnyAsync(x => x.SaleId == sale.Id && x.Status == TitleStatus.Paid))
            throw new DomainException("credit_already_paid", "O fiado desta venda já foi pago: faça uma devolução em vez de cancelar.", 409);
        audit.Add("sale.cancelled", actor.TenantId, nameof(Sale), sale.Id, new { r.Reason, sale.TotalCents });
        await ReverseLoyaltyAsync(sale, 1m, "cancelamento", "cancel");
        await db.SaveChangesAsync();
        foreach (var item in sale.Items)
            await stock.MoveExpandedAsync(sale.StoreId, item.ProductId, item.Quantity, StockMovementType.SaleCancellation, r.Reason, sale.Id, item.UnitCostCents);
        await tx.CommitAsync();
        return sale;
    }

    /// <summary>
    /// Estorna pontos proporcionalmente (fração 1 = cancelamento total): remove os pontos ganhos e devolve os resgatados.
    /// A chave única por operação impede estorno em dobro.
    /// </summary>
    public async Task ReverseLoyaltyAsync(Sale sale, decimal fraction, string reason, string keySuffix)
    {
        if (sale.CustomerId is null) return;
        var txs = await db.LoyaltyTransactions.AsNoTracking().Where(t => t.SaleId == sale.Id).ToListAsync();
        var earned = txs.Where(t => t.Type == LoyaltyType.Earn).Sum(t => t.Points);
        var reversed = -txs.Where(t => t.Type == LoyaltyType.EarnReversal).Sum(t => t.Points);
        var redeemed = -txs.Where(t => t.Type == LoyaltyType.Redeem).Sum(t => t.Points);
        var refunded = txs.Where(t => t.Type == LoyaltyType.RedeemRefund).Sum(t => t.Points);
        var program = await loyalty.ProgramAsync(sale.TenantId);
        var now = clock.GetUtcNow();
        var takeBack = Math.Min(earned - reversed, (int)Math.Ceiling(earned * fraction));
        if (takeBack > 0)
            db.LoyaltyTransactions.Add(new LoyaltyTransaction
            {
                CustomerId = sale.CustomerId.Value, SaleId = sale.Id, Type = LoyaltyType.EarnReversal, Points = -takeBack,
                UniqueKey = $"earnrev:{sale.Id}:{keySuffix}", Reason = reason, CreatedAt = now,
            });
        var giveBack = Math.Min(redeemed - refunded, (int)Math.Floor(redeemed * fraction));
        if (giveBack > 0)
            db.LoyaltyTransactions.Add(new LoyaltyTransaction
            {
                CustomerId = sale.CustomerId.Value, SaleId = sale.Id, Type = LoyaltyType.RedeemRefund, Points = giveBack,
                UniqueKey = $"redeemref:{sale.Id}:{keySuffix}", Reason = reason, CreatedAt = now,
                ExpiresAt = program.ExpirationDays > 0 ? now.AddDays(program.ExpirationDays) : null,
            });
    }

    public static SaleResponse ToResponse(Sale s, bool duplicate) =>
        new(s.Id, s.IdempotencyKey, s.Status.ToString(), s.SubtotalCents, s.DiscountCents, s.TotalCents, s.ChangeCents, duplicate, s.LoyaltyDiscountCents);
}

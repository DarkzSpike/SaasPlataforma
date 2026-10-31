using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Comercial.Shared.Promotions;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record GenerateVariantsRequest(Dictionary<string, List<string>> Attributes, long? PriceCents);
public sealed record KitComponentInput(Guid ProductId, decimal Quantity);
public sealed record BarcodeInput(string Code);
public sealed record PromotionInput(string Name, int Type, Guid? ProductId, int BuyQty, int PayQty, decimal MinQty, long UnitPriceCents, decimal Percent,
    string? CouponCode, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, bool Active = true);
public sealed record QuoteItemInput(Guid ProductId, decimal Quantity);
public sealed record CreateQuoteRequest(Guid? CustomerId, string? CustomerName, string? Notes, int ValidDays, List<QuoteItemInput> Items);
public sealed record ReceiveCreditRequest(long AmountCents, int Method, Guid? CashSessionId);

/// <summary>Grade (variações), kits e códigos de barras adicionais.</summary>
public sealed class CatalogService(AppDbContext db, AuditService audit, RequestActor actor, ScopedLookup lookup)
{
    private static string Abbrev(string v)
    {
        var s = new string(v.Normalize(System.Text.NormalizationForm.FormD).Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        return s.Length <= 4 ? s : s[..4];
    }

    /// <summary>Cria uma variação para cada combinação (ex.: P/M/G × Azul/Preto = 6 produtos), copiando preço, custo e dados fiscais.</summary>
    public async Task<List<Product>> GenerateVariantsAsync(Guid parentId, GenerateVariantsRequest r)
    {
        var parent = await lookup.GetAsync<Product>(parentId);
        if (parent.ParentProductId is not null) throw new DomainException("invalid_parent", "Uma variação não pode ter variações.");
        if (parent.IsKit) throw new DomainException("invalid_parent", "Kits não podem ter grade.");
        var attrs = (r.Attributes ?? []).Where(a => !string.IsNullOrWhiteSpace(a.Key) && a.Value is { Count: > 0 })
            .Select(a => (Name: a.Key.Trim(), Values: a.Value.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToList())).Where(a => a.Values.Count > 0).ToList();
        if (attrs.Count is 0 or > 3) throw new DomainException("invalid_attributes", "Informe de 1 a 3 atributos (ex.: Tamanho, Cor) com valores.");
        IEnumerable<List<string>> combos = [[]];
        foreach (var a in attrs) combos = combos.SelectMany(c => a.Values.Select(v => c.Append(v).ToList()));
        var list = combos.ToList();
        if (list.Count > 200) throw new DomainException("too_many_variants", "No máximo 200 variações por produto.");
        var existing = await db.Products.Where(p => p.ParentProductId == parent.Id).Select(p => p.VariantLabel).ToListAsync();
        var created = new List<Product>();
        foreach (var combo in list)
        {
            var label = string.Join(" / ", combo);
            if (existing.Contains(label)) continue;
            var sku = $"{parent.Sku}-{string.Join("-", combo.Select(Abbrev))}";
            if (sku.Length > 60) sku = sku[..60];
            if (await db.Products.AnyAsync(p => p.Sku == sku)) throw new DomainException("sku_taken", $"Já existe produto com o código {sku}.", 409);
            var v = new Product
            {
                Sku = sku, Name = $"{parent.Name} - {label}", Description = parent.Description, Unit = parent.Unit, PriceCents = r.PriceCents ?? parent.PriceCents,
                CostCents = parent.CostCents, MinStock = parent.MinStock, CategoryId = parent.CategoryId, ParentProductId = parent.Id, VariantLabel = label,
                Ncm = parent.Ncm, Cest = parent.Cest, Cfop = parent.Cfop, FiscalOrigin = parent.FiscalOrigin, IcmsCst = parent.IcmsCst, IbsCbsCst = parent.IbsCbsCst, IbsCbsClass = parent.IbsCbsClass,
            };
            db.Products.Add(v);
            created.Add(v);
        }
        parent.HasVariants = true;
        parent.Version++;
        audit.Add("product.variants_generated", actor.TenantId, nameof(Product), parent.Id, new { count = created.Count, attributes = attrs.Select(a => a.Name) });
        await db.SaveChangesAsync();
        return created;
    }

    public Task<List<Product>> VariantsAsync(Guid parentId) => db.Products.AsNoTracking().Where(p => p.ParentProductId == parentId && p.IsActive).OrderBy(p => p.Name).ToListAsync();

    /// <summary>Define os componentes do kit; o custo do kit passa a ser a soma dos custos dos componentes.</summary>
    public async Task<Product> SetComponentsAsync(Guid kitId, List<KitComponentInput> items)
    {
        var kit = await lookup.GetAsync<Product>(kitId);
        if (kit.HasVariants || kit.ParentProductId is not null) throw new DomainException("invalid_kit", "Produto com grade não pode ser kit.");
        if (items.Count > 30) throw new DomainException("invalid_kit", "No máximo 30 componentes.");
        var ids = items.Select(i => i.ProductId).Distinct().ToList();
        if (ids.Contains(kitId)) throw new DomainException("invalid_kit", "O kit não pode conter ele mesmo.");
        var comps = await db.Products.Where(p => ids.Contains(p.Id) && p.IsActive).ToDictionaryAsync(p => p.Id);
        if (comps.Count != ids.Count) throw new DomainException("product_not_found", "Componente inexistente.", 404);
        if (comps.Values.Any(c => c.IsKit || c.HasVariants)) throw new DomainException("invalid_kit", "Componentes devem ser produtos simples (sem kit e sem grade).");
        if (items.Any(i => i.Quantity <= 0 || i.Quantity > 1000)) throw new DomainException("invalid_quantity", "Quantidade do componente inválida.");
        db.ProductComponents.RemoveRange(db.ProductComponents.Where(c => c.KitProductId == kitId));
        foreach (var i in items.GroupBy(i => i.ProductId)) db.ProductComponents.Add(new ProductComponent { KitProductId = kitId, ComponentProductId = i.Key, Quantity = i.Sum(x => x.Quantity) });
        kit.IsKit = items.Count > 0;
        kit.CostCents = items.Sum(i => (long)Math.Round(comps[i.ProductId].CostCents * i.Quantity));
        kit.Version++;
        audit.Add("product.kit_updated", actor.TenantId, nameof(Product), kit.Id, items);
        await db.SaveChangesAsync();
        return kit;
    }

    public async Task<object> ComponentsAsync(Guid kitId) =>
        await (from c in db.ProductComponents.AsNoTracking() join p in db.Products.AsNoTracking() on c.ComponentProductId equals p.Id
               where c.KitProductId == kitId select new { c.ComponentProductId, p.Name, p.Sku, c.Quantity }).ToListAsync();

    public async Task<ProductBarcode> AddBarcodeAsync(Guid productId, BarcodeInput r)
    {
        var p = await lookup.GetAsync<Product>(productId);
        var code = (r.Code ?? "").Trim();
        if (code.Length is < 3 or > 32 || code.Any(char.IsWhiteSpace)) throw new DomainException("invalid_barcode", "Código de barras inválido.");
        if (await db.Products.AnyAsync(x => x.Barcode == code || x.Sku == code) || await db.ProductBarcodes.AnyAsync(x => x.Code == code))
            throw new DomainException("barcode_taken", "Este código já pertence a outro produto.", 409);
        var b = new ProductBarcode { ProductId = p.Id, Code = code };
        db.ProductBarcodes.Add(b);
        audit.Add("product.barcode_added", actor.TenantId, nameof(Product), p.Id, new { code });
        await db.SaveChangesAsync();
        return b;
    }

    public async Task RemoveBarcodeAsync(Guid productId, string code)
    {
        var b = await db.ProductBarcodes.FirstOrDefaultAsync(x => x.ProductId == productId && x.Code == code) ?? throw new DomainException("not_found", "Código não encontrado.", 404);
        db.ProductBarcodes.Remove(b);
        audit.Add("product.barcode_removed", actor.TenantId, nameof(Product), productId, new { code });
        await db.SaveChangesAsync();
    }
}

public sealed class PromotionService(AppDbContext db, AuditService audit, RequestActor actor, ScopedLookup lookup)
{
    public static PromoRule ToRule(Promotion p) => new(p.Id, p.Name, (PromoType)p.Type, p.ProductId, p.BuyQty, p.PayQty, p.MinQty, p.UnitPriceCents, p.Percent,
        p.CouponCode, p.StartsAt, p.EndsAt, p.Active);

    public async Task<Promotion> SaveAsync(Guid? id, PromotionInput r)
    {
        if (!Enum.IsDefined(typeof(PromoType), r.Type)) throw new DomainException("invalid_promotion", "Tipo de promoção inválido.");
        var rule = new PromoRule(Guid.Empty, r.Name?.Trim() ?? "", (PromoType)r.Type, r.ProductId, r.BuyQty, r.PayQty, r.MinQty, r.UnitPriceCents, r.Percent, r.CouponCode?.Trim(), r.StartsAt, r.EndsAt, r.Active);
        if (PromotionEngine.Validate(rule) is { } err) throw new DomainException("invalid_promotion", err);
        if (r.ProductId is { } pid) await lookup.GetAsync<Product>(pid);
        if (rule.Type == PromoType.Coupon && await db.Promotions.AnyAsync(x => x.Id != id && x.CouponCode == rule.CouponCode && x.Active))
            throw new DomainException("coupon_taken", "Já existe cupom ativo com este código.", 409);
        var p = id is { } eid ? await lookup.GetAsync<Promotion>(eid) : new Promotion();
        if (id is null) db.Promotions.Add(p);
        p.Name = rule.Name; p.Type = r.Type; p.ProductId = r.ProductId; p.BuyQty = r.BuyQty; p.PayQty = r.PayQty; p.MinQty = r.MinQty; p.UnitPriceCents = r.UnitPriceCents;
        p.Percent = r.Percent; p.CouponCode = rule.CouponCode?.ToUpperInvariant(); p.StartsAt = r.StartsAt; p.EndsAt = r.EndsAt; p.Active = r.Active;
        audit.Add(id is null ? "promotion.created" : "promotion.updated", actor.TenantId, nameof(Promotion), p.Id, r);
        await db.SaveChangesAsync();
        return p;
    }

    /// <summary>Regras vigentes (para o PDV calcular igual ao servidor, inclusive offline).</summary>
    public async Task<List<PromoRule>> ActiveRulesAsync() =>
        (await db.Promotions.AsNoTracking().Where(p => p.Active).ToListAsync()).Select(ToRule).ToList();
}

/// <summary>Orçamentos: preços do cadastro no momento; viram venda no PDV (a venda confere preço de novo).</summary>
public sealed class QuoteService(AppDbContext db, AuditService audit, RequestActor actor, ScopedLookup lookup, TimeProvider clock)
{
    public async Task<Quote> CreateAsync(CreateQuoteRequest r)
    {
        if (r.Items is not { Count: > 0 } || r.Items.Count > 200) throw new DomainException("invalid_items", "Inclua de 1 a 200 itens.");
        if (r.ValidDays is < 1 or > 90) throw new DomainException("invalid_validity", "Validade de 1 a 90 dias.");
        var ids = r.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id) && p.IsActive).ToDictionaryAsync(p => p.Id);
        if (products.Count != ids.Count) throw new DomainException("product_not_found", "Produto inexistente ou inativo.", 404);
        string? customerName = r.CustomerName?.Trim();
        if (r.CustomerId is { } cid) customerName = (await lookup.GetAsync<Customer>(cid)).Name;
        var q = new Quote
        {
            CustomerId = r.CustomerId, CustomerName = string.IsNullOrWhiteSpace(customerName) ? null : customerName, Notes = r.Notes?.Trim(),
            ValidUntil = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(-3)).Date.AddDays(r.ValidDays)), CreatedByUserId = actor.UserId!.Value,
        };
        foreach (var i in r.Items)
        {
            if (i.Quantity <= 0 || i.Quantity > 100_000) throw new DomainException("invalid_quantity", "Quantidade inválida.");
            var p = products[i.ProductId];
            q.Items.Add(new QuoteItem { QuoteId = q.Id, ProductId = p.Id, ProductName = p.Name, Quantity = i.Quantity, UnitPriceCents = p.PriceCents });
        }
        q.TotalCents = q.Items.Sum(i => (long)decimal.Round(i.UnitPriceCents * i.Quantity, 0, MidpointRounding.AwayFromZero));
        for (var attempt = 0; ; attempt++)
        {
            q.Number = (await db.Quotes.MaxAsync(x => (int?)x.Number) ?? 0) + 1;
            db.Quotes.Add(q);
            audit.Add("quote.created", actor.TenantId, nameof(Quote), q.Id, new { q.Number, q.TotalCents, items = q.Items.Count });
            try { await db.SaveChangesAsync(); return q; }
            catch (DbUpdateException) when (attempt < 3) { db.ChangeTracker.Clear(); }
        }
    }

    public async Task<Quote> CancelAsync(Guid id)
    {
        var q = await lookup.GetAsync<Quote>(id);
        if (q.Status != QuoteStatus.Open) throw new DomainException("quote_not_open", "Orçamento não está em aberto.", 409);
        q.Status = QuoteStatus.Cancelled;
        audit.Add("quote.cancelled", actor.TenantId, nameof(Quote), q.Id);
        await db.SaveChangesAsync();
        return q;
    }
}

/// <summary>Fiado (crediário): limite por cliente, conta a receber por venda e recebimento no caixa.</summary>
public sealed class CreditService(AppDbContext db, AuditService audit, RequestActor actor, ScopedLookup lookup, TimeProvider clock)
{
    public const string Category = "Vendas no fiado";

    public async Task<long> OpenBalanceAsync(Guid customerId) =>
        await db.FinancialTitles.Where(t => t.CustomerId == customerId && t.Kind == TitleKind.Receivable && t.Status == TitleStatus.Open && t.Category == Category)
            .SumAsync(t => (long?)t.AmountCents) ?? 0;

    public async Task EnsureLimitAsync(Customer c, long amount)
    {
        if (c.CreditLimitCents <= 0) throw new DomainException("credit_not_allowed", "Este cliente não tem limite para comprar no fiado.", 409);
        var open = await OpenBalanceAsync(c.Id);
        if (open + amount > c.CreditLimitCents)
            throw new DomainException("credit_limit", $"Limite do fiado excedido: disponível {(c.CreditLimitCents - open) / 100m:C}.", 409);
    }

    public void AddReceivable(Sale sale, Customer c, long amount) => db.FinancialTitles.Add(new FinancialTitle
    {
        Kind = TitleKind.Receivable, Description = $"Compra no fiado {sale.ReceivedAt.ToOffset(TimeSpan.FromHours(-3)):dd/MM/yyyy}", Category = Category,
        Counterparty = c.Name, CustomerId = c.Id, SaleId = sale.Id, DueDate = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(-3)).Date.AddDays(30)),
        AmountCents = amount, GroupId = Guid.NewGuid(), CreatedByUserId = actor.UserId!.Value,
    });

    public async Task<object> StatementAsync(Guid customerId)
    {
        var c = await lookup.GetAsync<Customer>(customerId);
        var titles = await db.FinancialTitles.AsNoTracking().Where(t => t.CustomerId == customerId && t.Category == Category)
            .OrderByDescending(t => t.CreatedAt).Take(100).Select(t => new { t.Id, t.Description, t.DueDate, t.AmountCents, status = t.Status.ToString(), t.PaidOn, t.PaidAmountCents }).ToListAsync();
        var open = await OpenBalanceAsync(customerId);
        // O que foi levado em cada compra que ainda está em aberto (o app mostra produto por produto).
        var saleIds = await db.FinancialTitles.AsNoTracking()
            .Where(t => t.CustomerId == customerId && t.Category == Category && t.Status == TitleStatus.Open && t.SaleId != null)
            .Select(t => t.SaleId!.Value).Distinct().ToListAsync();
        var purchases = (await db.Sales.AsNoTracking().Where(s => saleIds.Contains(s.Id))
                .Select(s => new { s.Id, s.ReceivedAt, items = s.Items.Select(i => new { i.ProductName, i.Quantity, i.TotalCents }).ToList() }).ToListAsync())
            .OrderByDescending(s => s.ReceivedAt).ToList();
        return new { customer = c.Name, limitCents = c.CreditLimitCents, openCents = open, availableCents = Math.Max(0, c.CreditLimitCents - open), titles, purchases };
    }

    /// <summary>
    /// Fiado da loja inteira: quem está devendo, quanto, o limite de cada um e os totais (tela Clientes do app).
    /// </summary>
    public async Task<object> OverviewAsync()
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(-3)).Date);
        var titles = await db.FinancialTitles.AsNoTracking()
            .Where(t => t.Kind == TitleKind.Receivable && t.Status == TitleStatus.Open && t.Category == Category && t.CustomerId != null)
            .Select(t => new { CustomerId = t.CustomerId!.Value, t.AmountCents, t.DueDate, t.SaleId }).ToListAsync();
        var byCustomer = titles.GroupBy(t => t.CustomerId).ToDictionary(g => g.Key, g => new
        {
            open = g.Sum(t => t.AmountCents),
            overdue = g.Where(t => t.DueDate < today).Sum(t => t.AmountCents),
            oldestDue = g.Min(t => t.DueDate),
        });
        var saleIds = titles.Where(t => t.SaleId != null).Select(t => t.SaleId!.Value).Distinct().ToList();
        var items = saleIds.Count == 0 ? 0m : (await db.Sales.AsNoTracking().Where(s => saleIds.Contains(s.Id)).SelectMany(s => s.Items).Select(i => i.Quantity).ToListAsync()).Sum();
        var debtorIds = byCustomer.Keys.ToList();
        var customers = await db.Customers.AsNoTracking()
            .Where(c => c.IsActive && c.AnonymizedAt == null && (c.CreditLimitCents > 0 || debtorIds.Contains(c.Id)))
            .Select(c => new { c.Id, c.Name, c.Phone, c.CreditLimitCents, c.BillingDay }).ToListAsync();
        var rows = customers.Select(c =>
        {
            var b = byCustomer.GetValueOrDefault(c.Id);
            var open = b?.open ?? 0;
            return new
            {
                c.Id, c.Name, c.Phone, limitCents = c.CreditLimitCents, openCents = open, availableCents = Math.Max(0, c.CreditLimitCents - open),
                overdueCents = b?.overdue ?? 0, oldestDue = b?.oldestDue, c.BillingDay,
            };
        }).OrderByDescending(r => r.openCents).ThenBy(r => r.Name).ToList();
        return new
        {
            totalOpenCents = rows.Sum(r => r.openCents), overdueCents = rows.Sum(r => r.overdueCents), debtors = rows.Count(r => r.openCents > 0),
            itemsQuantity = items, totalLimitCents = rows.Sum(r => r.limitCents), customers = rows,
        };
    }

    /// <summary>Recebe um valor do cliente e baixa as contas mais antigas primeiro. Dinheiro entra no caixa aberto.</summary>
    public async Task<object> ReceiveAsync(Guid customerId, ReceiveCreditRequest r)
    {
        var c = await lookup.GetAsync<Customer>(customerId);
        if (r.AmountCents <= 0) throw new DomainException("invalid_amount", "Valor inválido.");
        if (r.Method is not (1 or 2 or 3 or 4)) throw new DomainException("invalid_payment", "Forma de pagamento inválida.");
        var open = await db.FinancialTitles.Where(t => t.CustomerId == c.Id && t.Category == Category && t.Status == TitleStatus.Open)
            .OrderBy(t => t.DueDate).ThenBy(t => t.CreatedAt).ToListAsync();
        var total = open.Sum(t => t.AmountCents);
        if (r.AmountCents > total) throw new DomainException("overpayment", $"Valor maior que a dívida ({total / 100m:C}).", 409);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(-3)).Date);
        var left = r.AmountCents;
        var method = ((PaymentMethod)r.Method).ToString();
        foreach (var t in open)
        {
            if (left <= 0) break;
            if (left >= t.AmountCents) { t.Status = TitleStatus.Paid; t.PaidOn = today; t.PaidAmountCents = t.AmountCents; t.PaymentMethod = method; left -= t.AmountCents; }
            else
            {
                // Pagamento parcial: baixa o valor pago e deixa o saldo em uma nova conta em aberto.
                db.FinancialTitles.Add(new FinancialTitle
                {
                    Kind = TitleKind.Receivable, Description = t.Description + " (saldo)", Category = Category, Counterparty = t.Counterparty, CustomerId = t.CustomerId,
                    SaleId = t.SaleId, DueDate = t.DueDate, AmountCents = t.AmountCents - left, GroupId = t.GroupId, CreatedByUserId = actor.UserId!.Value,
                });
                t.Status = TitleStatus.Paid; t.PaidOn = today; t.PaidAmountCents = left; t.AmountCents = left; t.PaymentMethod = method;
                left = 0;
            }
        }
        if (r.Method == 1)
        {
            var session = r.CashSessionId is { } sid ? await lookup.GetAsync<CashSession>(sid)
                : await db.CashSessions.FirstOrDefaultAsync(s => s.OperatorUserId == actor.UserId && s.Status == CashSessionStatus.Open)
                  ?? throw new DomainException("no_open_cash", "Abra o caixa para receber em dinheiro.", 409);
            if (session.Status != CashSessionStatus.Open) throw new DomainException("cash_not_open", "Caixa não está aberto.", 409);
            db.CashMovements.Add(new CashMovement { CashSessionId = session.Id, Type = CashMovementType.ReceivableReceipt, AmountCents = r.AmountCents, UserId = actor.UserId!.Value, Reason = "Recebimento de fiado: " + c.Name });
        }
        audit.Add("credit.received", actor.TenantId, nameof(Customer), c.Id, new { r.AmountCents, r.Method });
        await db.SaveChangesAsync();
        return await StatementAsync(c.Id);
    }
}

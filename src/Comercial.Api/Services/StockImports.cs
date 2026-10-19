using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record NfeItem(int Index, string SupplierCode, string? Ean, string Name, string? Ncm, string? Cest, string Unit, decimal Quantity, long UnitCostCents,
    long TotalCents, string? Batch, DateOnly? ExpiresOn, Guid? MatchedProductId, string? MatchedName);
public sealed record NfePreview(string AccessKey, string Number, string SupplierCnpj, string SupplierName, long TotalCents, bool AlreadyImported, List<NfeItem> Items);
public sealed record NfeImportRequest(string Xml, Guid StoreId, decimal MarkupPercent, bool CreateMissing, Dictionary<int, Guid>? Mapping);
public sealed record CsvImportRequest(string Csv, Guid? StoreId);
public sealed record TransferItemInput(Guid ProductId, decimal Quantity);
public sealed record TransferRequest(Guid FromStoreId, Guid ToStoreId, List<TransferItemInput> Items, string? Notes);

/// <summary>
/// Importa a NF-e de compra pelo XML: fornecedor, produtos (pelo código de barras/EAN), custo, lote/validade e entrada no estoque.
/// XML lido com DTD proibida e sem resolver entidades externas (proteção contra XXE).
/// </summary>
public sealed class NfeImportService(AppDbContext db, AuditService audit, RequestActor actor, StockService stock, BatchService batches)
{
    private static readonly XNamespace Ns = "http://www.portalfiscal.inf.br/nfe";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const int MaxXmlChars = 3_000_000;

    private static XDocument Load(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaxXmlChars) throw new DomainException("invalid_xml", "Arquivo XML vazio ou grande demais.");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0, IgnoreComments = true };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException) { throw new DomainException("invalid_xml", "O arquivo não é um XML de NF-e válido."); }
    }

    private static long Cents(string? v) => (long)Math.Round(decimal.Parse(v ?? "0", Inv) * 100);

    public async Task<NfePreview> PreviewAsync(string xml)
    {
        var doc = Load(xml);
        var inf = doc.Descendants(Ns + "infNFe").FirstOrDefault() ?? throw new DomainException("invalid_xml", "XML sem a tag infNFe (não é NF-e).");
        var key = ((string?)inf.Attribute("Id") ?? "").Replace("NFe", "");
        if (key.Length != 44 || !key.All(char.IsAsciiDigit)) throw new DomainException("invalid_xml", "Chave de acesso da NF-e inválida.");
        var emit = inf.Element(Ns + "emit");
        var cnpj = (string?)emit?.Element(Ns + "CNPJ") ?? (string?)emit?.Element(Ns + "CPF") ?? "";
        var name = (string?)emit?.Element(Ns + "xNome") ?? "Fornecedor";
        var number = (string?)inf.Element(Ns + "ide")?.Element(Ns + "nNF") ?? "";
        var total = Cents((string?)inf.Element(Ns + "total")?.Element(Ns + "ICMSTot")?.Element(Ns + "vNF"));
        var dets = inf.Elements(Ns + "det").ToList();
        if (dets.Count is 0 or > 990) throw new DomainException("invalid_xml", "NF-e sem itens ou com itens demais.");

        var eans = dets.Select(d => (string?)d.Element(Ns + "prod")?.Element(Ns + "cEAN")).Where(e => e is { Length: >= 8 } && e.All(char.IsAsciiDigit)).Distinct().ToList();
        var byBarcode = await db.Products.AsNoTracking().Where(p => p.Barcode != null && eans.Contains(p.Barcode)).ToDictionaryAsync(p => p.Barcode!, p => (p.Id, p.Name));
        var extra = await (from b in db.ProductBarcodes.AsNoTracking() join p in db.Products.AsNoTracking() on b.ProductId equals p.Id
                           where eans.Contains(b.Code) select new { b.Code, p.Id, p.Name }).ToListAsync();
        foreach (var e in extra) byBarcode.TryAdd(e.Code, (e.Id, e.Name));

        var items = new List<NfeItem>();
        var i = 0;
        foreach (var d in dets)
        {
            var p = d.Element(Ns + "prod")!;
            var ean = (string?)p.Element(Ns + "cEAN");
            if (ean is null || ean.Length < 8 || !ean.All(char.IsAsciiDigit)) ean = null; // "SEM GTIN"
            var rastro = p.Element(Ns + "rastro");
            DateOnly? val = DateOnly.TryParse((string?)rastro?.Element(Ns + "dVal"), Inv, out var dv) ? dv : null;
            var qty = decimal.Parse((string?)p.Element(Ns + "qCom") ?? "0", Inv);
            var match = ean is not null && byBarcode.TryGetValue(ean, out var m) ? m : ((Guid, string)?)null;
            items.Add(new NfeItem(i++, (string?)p.Element(Ns + "cProd") ?? "", ean, (string?)p.Element(Ns + "xProd") ?? "", (string?)p.Element(Ns + "NCM"), (string?)p.Element(Ns + "CEST"),
                ((string?)p.Element(Ns + "uCom") ?? "UN").ToUpperInvariant(), qty, Cents((string?)p.Element(Ns + "vUnCom")), Cents((string?)p.Element(Ns + "vProd")),
                (string?)rastro?.Element(Ns + "nLote"), val, match?.Item1, match?.Item2));
        }
        var already = await db.ImportedInvoices.AnyAsync(x => x.AccessKey == key);
        return new NfePreview(key, number, cnpj, name, total, already, items);
    }

    private static string Unit(string u) => u switch { "UN" or "UND" or "UNID" or "PC" or "PCT" => u is "PC" or "PCT" ? "PC" : "UN", "KG" => "KG", "G" or "GR" => "G", "L" or "LT" => "L", "ML" => "ML", "M" or "MT" => "M", "CX" => "CX", _ => "UN" };

    public async Task<object> ConfirmAsync(NfeImportRequest r)
    {
        await stock.EnsureStoreAccessAsync(r.StoreId);
        if (r.MarkupPercent is < 0 or > 1000) throw new DomainException("invalid_markup", "Margem inválida.");
        var pv = await PreviewAsync(r.Xml);
        if (pv.AlreadyImported) throw new DomainException("already_imported", "Esta NF-e já foi importada.", 409);

        var cnpj = FiscalRules.Digits(pv.SupplierCnpj);
        var supplier = cnpj.Length > 0 ? await db.Suppliers.FirstOrDefaultAsync(s => s.Document == cnpj) : null;
        if (supplier is null)
        {
            supplier = new Supplier { Name = pv.SupplierName, Document = cnpj.Length > 0 ? cnpj : null, Notes = "Criado pela importação de NF-e" };
            db.Suppliers.Add(supplier);
        }
        await using var tx = await db.Database.BeginTransactionAsync();
        int created = 0, updated = 0;
        foreach (var it in pv.Items)
        {
            Guid? productId = r.Mapping is not null && r.Mapping.TryGetValue(it.Index, out var mapped) ? mapped : it.MatchedProductId;
            Product product;
            if (productId is { } pid) product = await db.Products.FirstOrDefaultAsync(p => p.Id == pid) ?? throw new DomainException("product_not_found", "Produto escolhido não existe.", 404);
            else
            {
                if (!r.CreateMissing) throw new DomainException("unmatched_items", $"Item \"{it.Name}\" sem produto correspondente.", 409);
                var sku = ("F" + cnpj[..Math.Min(4, cnpj.Length)] + "-" + it.SupplierCode).ToUpperInvariant();
                if (sku.Length > 60) sku = sku[..60];
                while (await db.Products.AnyAsync(p => p.Sku == sku)) sku = sku[..Math.Min(55, sku.Length)] + "-" + Random.Shared.Next(100, 999);
                product = new Product
                {
                    Sku = sku, Barcode = it.Ean, Name = it.Name.Length > 200 ? it.Name[..200] : it.Name, Unit = Unit(it.Unit), CostCents = it.UnitCostCents,
                    PriceCents = (long)Math.Round(it.UnitCostCents * (1 + r.MarkupPercent / 100m)), Ncm = it.Ncm is { Length: 8 } ? it.Ncm : null,
                    Cest = it.Cest is { Length: 7 } ? it.Cest : null,
                };
                db.Products.Add(product);
                created++;
            }
            if (productId is not null) { product.CostCents = it.UnitCostCents; product.Version++; updated++; }
            await db.SaveChangesAsync();
            await stock.MoveAsync(r.StoreId, product.Id, it.Quantity, StockMovementType.PurchaseReceipt, $"NF-e {pv.Number} - {pv.SupplierName}", null, it.UnitCostCents);
            if (it.Batch is not null || it.ExpiresOn is not null) batches.Add(product.Id, r.StoreId, it.Batch ?? "S/L", it.ExpiresOn, it.Quantity);
        }
        db.ImportedInvoices.Add(new ImportedInvoice
        {
            AccessKey = pv.AccessKey, Number = pv.Number, SupplierId = supplier.Id, SupplierName = pv.SupplierName, StoreId = r.StoreId, TotalCents = pv.TotalCents,
            Items = pv.Items.Count, ImportedByUserId = actor.UserId!.Value,
        });
        audit.Add("purchase.nfe_imported", actor.TenantId, nameof(ImportedInvoice), pv.AccessKey, new { pv.Number, pv.SupplierName, pv.TotalCents, created, updated });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { throw new DomainException("already_imported", "Esta NF-e já foi importada.", 409); }
        await tx.CommitAsync();
        return new { pv.AccessKey, pv.Number, supplier = pv.SupplierName, items = pv.Items.Count, created, updated };
    }
}

/// <summary>
/// Importação de produtos por planilha (CSV com ";" — o padrão do Excel em português).
/// Colunas: sku;nome;preco;custo;codigo_barras;unidade;estoque_minimo;ncm;quantidade. Atualiza pelo SKU.
/// </summary>
public sealed class ProductCsvImportService(AppDbContext db, AuditService audit, RequestActor actor, StockService stock)
{
    public const int MaxLines = 5000;
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public async Task<object> ImportAsync(CsvImportRequest r)
    {
        var lines = (r.Csv ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) throw new DomainException("invalid_csv", "A planilha precisa do cabeçalho e de pelo menos uma linha.");
        if (lines.Length > MaxLines + 1) throw new DomainException("invalid_csv", $"No máximo {MaxLines} produtos por arquivo.");
        var sep = lines[0].Contains(';') ? ';' : ',';
        var head = lines[0].TrimStart('﻿').Split(sep).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => head.FindIndex(h => names.Contains(h));
        int cSku = Col("sku", "codigo", "código"), cName = Col("nome", "descricao", "descrição", "produto"), cPrice = Col("preco", "preço", "preco_venda"),
            cCost = Col("custo", "preco_custo"), cBar = Col("codigo_barras", "ean", "gtin"), cUnit = Col("unidade", "un"), cMin = Col("estoque_minimo", "minimo"),
            cNcm = Col("ncm"), cQty = Col("quantidade", "estoque", "qtd");
        if (cSku < 0 || cName < 0 || cPrice < 0) throw new DomainException("invalid_csv", "Colunas obrigatórias: sku; nome; preco.");
        if (cQty >= 0 && r.StoreId is { } sid) await stock.EnsureStoreAccessAsync(sid);

        int created = 0, updated = 0;
        var errors = new List<string>();
        var existing = await db.Products.ToDictionaryAsync(p => p.Sku);
        var toStock = new List<(Product P, decimal Q)>();
        for (var n = 1; n < lines.Length; n++)
        {
            var c = lines[n].Split(sep).Select(x => x.Trim().Trim('"')).ToArray();
            string? V(int i) => i >= 0 && i < c.Length && c[i].Length > 0 ? c[i] : null;
            bool Money(string? s, out long cents)
            {
                cents = 0;
                if (s is null) return true;
                if (!decimal.TryParse(s.Replace("R$", "").Trim(), NumberStyles.Number, Pt, out var v) || v < 0) return false;
                cents = (long)Math.Round(v * 100);
                return true;
            }
            var sku = V(cSku); var name = V(cName);
            if (sku is null || name is null || sku.Length > 60 || name.Length > 200) { errors.Add($"Linha {n + 1}: SKU e nome obrigatórios."); continue; }
            if (!Money(V(cPrice), out var price) || !Money(V(cCost), out var cost)) { errors.Add($"Linha {n + 1}: preço ou custo inválido."); continue; }
            var unit = (V(cUnit) ?? "UN").ToUpperInvariant();
            if (unit is not ("UN" or "KG" or "G" or "L" or "ML" or "M" or "CX" or "PC")) { errors.Add($"Linha {n + 1}: unidade \"{unit}\" inválida."); continue; }
            var ncm = V(cNcm) is { } nn ? FiscalRules.Digits(nn) : null;
            if (ncm is { Length: > 0 and not 8 }) { errors.Add($"Linha {n + 1}: NCM deve ter 8 dígitos."); continue; }
            decimal.TryParse(V(cMin) ?? "0", NumberStyles.Number, Pt, out var min);
            if (!existing.TryGetValue(sku, out var p))
            {
                p = new Product { Sku = sku };
                db.Products.Add(p);
                existing[sku] = p;
                created++;
            }
            else { p.Version++; p.UpdatedAt = DateTimeOffset.UtcNow; updated++; }
            p.Name = name; p.PriceCents = price; if (V(cCost) is not null) p.CostCents = cost; p.Unit = unit; p.MinStock = min;
            if (V(cBar) is { } bar) p.Barcode = bar;
            if (ncm is { Length: 8 }) p.Ncm = ncm;
            if (decimal.TryParse(V(cQty) ?? "", NumberStyles.Number, Pt, out var q) && q > 0) toStock.Add((p, q));
        }
        await db.SaveChangesAsync();
        if (r.StoreId is { } store)
            foreach (var (p, q) in toStock) await stock.MoveAsync(store, p.Id, q, StockMovementType.Entry, "Importação de planilha", null, p.CostCents);
        audit.Add("product.csv_imported", actor.TenantId, nameof(Product), null, new { created, updated, errors = errors.Count });
        await db.SaveChangesAsync();
        return new { created, updated, stockEntries = r.StoreId is null ? 0 : toStock.Count, errors = errors.Take(100) };
    }
}

/// <summary>Lotes e validade: entrada cria lote (a venda consome FEFO no StockService); relatório e alerta de vencimento.</summary>
public sealed class BatchService(AppDbContext db, NotificationService notifications, TimeProvider clock)
{
    public void Add(Guid productId, Guid storeId, string code, DateOnly? expires, decimal qty) => db.ProductBatches.Add(new ProductBatch
    {
        ProductId = productId, StoreId = storeId, BatchCode = code.Length > 40 ? code[..40] : code, ExpiresOn = expires, QuantityReceived = qty, QuantityRemaining = qty,
    });

    public async Task<object> ExpiringAsync(int days)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(-3)).Date);
        var limit = today.AddDays(Math.Clamp(days, 0, 365));
        return await (from b in db.ProductBatches.AsNoTracking()
                      join p in db.Products.AsNoTracking() on b.ProductId equals p.Id
                      where b.QuantityRemaining > 0 && b.ExpiresOn != null && b.ExpiresOn <= limit
                      orderby b.ExpiresOn
                      select new { b.Id, b.ProductId, p.Name, p.Sku, b.BatchCode, b.ExpiresOn, b.QuantityRemaining, b.StoreId, expired = b.ExpiresOn < today })
            .Take(500).ToListAsync();
    }

    /// <summary>Aviso ao dono para lotes que vencem em até 15 dias (uma vez por lote).</summary>
    public async Task<int> AlertAsync(CancellationToken ct)
    {
        var limit = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(-3)).Date.AddDays(15));
        var due = await db.ProductBatches.IgnoreQueryFilters().Where(b => b.QuantityRemaining > 0 && b.ExpiresOn != null && b.ExpiresOn <= limit && b.AlertedAt == null)
            .Take(200).ToListAsync(ct);
        foreach (var g in due.GroupBy(b => b.TenantId))
        {
            var ids = g.Select(b => b.ProductId).ToList();
            var names = await db.Products.IgnoreQueryFilters().Where(p => p.TenantId == g.Key && ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            var lines = g.OrderBy(b => b.ExpiresOn).Take(5).Select(b => $"{names.GetValueOrDefault(b.ProductId)} (lote {b.BatchCode}, vence {b.ExpiresOn:dd/MM})");
            await notifications.CreateAsync(g.Key, "stock.expiring", $"{g.Count()} lote(s) perto do vencimento",
                string.Join("; ", lines) + (g.Count() > 5 ? "..." : "") + ". Veja em Estoque → Validades.", new { batches = g.Select(b => b.Id) }, ct);
            foreach (var b in g) b.AlertedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return due.Count;
    }
}

public sealed class ExpiryJob(IServiceProvider sp, IConfiguration config, ILogger<ExpiryJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("Billing:AutoRun", true)) return;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(30), ct); } catch (OperationCanceledException) { return; }
            try
            {
                using var scope = sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
                await scope.ServiceProvider.GetRequiredService<BatchService>().AlertAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Falha no alerta de validade"); }
        }
    }
}

/// <summary>Transferência entre lojas: saída e entrada na mesma transação, com número sequencial.</summary>
public sealed class TransferService(AppDbContext db, AuditService audit, RequestActor actor, StockService stock)
{
    public async Task<StockTransfer> CreateAsync(TransferRequest r)
    {
        if (r.FromStoreId == r.ToStoreId) throw new DomainException("same_store", "Escolha lojas diferentes.");
        await stock.EnsureStoreAccessAsync(r.FromStoreId);
        if (!await db.Stores.AnyAsync(s => s.Id == r.ToStoreId && s.IsActive)) throw new DomainException("store_not_found", "Loja de destino não encontrada.", 404);
        if (r.Items is not { Count: > 0 } || r.Items.Count > 500 || r.Items.Any(i => i.Quantity <= 0)) throw new DomainException("invalid_items", "Informe os produtos e quantidades.");
        var ids = r.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        if (products.Count != ids.Count) throw new DomainException("product_not_found", "Produto inexistente.", 404);
        var t = new StockTransfer { FromStoreId = r.FromStoreId, ToStoreId = r.ToStoreId, Notes = r.Notes?.Trim(), CreatedByUserId = actor.UserId!.Value };
        t.Number = (await db.StockTransfers.MaxAsync(x => (int?)x.Number) ?? 0) + 1;
        foreach (var i in r.Items) t.Items.Add(new StockTransferItem { TransferId = t.Id, ProductId = i.ProductId, Quantity = i.Quantity });
        await using var tx = await db.Database.BeginTransactionAsync();
        db.StockTransfers.Add(t);
        audit.Add("stock.transfer", actor.TenantId, nameof(StockTransfer), t.Id, new { t.Number, r.FromStoreId, r.ToStoreId, items = r.Items.Count });
        await db.SaveChangesAsync();
        foreach (var i in r.Items)
        {
            var cost = products[i.ProductId].CostCents;
            await stock.MoveAsync(r.FromStoreId, i.ProductId, -i.Quantity, StockMovementType.TransferOut, $"Transferência #{t.Number}", t.Id, cost);
            await stock.MoveAsync(r.ToStoreId, i.ProductId, i.Quantity, StockMovementType.TransferIn, $"Transferência #{t.Number}", t.Id, cost);
        }
        await tx.CommitAsync();
        return t;
    }

    public async Task<object> ConsolidatedAsync()
    {
        var stores = await db.Stores.AsNoTracking().Where(s => s.IsActive).Select(s => new { s.Id, s.Name }).ToListAsync();
        var rows = await (from p in db.Products.AsNoTracking() where p.IsActive && !p.HasVariants
                          join b in db.StockBalances.AsNoTracking() on p.Id equals b.ProductId into bs
                          select new { p.Id, p.Name, p.Sku, balances = bs.Select(x => new { x.StoreId, x.Quantity }) }).Take(2000).ToListAsync();
        return new { stores, products = rows.Select(r => new { r.Id, r.Name, r.Sku, total = r.balances.Sum(b => b.Quantity), byStore = r.balances }) };
    }
}

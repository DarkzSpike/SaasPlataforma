using System.Globalization;
using System.IO.Compression;
using System.Text;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

/// <summary>
/// Relatórios gerenciais: curva ABC, giro de estoque, vendas e comissões por vendedor, ticket médio e horários de pico,
/// DRE simplificado e pacote para o contador. Valores de vendas canceladas não entram; devoluções são descontadas.
/// </summary>
public sealed class ReportService(AppDbContext db, RequestActor actor, AuditService audit)
{
    private static long Cost(long unit, decimal qty) => (long)Math.Round(unit * qty);

    public static void CheckPeriod(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from || to - from > TimeSpan.FromDays(400)) throw new DomainException("invalid_period", "Período inválido (máx. 400 dias).");
    }

    private IQueryable<Sale> Sales(DateTimeOffset from, DateTimeOffset to, Guid? storeId)
    {
        var q = db.Sales.AsNoTracking().Where(s => s.ReceivedAt >= from && s.ReceivedAt < to && s.Status == SaleStatus.Completed);
        var st = actor.UserStoreId ?? storeId;
        return st is { } s1 ? q.Where(s => s.StoreId == s1) : q;
    }

    /// <summary>Curva ABC por faturamento: A = itens que compõem os primeiros 80%, B = até 95%, C = o resto.</summary>
    public async Task<object> AbcAsync(DateTimeOffset from, DateTimeOffset to, Guid? storeId)
    {
        CheckPeriod(from, to);
        var ids = Sales(from, to, storeId).Select(s => s.Id);
        var rows = await db.SaleItems.AsNoTracking().Where(i => ids.Contains(i.SaleId))
            .GroupBy(i => new { i.ProductId, i.ProductName })
            .Select(g => new { g.Key.ProductId, Name = g.Key.ProductName, Qty = g.Sum(i => i.Quantity), Revenue = g.Sum(i => i.TotalCents) }).ToListAsync();
        var total = rows.Sum(r => r.Revenue);
        long acc = 0;
        var list = rows.OrderByDescending(r => r.Revenue).Select(r =>
        {
            // A classe considera o acumulado ANTES do item: o primeiro produto é sempre A, mesmo que sozinho passe de 80%.
            var before = total == 0 ? 0 : acc * 100m / total;
            acc += r.Revenue;
            var cum = total == 0 ? 0 : Math.Round(acc * 100m / total, 1);
            return new { r.ProductId, r.Name, quantity = r.Qty, revenueCents = r.Revenue, sharePercent = total == 0 ? 0 : Math.Round(r.Revenue * 100m / total, 1),
                cumulativePercent = cum, @class = before < 80 ? "A" : before < 95 ? "B" : "C" };
        }).ToList();
        return new { totalCents = total, a = list.Count(x => x.@class == "A"), b = list.Count(x => x.@class == "B"), c = list.Count(x => x.@class == "C"), products = list };
    }

    /// <summary>Giro: vendido no período ÷ estoque atual; cobertura = dias que o estoque atual dura no ritmo de vendas.</summary>
    public async Task<object> TurnoverAsync(DateTimeOffset from, DateTimeOffset to, Guid storeId)
    {
        CheckPeriod(from, to);
        var days = Math.Max(1, (decimal)(to - from).TotalDays);
        var ids = Sales(from, to, storeId).Select(s => s.Id);
        var sold = await db.SaleItems.AsNoTracking().Where(i => ids.Contains(i.SaleId)).GroupBy(i => i.ProductId)
            .Select(g => new { ProductId = g.Key, Qty = g.Sum(i => i.Quantity) }).ToDictionaryAsync(x => x.ProductId, x => x.Qty);
        var stock = await (from p in db.Products.AsNoTracking() where p.IsActive && !p.HasVariants && !p.IsKit
                           join b in db.StockBalances.AsNoTracking().Where(b => b.StoreId == storeId) on p.Id equals b.ProductId into bs
                           from b in bs.DefaultIfEmpty()
                           select new { p.Id, p.Name, p.Sku, p.CostCents, Qty = b == null ? 0m : b.Quantity }).ToListAsync();
        var list = stock.Select(s =>
        {
            var q = sold.GetValueOrDefault(s.Id);
            var perDay = q / days;
            return new { s.Id, s.Name, s.Sku, soldQuantity = q, stockQuantity = s.Qty, stockValueCents = Cost(s.CostCents, Math.Max(0, s.Qty)),
                turnover = s.Qty <= 0 ? (decimal?)null : Math.Round(q / s.Qty, 2), coverageDays = perDay <= 0 ? (decimal?)null : Math.Round(Math.Max(0, s.Qty) / perDay, 0),
                stalled = q == 0 && s.Qty > 0 };
        }).OrderByDescending(x => x.soldQuantity).ToList();
        return new { stockValueCents = list.Sum(x => x.stockValueCents), stalledCount = list.Count(x => x.stalled), products = list };
    }

    /// <summary>Vendas por vendedor (operador do caixa) e comissão sobre a venda líquida de devoluções.</summary>
    public async Task<object> SellersAsync(DateTimeOffset from, DateTimeOffset to, Guid? storeId)
    {
        CheckPeriod(from, to);
        var rows = await Sales(from, to, storeId).GroupBy(s => s.OperatorUserId)
            .Select(g => new { UserId = g.Key, Count = g.Count(), Revenue = g.Sum(s => s.TotalCents - s.ReturnedCents) }).ToListAsync();
        var ids = rows.Select(r => r.UserId).ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => (u.DisplayName, u.CommissionPercent));
        return rows.Select(r =>
        {
            var (name, pct) = users.GetValueOrDefault(r.UserId, ("(removido)", 0m));
            return new { r.UserId, name, sales = r.Count, revenueCents = r.Revenue, averageTicketCents = r.Count == 0 ? 0 : r.Revenue / r.Count,
                commissionPercent = pct, commissionCents = (long)Math.Round(r.Revenue * pct / 100m) };
        }).OrderByDescending(x => x.revenueCents).ToList();
    }

    /// <summary>Ticket médio e movimento por hora e dia da semana (no fuso da loja).</summary>
    public async Task<object> HoursAsync(DateTimeOffset from, DateTimeOffset to, Guid? storeId)
    {
        CheckPeriod(from, to);
        var tz = await StoreClock.ZoneAsync(db, actor);
        var sales = await Sales(from, to, storeId).Select(s => new { s.ReceivedAt, s.TotalCents }).ToListAsync();
        var local = sales.Select(s => (When: TimeZoneInfo.ConvertTime(s.ReceivedAt, tz), s.TotalCents)).ToList();
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        return new
        {
            sales = sales.Count, revenueCents = sales.Sum(s => s.TotalCents), averageTicketCents = sales.Count == 0 ? 0 : sales.Sum(s => s.TotalCents) / sales.Count,
            byHour = Enumerable.Range(0, 24).Select(h => new { hour = h, sales = local.Count(x => x.When.Hour == h), revenueCents = local.Where(x => x.When.Hour == h).Sum(x => x.TotalCents) }),
            byWeekday = Enumerable.Range(0, 7).Select(d => new { day = pt.DateTimeFormat.GetDayName((DayOfWeek)d), sales = local.Count(x => (int)x.When.DayOfWeek == d),
                revenueCents = local.Where(x => (int)x.When.DayOfWeek == d).Sum(x => x.TotalCents) }),
        };
    }

    /// <summary>DRE simplificado (gerencial, não contábil).</summary>
    public async Task<object> DreAsync(DateTimeOffset from, DateTimeOffset to, Guid? storeId)
    {
        CheckPeriod(from, to);
        var tz = await StoreClock.ZoneAsync(db, actor);
        var sales = await Sales(from, to, storeId).Select(s => new { s.Id, s.SubtotalCents, s.DiscountCents, s.TotalCents, s.ReturnedCents }).ToListAsync();
        var ids = sales.Select(s => s.Id).ToList();
        var cmv = await db.SaleItems.AsNoTracking().Where(i => ids.Contains(i.SaleId)).Select(i => new { i.UnitCostCents, i.Quantity }).ToListAsync();
        // Devolução com retorno ao estoque desfaz o custo da mercadoria vendida.
        var returnsCost = await db.SaleReturns.AsNoTracking().Where(r => ids.Contains(r.SaleId) && r.Restock).SumAsync(r => (long?)r.CostCents) ?? 0;
        var gross = sales.Sum(s => s.SubtotalCents);
        var discounts = sales.Sum(s => s.DiscountCents);
        var returns = sales.Sum(s => s.ReturnedCents);
        var net = gross - discounts - returns;
        var goods = cmv.Sum(i => Cost(i.UnitCostCents, i.Quantity)) - returnsCost;
        var d1 = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, tz).DateTime);
        var d2 = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, tz).DateTime);
        var expensesByCat = await db.FinancialTitles.AsNoTracking()
            .Where(t => t.Kind == TitleKind.Payable && t.Status == TitleStatus.Paid && t.Category != FinanceCategories.Merchandise && t.PaidOn >= d1 && t.PaidOn <= d2)
            .GroupBy(t => t.Category).Select(g => new { category = g.Key, cents = g.Sum(t => t.PaidAmountCents ?? 0) }).ToListAsync();
        var expenses = expensesByCat.Sum(e => e.cents);
        return new
        {
            grossRevenueCents = gross, discountsCents = discounts, returnsCents = returns, netRevenueCents = net, costOfGoodsCents = goods,
            grossProfitCents = net - goods, grossMarginPercent = net == 0 ? 0 : Math.Round((net - goods) * 100m / net, 1),
            expenses = expensesByCat.OrderByDescending(e => e.cents), expensesCents = expenses, netResultCents = net - goods - expenses,
            note = "DRE gerencial: impostos sobre vendas só aparecem se lançados como despesa. Não substitui a contabilidade.",
        };
    }

    private static string Csv(IEnumerable<string[]> rows) =>
        "﻿" + string.Join("\r\n", rows.Select(r => string.Join(";", r.Select(v => v.Length > 0 && "=+-@".Contains(v[0]) ? "'" + v : v.Replace(";", ",")))));

    /// <summary>
    /// Pacote do contador (ZIP): vendas, NFC-e (chave, número, situação e link do XML no provedor), despesas pagas, produtos com NCM.
    /// SPED Fiscal/EFD e Sintegra NÃO são gerados aqui: o contador gera a partir dos XMLs.
    /// </summary>
    public async Task<byte[]> AccountantPackageAsync(DateTimeOffset from, DateTimeOffset to)
    {
        CheckPeriod(from, to);
        var tz = await StoreClock.ZoneAsync(db, actor);
        string L(DateTimeOffset d) => TimeZoneInfo.ConvertTime(d, tz).ToString("dd/MM/yyyy HH:mm");
        static string M(long c) => (c / 100m).ToString("0.00", CultureInfo.GetCultureInfo("pt-BR"));
        var sales = await db.Sales.AsNoTracking().Include(s => s.Payments).Where(s => s.ReceivedAt >= from && s.ReceivedAt < to).OrderBy(s => s.ReceivedAt).ToListAsync();
        var docs = await db.FiscalDocuments.AsNoTracking().Where(d => d.CreatedAt >= from && d.CreatedAt < to).OrderBy(d => d.CreatedAt).ToListAsync();
        var d1 = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, tz).DateTime);
        var d2 = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, tz).DateTime);
        var titles = await db.FinancialTitles.AsNoTracking().Where(t => t.Status == TitleStatus.Paid && t.PaidOn >= d1 && t.PaidOn <= d2).ToListAsync();
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).ToListAsync();
        var profile = await db.FiscalProfiles.AsNoTracking().FirstOrDefaultAsync();

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string content)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Add("vendas.csv", Csv(new[] { new[] { "data_hora", "situacao", "subtotal", "descontos", "total", "devolvido", "formas_pagamento" } }
                .Concat(sales.Select(s => new[] { L(s.ReceivedAt), s.Status.ToString(), M(s.SubtotalCents), M(s.DiscountCents), M(s.TotalCents), M(s.ReturnedCents),
                    string.Join(" + ", s.Payments.Select(p => $"{p.Method}:{M(p.AmountCents)}")) }))));
            Add("nfce.csv", Csv(new[] { new[] { "emitida_em", "serie", "numero", "chave", "situacao", "protocolo", "ambiente", "xml", "danfe" } }
                .Concat(docs.Select(d => new[] { L(d.CreatedAt), d.Series?.ToString() ?? "", d.Number?.ToString() ?? "", d.AccessKey ?? "", d.Status.ToString(), d.Protocol ?? "",
                    d.Environment == 1 ? "producao" : "homologacao", d.XmlUrl ?? "", d.DanfeUrl ?? "" }))));
            Add("contas_pagas_e_recebidas.csv", Csv(new[] { new[] { "pago_em", "tipo", "categoria", "descricao", "fornecedor_cliente", "valor", "forma" } }
                .Concat(titles.Select(t => new[] { t.PaidOn?.ToString("dd/MM/yyyy") ?? "", t.Kind == TitleKind.Payable ? "pagar" : "receber", t.Category, t.Description,
                    t.Counterparty ?? "", M(t.PaidAmountCents ?? t.AmountCents), t.PaymentMethod ?? "" }))));
            Add("produtos.csv", Csv(new[] { new[] { "sku", "nome", "ncm", "cest", "cfop", "origem", "preco", "custo" } }
                .Concat(products.Select(p => new[] { p.Sku, p.Name, p.Ncm ?? "", p.Cest ?? "", p.Cfop ?? "", p.FiscalOrigin.ToString(), M(p.PriceCents), M(p.CostCents) }))));
            Add("LEIA-ME.txt",
                $"Pacote para o contador — período {d1:dd/MM/yyyy} a {d2:dd/MM/yyyy}\r\n" +
                $"Emitente: {(profile is null ? "perfil fiscal não configurado" : "CNPJ " + profile.Cnpj + " / " + profile.Uf)}\r\n\r\n" +
                "Arquivos: vendas.csv, nfce.csv (com links dos XMLs no provedor fiscal), contas_pagas_e_recebidas.csv, produtos.csv.\r\n" +
                "Os XMLs oficiais das NFC-e ficam no provedor fiscal (ex.: painel Focus NFe), que também permite baixá-los em lote.\r\n" +
                "SPED Fiscal/EFD e Sintegra não são gerados por este sistema; o contador os gera a partir dos XMLs.\r\n");
        }
        audit.Add("report.accountant_package", actor.TenantId, "report", null, new { from, to, sales = sales.Count, docs = docs.Count });
        await db.SaveChangesAsync();
        return ms.ToArray();
    }
}

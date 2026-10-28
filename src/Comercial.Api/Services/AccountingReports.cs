using System.Globalization;
using System.Text;
using System.Xml;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Modules;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

/// <summary>Período em datas locais da loja, convertido para o intervalo [From, To) em UTC.</summary>
public sealed record AccountingPeriod(DateOnly FirstDay, DateOnly LastDay, DateTimeOffset From, DateTimeOffset To, TimeZoneInfo Tz)
{
    public string Label => FirstDay.Day == 1 && LastDay == FirstDay.AddMonths(1).AddDays(-1)
        ? $"{AccountingReportService.MonthName(FirstDay.Month)}/{FirstDay.Year}" : $"{FirstDay:dd/MM/yyyy} a {LastDay:dd/MM/yyyy}";
    public string Local(DateTimeOffset d) => TimeZoneInfo.ConvertTime(d, Tz).ToString("dd/MM/yyyy HH:mm");
}

public sealed record ReportTotal(string Label, string Value);
/// <summary>Relatório em forma de tabela: o mesmo conteúdo vai para a tela e para o CSV.</summary>
public sealed record ReportTable(string Key, string Title, string Period, string[] Headers, List<string[]> Rows, List<ReportTotal> Totals, List<string> Notes);

/// <summary>Divergência encontrada para análise (o sistema só aponta; não corrige nada fiscal sozinho).</summary>
public sealed record Divergence(string Type, string When, string Sale, string Document, long SaleCents, long? DocumentCents, string Detail);

/// <summary>
/// Relatórios para a contabilidade: fechamento de caixa, meios de pagamento (com conciliação PIX), vendas × documentos fiscais,
/// cancelamentos, devoluções, rejeitados, pendentes, produtos e movimentações financeiras. Valores em centavos, sem arredondar.
/// </summary>
public sealed class AccountingReportService(AppDbContext db, RequestActor actor, FeatureAuthorization features)
{
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
    public static string M(long cents) => (cents / 100m).ToString("0.00", Br);
    public static string MonthName(int m) => Br.DateTimeFormat.GetMonthName(m) is { } n ? char.ToUpperInvariant(n[0]) + n[1..] : m.ToString();
    private static string Short(Guid id) => id.ToString("N")[..8];

    public static readonly string[] ReportKeys = ["caixa", "pagamentos", "conciliacao", "vendas_fiscal", "divergencias", "cancelamentos", "devolucoes", "rejeitados", "pendentes", "produtos", "financeiro"];

    // ---------------- Período ----------------

    public async Task<AccountingPeriod> PeriodAsync(DateOnly first, DateOnly last)
    {
        if (last < first) throw new DomainException("invalid_period", "Data final antes da inicial.");
        if (last.DayNumber - first.DayNumber > 370) throw new DomainException("invalid_period", "Período máximo: 1 ano.");
        var tz = await StoreClock.ZoneAsync(db, actor);
        static DateTimeOffset At(TimeZoneInfo tz, DateTime local) => new(local, tz.GetUtcOffset(local));
        return new(first, last, At(tz, first.ToDateTime(TimeOnly.MinValue)).ToUniversalTime(), At(tz, last.AddDays(1).ToDateTime(TimeOnly.MinValue)).ToUniversalTime(), tz);
    }

    public Task<AccountingPeriod> MonthAsync(int year, int month)
    {
        if (year is < 2000 or > 2100 || month is < 1 or > 12) throw new DomainException("invalid_period", "Mês inválido.");
        var first = new DateOnly(year, month, 1);
        return PeriodAsync(first, first.AddMonths(1).AddDays(-1));
    }

    public async Task<bool> FiscalEnabledAsync() => (await features.ModulesAsync()).Contains(ModuleKeys.Fiscal);

    /// <summary>Modo de emissão da loja (sempre / a pedido / nunca). Sem perfil fiscal, conta como "sempre" (comportamento anterior).</summary>
    public async Task<string> EmissionModeAsync() => await db.FiscalProfiles.AsNoTracking().Select(p => p.EmissionMode).FirstOrDefaultAsync() ?? NfceModes.Always;

    // ---------------- Dados base ----------------

    private sealed record SaleRow(Guid Id, DateTimeOffset At, Guid StoreId, Guid CashSessionId, Guid OperatorUserId, SaleStatus Status, long Subtotal, long Discount,
        long Total, long Change, long Returned, DateTimeOffset? CancelledAt, string? CancelReason);

    private async Task<List<SaleRow>> SalesAsync(AccountingPeriod p, Guid? storeId) =>
        await db.Sales.AsNoTracking().Where(s => s.ReceivedAt >= p.From && s.ReceivedAt < p.To && (storeId == null || s.StoreId == storeId))
            .OrderBy(s => s.ReceivedAt)
            .Select(s => new SaleRow(s.Id, s.ReceivedAt, s.StoreId, s.CashSessionId, s.OperatorUserId, s.Status, s.SubtotalCents, s.DiscountCents, s.TotalCents,
                s.ChangeCents, s.ReturnedCents, s.CancelledAt, s.CancelReason))
            .ToListAsync();

    private sealed record DocRow(Guid Id, Guid SaleId, FiscalStatus Status, int? Number, int? Series, string? AccessKey, DateTimeOffset? AuthorizedAt, DateTimeOffset CreatedAt,
        string? LastMessage, int Attempts, DateTimeOffset? NextAttemptAt, string? XmlUrl, string? CancelXmlUrl);

    private async Task<Dictionary<Guid, DocRow>> DocsBySaleAsync(List<Guid> saleIds)
    {
        var list = new List<DocRow>();
        foreach (var chunk in saleIds.Chunk(1000))
            list.AddRange(await db.FiscalDocuments.AsNoTracking().Where(d => chunk.Contains(d.SaleId))
                .Select(d => new DocRow(d.Id, d.SaleId, d.Status, d.Number, d.Series, d.AccessKey, d.AuthorizedAt, d.CreatedAt, d.LastMessage, d.Attempts, d.NextAttemptAt, d.XmlUrl, d.CancelXmlUrl))
                .ToListAsync());
        return list.GroupBy(d => d.SaleId).ToDictionary(g => g.Key, g => g.First());
    }

    private async Task<HashSet<(Guid Doc, string Kind)>> ArchivedAsync(IEnumerable<Guid> docIds)
    {
        var set = new HashSet<(Guid, string)>();
        foreach (var chunk in docIds.Chunk(1000))
            foreach (var x in await db.FiscalXmlFiles.AsNoTracking().Where(f => f.FiscalDocumentId != null && chunk.Contains(f.FiscalDocumentId.Value))
                         .Select(f => new { f.FiscalDocumentId, f.Kind }).ToListAsync())
                set.Add((x.FiscalDocumentId!.Value, x.Kind));
        return set;
    }

    private Task<Dictionary<Guid, string>> UserNamesAsync() => db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
    private Task<Dictionary<Guid, string>> StoreNamesAsync() => db.Stores.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);

    /// <summary>Totais tributários lidos dos XMLs autorizados guardados (só o que estiver nos documentos).</summary>
    public sealed record TaxTotals(long Nf, long Icms, long Pis, long Cofins, long TotTrib, long Ibs, long Cbs, int Files);

    private async Task<(TaxTotals Totals, Dictionary<Guid, long> NfByDoc)> TaxesAsync(IEnumerable<Guid> docIds)
    {
        long nf = 0, icms = 0, pis = 0, cofins = 0, trib = 0, ibs = 0, cbs = 0; var files = 0;
        var byDoc = new Dictionary<Guid, long>();
        foreach (var chunk in docIds.Chunk(200))
        {
            var xmls = await db.FiscalXmlFiles.AsNoTracking().Where(f => f.Kind == FiscalXmlKind.Authorization && f.FiscalDocumentId != null && chunk.Contains(f.FiscalDocumentId.Value))
                .Select(f => new { f.FiscalDocumentId, f.Content }).ToListAsync();
            foreach (var x in xmls)
            {
                var t = ReadTotals(x.Content);
                if (t is null) continue;
                files++;
                byDoc[x.FiscalDocumentId!.Value] = t.Value.Nf;
                nf += t.Value.Nf; icms += t.Value.Icms; pis += t.Value.Pis; cofins += t.Value.Cofins; trib += t.Value.Trib; ibs += t.Value.Ibs; cbs += t.Value.Cbs;
            }
        }
        return (new TaxTotals(nf, icms, pis, cofins, trib, ibs, cbs, files), byDoc);
    }

    public static (long Nf, long Icms, long Pis, long Cofins, long Trib, long Ibs, long Cbs)? ReadTotals(byte[] xml)
    {
        try
        {
            using var ms = new MemoryStream(xml);
            using var r = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var x = new XmlDocument { XmlResolver = null };
            x.Load(r);
            long C(string xpath) => decimal.TryParse(x.SelectSingleNode(xpath)?.InnerText, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? (long)Math.Round(v * 100) : 0;
            const string tot = "//*[local-name()='ICMSTot']/*[local-name()='{0}']";
            return (C(string.Format(tot, "vNF")), C(string.Format(tot, "vICMS")), C(string.Format(tot, "vPIS")), C(string.Format(tot, "vCOFINS")), C(string.Format(tot, "vTotTrib")),
                C("//*[local-name()='IBSCBSTot']//*[local-name()='vIBS']"), C("//*[local-name()='IBSCBSTot']//*[local-name()='vCBS']"));
        }
        catch (XmlException) { return null; }
    }

    /// <summary>Notas que precisam do XML original com protocolo: autorizadas (mesmo se canceladas depois) e denegadas.</summary>
    public static bool NeedsAuthorizationXml(FiscalStatus s, DateTimeOffset? authorizedAt) =>
        s is FiscalStatus.Authorized or FiscalStatus.Denied || (s == FiscalStatus.Cancelled && authorizedAt != null);

    // ---------------- Relatórios ----------------

    public Task<ReportTable> ReportAsync(string key, AccountingPeriod p, Guid? storeId) => key switch
    {
        "caixa" => CashAsync(p, storeId),
        "pagamentos" => PaymentsAsync(p, storeId),
        "conciliacao" => ReconciliationAsync(p, storeId),
        "vendas_fiscal" => SalesFiscalAsync(p, storeId),
        "divergencias" => DivergenceTableAsync(p, storeId),
        "cancelamentos" => CancellationsAsync(p, storeId),
        "devolucoes" => ReturnsAsync(p, storeId),
        "rejeitados" => RejectedAsync(p, storeId),
        "pendentes" => PendingAsync(p, storeId),
        "produtos" => ProductsAsync(p, storeId),
        "financeiro" => FinanceAsync(p),
        _ => throw new DomainException("invalid_report", "Relatório inexistente.", 404),
    };

    public async Task<ReportTable> CashAsync(AccountingPeriod p, Guid? storeId)
    {
        var sessions = await db.CashSessions.AsNoTracking().Where(c => c.OpenedAt >= p.From && c.OpenedAt < p.To && (storeId == null || c.StoreId == storeId))
            .OrderBy(c => c.OpenedAt).ToListAsync();
        var ids = sessions.Select(s => s.Id).ToList();
        var moves = await db.CashMovements.AsNoTracking().Where(m => ids.Contains(m.CashSessionId)).Select(m => new { m.CashSessionId, m.Type, m.AmountCents, m.ReferenceId }).ToListAsync();
        var sales = await db.Sales.AsNoTracking().Where(s => ids.Contains(s.CashSessionId))
            .Select(s => new { s.Id, s.CashSessionId, s.Status, s.TotalCents, s.DiscountCents, s.ChangeCents }).ToListAsync();
        var saleIds = sales.Select(s => s.Id).ToList();
        var pays = await db.SalePayments.AsNoTracking().Where(x => saleIds.Contains(x.SaleId)).Select(x => new { x.SaleId, x.Method, x.AmountCents }).ToListAsync();
        var refs = moves.Where(m => m.ReferenceId != null && m.Type == CashMovementType.SaleCancellation).Select(m => m.ReferenceId!.Value).Distinct().ToList();
        var returnIds = (await db.SaleReturns.AsNoTracking().Where(r => refs.Contains(r.Id)).Select(r => r.Id).ToListAsync()).ToHashSet();
        var users = await UserNamesAsync();
        var stores = await StoreNamesAsync();
        var completed = sales.Where(s => s.Status == SaleStatus.Completed).ToList();

        long T(Guid id, CashMovementType t, Func<Guid?, bool>? f = null) => moves.Where(m => m.CashSessionId == id && m.Type == t && (f is null || f(m.ReferenceId))).Sum(m => m.AmountCents);
        var sessionOf = completed.ToDictionary(s => s.Id, s => s.CashSessionId);
        long Pay(Guid id, params PaymentMethod[] ms) => pays.Where(x => ms.Contains(x.Method) && sessionOf.TryGetValue(x.SaleId, out var sid) && sid == id).Sum(x => x.AmountCents);
        var rows = new List<string[]>();
        long sOpen = 0, sSales = 0, sSupply = 0, sWithdraw = 0, sCancel = 0, sReturn = 0, sDisc = 0, sExpected = 0, sDeclared = 0, sDiff = 0;
        foreach (var c in sessions)
        {
            var mine = completed.Where(s => s.CashSessionId == c.Id).ToList();
            var cancelMoves = T(c.Id, CashMovementType.SaleCancellation, r => r is null || !returnIds.Contains(r.Value));
            var returnMoves = T(c.Id, CashMovementType.SaleCancellation, r => r is not null && returnIds.Contains(r.Value));
            var expected = c.ExpectedCashCents ?? moves.Where(m => m.CashSessionId == c.Id).Sum(m => m.AmountCents);
            sOpen += c.OpeningCents; sSales += mine.Sum(s => s.TotalCents); sSupply += T(c.Id, CashMovementType.Supply); sWithdraw += T(c.Id, CashMovementType.Withdrawal);
            sCancel += cancelMoves; sReturn += returnMoves; sDisc += mine.Sum(s => s.DiscountCents); sExpected += expected; sDeclared += c.DeclaredCashCents ?? 0; sDiff += c.DifferenceCents ?? 0;
            rows.Add([
                p.Local(c.OpenedAt), stores.GetValueOrDefault(c.StoreId, "?"), users.GetValueOrDefault(c.OperatorUserId, "?"), M(c.OpeningCents), mine.Count.ToString(), M(mine.Sum(s => s.TotalCents)),
                M(Pay(c.Id, PaymentMethod.Cash) - mine.Sum(s => s.ChangeCents)), M(Pay(c.Id, PaymentMethod.Pix)), M(Pay(c.Id, PaymentMethod.DebitCard, PaymentMethod.CreditCard)), M(Pay(c.Id, PaymentMethod.StoreCredit, PaymentMethod.Other)),
                M(T(c.Id, CashMovementType.Supply)), M(-T(c.Id, CashMovementType.Withdrawal)), M(T(c.Id, CashMovementType.ReceivableReceipt)), M(-cancelMoves), M(mine.Sum(s => s.DiscountCents)), M(-returnMoves),
                M(expected), c.DeclaredCashCents is { } d ? M(d) : "—", c.DifferenceCents is { } df ? M(df) : "—",
                c.Status == CashSessionStatus.Open ? "aberto" : "fechado", c.ClosedAt is { } ca ? p.Local(ca) : "—",
                c.ReviewedByUserId is { } rv ? users.GetValueOrDefault(rv, "?") : "—", Short(c.Id),
            ]);
        }
        return new("caixa", "Fechamento de caixa", p.Label,
            ["aberto_em", "loja", "operador (abertura e fechamento)", "saldo_inicial", "vendas_qtd", "vendas_total", "dinheiro", "pix", "cartoes", "crediario_outros", "suprimentos", "sangrias",
             "recebimentos_crediario", "cancelamentos_estornos", "descontos", "devolucoes_dinheiro", "saldo_final_esperado", "valor_contado", "diferenca", "situacao", "fechado_em", "revisado_por", "caixa"],
            rows,
            [new("Caixas", sessions.Count.ToString()), new("Saldo inicial", M(sOpen)), new("Vendas", M(sSales)), new("Suprimentos", M(sSupply)), new("Sangrias", M(-sWithdraw)),
             new("Cancelamentos/estornos (dinheiro)", M(-sCancel)), new("Devoluções em dinheiro", M(-sReturn)), new("Descontos", M(sDisc)), new("Esperado na gaveta", M(sExpected)),
             new("Contado", M(sDeclared)), new("Diferenças", M(sDiff)), new("Caixas ainda abertos", sessions.Count(s => s.Status == CashSessionStatus.Open).ToString())],
            ["Saldo esperado = abertura + vendas em dinheiro + suprimentos − sangrias − cancelamentos/devoluções em dinheiro.",
             "O sistema registra um operador por caixa (quem abre é quem fecha). Revisão = quem conferiu a diferença."]);
    }

    /// <summary>Movimentos de um caixa (consulta individual).</summary>
    public async Task<ReportTable> CashSessionAsync(Guid id, AccountingPeriod p)
    {
        var c = await db.CashSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) ?? throw new DomainException("not_found", "Caixa não encontrado.", 404);
        var users = await UserNamesAsync();
        var moves = await db.CashMovements.AsNoTracking().Where(m => m.CashSessionId == id).OrderBy(m => m.CreatedAt).ToListAsync();
        static string Type(CashMovementType t) => t switch
        {
            CashMovementType.Opening => "abertura", CashMovementType.Supply => "suprimento", CashMovementType.Withdrawal => "sangria",
            CashMovementType.CashSale => "venda em dinheiro", CashMovementType.SaleCancellation => "cancelamento/devolução", CashMovementType.ReceivableReceipt => "recebimento de crediário", _ => t.ToString(),
        };
        long run = 0;
        var rows = moves.Select(m => { run += m.AmountCents; return new[] { p.Local(m.CreatedAt), Type(m.Type), M(m.AmountCents), M(run), users.GetValueOrDefault(m.UserId, "?"), m.Reason ?? "" }; }).ToList();
        return new("caixa_detalhe", "Movimentos do caixa " + Short(id), p.Local(c.OpenedAt) + (c.ClosedAt is { } x ? " a " + p.Local(x) : " (aberto)"),
            ["data_hora", "tipo", "valor", "saldo_na_gaveta", "usuario", "motivo"], rows,
            [new("Esperado", M(c.ExpectedCashCents ?? run)), new("Contado", c.DeclaredCashCents is { } d ? M(d) : "—"), new("Diferença", c.DifferenceCents is { } df ? M(df) : "—")],
            c.Justification is { Length: > 0 } j ? ["Justificativa da diferença: " + j] : []);
    }

    private static string MethodName(PaymentMethod m) => m switch
    {
        PaymentMethod.Cash => "Dinheiro", PaymentMethod.Pix => "PIX", PaymentMethod.DebitCard => "Cartão de débito", PaymentMethod.CreditCard => "Cartão de crédito",
        PaymentMethod.StoreCredit => "Crediário (fiado)", _ => "Outros",
    };

    private sealed record PixRecon(long SalesPix, long Confirmed, List<string[]> Rows, int Divergences);

    private async Task<PixRecon> PixAsync(AccountingPeriod p, Guid? storeId, List<SaleRow> sales)
    {
        var completed = sales.Where(s => s.Status == SaleStatus.Completed).Select(s => s.Id).ToHashSet();
        var ids = completed.ToList();
        var pixPays = await db.SalePayments.AsNoTracking().Where(x => ids.Contains(x.SaleId) && x.Method == PaymentMethod.Pix).Select(x => new { x.SaleId, x.AmountCents }).ToListAsync();
        var sessionIds = await db.CashSessions.AsNoTracking().Where(c => storeId == null || c.StoreId == storeId).Select(c => c.Id).ToListAsync();
        var charges = await db.PosPixCharges.AsNoTracking()
            .Where(c => (c.UsedBySaleId != null && ids.Contains(c.UsedBySaleId.Value)) || (c.Status == PixChargeStatus.Approved && c.ApprovedAt >= p.From && c.ApprovedAt < p.To && sessionIds.Contains(c.CashSessionId)))
            .ToListAsync();
        var rows = new List<string[]>();
        var div = 0;
        foreach (var c in charges.OrderBy(c => c.CreatedAt))
        {
            string state;
            if (c.Status == PixChargeStatus.Approved && c.UsedBySaleId is null) { state = "PIX recebido sem venda vinculada"; div++; }
            else if (c.UsedBySaleId is { } sid && !completed.Contains(sid)) { state = "venda vinculada foi cancelada (verificar estorno)"; div++; }
            else state = c.Status == PixChargeStatus.Approved ? "conciliado" : "não pago (" + c.Status + ")";
            rows.Add([p.Local(c.CreatedAt), M(c.AmountCents), c.Status, c.MpPaymentId, c.UsedBySaleId is { } u ? Short(u) : "—", state]);
        }
        var bySale = charges.Where(c => c.Status == PixChargeStatus.Approved && c.UsedBySaleId != null).GroupBy(c => c.UsedBySaleId!.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.AmountCents));
        foreach (var g in pixPays.GroupBy(x => x.SaleId))
        {
            var paid = g.Sum(x => x.AmountCents);
            var conf = bySale.GetValueOrDefault(g.Key);
            if (conf == paid) continue;
            var sale = sales.First(s => s.Id == g.Key);
            rows.Add([p.Local(sale.At), M(paid), "sem cobrança", "—", Short(g.Key),
                conf == 0 ? "PIX registrado sem confirmação do Mercado Pago (PIX manual/maquininha)" : $"diferença: confirmado {M(conf)}"]);
            div++;
        }
        return new(pixPays.Sum(x => x.AmountCents), charges.Where(c => c.Status == PixChargeStatus.Approved && c.UsedBySaleId != null && completed.Contains(c.UsedBySaleId.Value)).Sum(c => c.AmountCents), rows, div);
    }

    public async Task<ReportTable> PaymentsAsync(AccountingPeriod p, Guid? storeId)
    {
        var sales = await SalesAsync(p, storeId);
        var ids = sales.Select(s => s.Id).ToList();
        var pays = await db.SalePayments.AsNoTracking().Where(x => ids.Contains(x.SaleId)).Select(x => new { x.SaleId, x.Method, x.AmountCents }).ToListAsync();
        var status = sales.ToDictionary(s => s.Id, s => s.Status);
        var change = sales.Where(s => s.Status == SaleStatus.Completed).Sum(s => s.Change);
        var returns = await db.SaleReturns.AsNoTracking().Where(r => r.CreatedAt >= p.From && r.CreatedAt < p.To).Join(db.Sales.Where(s => storeId == null || s.StoreId == storeId), r => r.SaleId, s => s.Id, (r, s) => r)
            .Select(r => new { r.RefundMethod, r.RefundCents }).ToListAsync();
        var pix = await PixAsync(p, storeId, sales);
        var rows = new List<string[]>();
        foreach (var m in new[] { PaymentMethod.Cash, PaymentMethod.Pix, PaymentMethod.DebitCard, PaymentMethod.CreditCard, PaymentMethod.StoreCredit, PaymentMethod.Other })
        {
            var ok = pays.Where(x => x.Method == m && status[x.SaleId] == SaleStatus.Completed).ToList();
            var cancelled = pays.Where(x => x.Method == m && status[x.SaleId] == SaleStatus.Cancelled).Sum(x => x.AmountCents);
            var gross = ok.Sum(x => x.AmountCents) - (m == PaymentMethod.Cash ? change : 0);
            var refunded = m == PaymentMethod.Cash ? returns.Where(r => r.RefundMethod == "cash").Sum(r => r.RefundCents) : 0;
            var conciliated = m switch { PaymentMethod.Pix => M(pix.Confirmed), PaymentMethod.Cash => "conferido no fechamento de caixa", _ => "sem integração com adquirente" };
            var diff = m == PaymentMethod.Pix ? M(pix.Confirmed - pix.SalesPix) : "—";
            rows.Add([MethodName(m), ok.Select(x => x.SaleId).Distinct().Count().ToString(), M(gross), M(cancelled), M(refunded), M(gross - refunded),
                "não disponível", "não disponível", conciliated, diff]);
        }
        var other = returns.Where(r => r.RefundMethod != "cash").Sum(r => r.RefundCents);
        return new("pagamentos", "Vendas por meio de pagamento", p.Label,
            ["meio", "transacoes", "valor_bruto_vendido", "cancelado", "estornado", "liquido_registrado", "taxas", "valor_liquido_previsto", "recebido_conciliado", "diferenca_conciliacao"],
            rows,
            [new("Total vendido (vendas concluídas)", M(sales.Where(s => s.Status == SaleStatus.Completed).Sum(s => s.Total))),
             new("Troco devolvido (já descontado do dinheiro)", M(change)), new("Devoluções em outras formas (crédito/estorno no meio original)", M(other)),
             new("PIX vendido × confirmado pelo Mercado Pago", $"{M(pix.SalesPix)} × {M(pix.Confirmed)}"), new("Divergências de conciliação PIX", pix.Divergences.ToString())],
            ["Valor vendido NÃO é o mesmo que valor recebido: dinheiro é conferido no fechamento do caixa; PIX do caixa é conciliado com o Mercado Pago;",
             "cartões (bandeira, adquirente, taxas, parcelas e valor líquido) dependem do extrato da adquirente — não há integração com maquininha no sistema.",
             "Vale-alimentação, vale-refeição e transferência não são formas cadastradas no caixa; quando usadas, aparecem em \"Outros\"."]);
    }

    public async Task<ReportTable> ReconciliationAsync(AccountingPeriod p, Guid? storeId)
    {
        var pix = await PixAsync(p, storeId, await SalesAsync(p, storeId));
        return new("conciliacao", "Conciliação de pagamentos (PIX no caixa)", p.Label, ["data_hora", "valor", "situacao_mp", "id_pagamento_mp", "venda", "resultado"], pix.Rows,
            [new("PIX nas vendas", M(pix.SalesPix)), new("Confirmado pelo Mercado Pago", M(pix.Confirmed)), new("Divergências", pix.Divergences.ToString())],
            ["Cartões: sem integração com adquirente; concilie pelo extrato da maquininha."]);
    }

    /// <summary>Divergências entre vendas e documentos fiscais (para análise; nada é corrigido automaticamente).</summary>
    public async Task<List<Divergence>> DivergencesAsync(AccountingPeriod p, Guid? storeId)
    {
        var sales = await SalesAsync(p, storeId);
        var docs = await DocsBySaleAsync(sales.Select(s => s.Id).ToList());
        var fiscalOn = await FiscalEnabledAsync() && await EmissionModeAsync() == NfceModes.Always;
        var archived = await ArchivedAsync(docs.Values.Select(d => d.Id));
        var (_, nfByDoc) = await TaxesAsync(docs.Values.Where(d => d.Status == FiscalStatus.Authorized).Select(d => d.Id));
        var list = new List<Divergence>();
        static string DocLabel(DocRow d) => d.Number is { } n ? $"NFC-e {n}/{d.Series}" : "NFC-e sem número";
        foreach (var s in sales)
        {
            docs.TryGetValue(s.Id, out var d);
            var when = p.Local(s.At);
            if (s.Status == SaleStatus.Completed)
            {
                if (d is null) { if (fiscalOn) list.Add(new("venda_sem_documento", when, Short(s.Id), "—", s.Total, null, "Venda concluída sem documento fiscal registrado.")); continue; }
                if (d.Status is FiscalStatus.PendingTransmission or FiscalStatus.Contingency)
                    list.Add(new("documento_pendente", when, Short(s.Id), DocLabel(d), s.Total, null, "Nota ainda não autorizada: " + (d.LastMessage ?? "")));
                else if (d.Status == FiscalStatus.Rejected)
                    list.Add(new("documento_rejeitado", when, Short(s.Id), DocLabel(d), s.Total, null, "Rejeitada: " + (d.LastMessage ?? "")));
                else if (d.Status == FiscalStatus.Denied)
                    list.Add(new("documento_denegado", when, Short(s.Id), DocLabel(d), s.Total, null, "Uso denegado pela SEFAZ: " + (d.LastMessage ?? "")));
                else if (d.Status == FiscalStatus.Cancelled && d.AuthorizedAt != null)
                    list.Add(new("nota_cancelada_venda_ativa", when, Short(s.Id), DocLabel(d), s.Total, null, "Nota cancelada na SEFAZ, mas a venda continua ativa."));
            }
            else if (d is { Status: FiscalStatus.Authorized })
                list.Add(new("venda_cancelada_nota_autorizada", when, Short(s.Id), DocLabel(d), s.Total, null, "Venda cancelada e a NFC-e continua autorizada (falta o evento de cancelamento ou nota de devolução)."));
            if (d is { Status: FiscalStatus.Authorized } && nfByDoc.TryGetValue(d.Id, out var nf) && nf != s.Total)
                list.Add(new("valor_divergente", when, Short(s.Id), DocLabel(d), s.Total, nf, "Valor da nota (vNF) diferente do total da venda."));
            if (d is not null && NeedsAuthorizationXml(d.Status, d.AuthorizedAt) && !archived.Contains((d.Id, FiscalXmlKind.Authorization)))
                list.Add(new("xml_ausente", when, Short(s.Id), DocLabel(d), s.Total, null, d.XmlUrl is null
                    ? "Nota autorizada sem XML original: o provedor não informou o link — baixe no painel do provedor."
                    : "XML original ainda não arquivado (o sistema tenta baixar do provedor fiscal)."));
            if (d is { Status: FiscalStatus.Cancelled } && d.AuthorizedAt != null && !archived.Contains((d.Id, FiscalXmlKind.Cancellation)))
                list.Add(new("evento_ausente", when, Short(s.Id), DocLabel(d), s.Total, null, "Cancelamento sem o XML do evento no arquivo fiscal."));
        }
        return list;
    }

    private async Task<ReportTable> DivergenceTableAsync(AccountingPeriod p, Guid? storeId)
    {
        var list = await DivergencesAsync(p, storeId);
        return new("divergencias", "Divergências para análise", p.Label, ["tipo", "data_hora", "venda", "documento", "valor_venda", "valor_nota", "detalhe"],
            list.Select(d => new[] { d.Type, d.When, d.Sale, d.Document, M(d.SaleCents), d.DocumentCents is { } c ? M(c) : "—", d.Detail }).ToList(),
            list.GroupBy(d => d.Type).Select(g => new ReportTotal(g.Key, g.Count().ToString())).ToList(),
            ["O sistema apenas aponta as diferenças. Correções fiscais (cancelamento, nota de devolução, inutilização) dependem de decisão sua e do contador."]);
    }

    public async Task<ReportTable> SalesFiscalAsync(AccountingPeriod p, Guid? storeId)
    {
        var sales = await SalesAsync(p, storeId);
        var docs = await DocsBySaleAsync(sales.Select(s => s.Id).ToList());
        var (tax, _) = await TaxesAsync(docs.Values.Where(d => d.Status == FiscalStatus.Authorized).Select(d => d.Id));
        var divergences = await DivergencesAsync(p, storeId);
        var ok = sales.Where(s => s.Status == SaleStatus.Completed).ToList();
        int Count(Func<DocRow, bool> f) => docs.Values.Count(f);
        long Sum(Func<DocRow, bool> f) => sales.Where(s => docs.TryGetValue(s.Id, out var d) && f(d)).Sum(s => s.Total);
        var rows = sales.GroupBy(s => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.At, p.Tz).DateTime)).OrderBy(g => g.Key).Select(g =>
        {
            var done = g.Where(s => s.Status == SaleStatus.Completed).ToList();
            var auth = done.Where(s => docs.TryGetValue(s.Id, out var d) && d.Status == FiscalStatus.Authorized).ToList();
            return new[] { g.Key.ToString("dd/MM/yyyy"), done.Count.ToString(), M(done.Sum(s => s.Total)), auth.Count.ToString(), M(auth.Sum(s => s.Total)), M(done.Sum(s => s.Total) - auth.Sum(s => s.Total)),
                g.Count(s => s.Status == SaleStatus.Cancelled).ToString(), M(done.Sum(s => s.Discount)), M(done.Sum(s => s.Returned)) };
        }).ToList();
        return new("vendas_fiscal", "Vendas e documentos fiscais", p.Label,
            ["dia", "vendas_qtd", "vendas_valor", "nfce_autorizadas_qtd", "nfce_autorizadas_valor", "diferenca_sem_nota", "vendas_canceladas", "descontos", "devolucoes"],
            rows,
            [new("Vendas concluídas", $"{ok.Count} • {M(ok.Sum(s => s.Total))}"), new("Vendas canceladas", $"{sales.Count(s => s.Status == SaleStatus.Cancelled)} • {M(sales.Where(s => s.Status == SaleStatus.Cancelled).Sum(s => s.Total))}"),
             new("Descontos", M(ok.Sum(s => s.Discount))), new("Devoluções", M(ok.Sum(s => s.Returned))),
             new("NFC-e autorizadas", $"{Count(d => d.Status == FiscalStatus.Authorized)} • {M(Sum(d => d.Status == FiscalStatus.Authorized))}"),
             new("NFC-e canceladas na SEFAZ", $"{Count(d => d.Status == FiscalStatus.Cancelled && d.AuthorizedAt != null)} • {M(Sum(d => d.Status == FiscalStatus.Cancelled && d.AuthorizedAt != null))}"),
             new("Rejeitadas", Count(d => d.Status == FiscalStatus.Rejected).ToString()), new("Denegadas", Count(d => d.Status == FiscalStatus.Denied).ToString()),
             new("Pendentes/contingência", Count(d => d.Status is FiscalStatus.PendingTransmission or FiscalStatus.Contingency).ToString()),
             new("Descartadas (venda cancelada antes de transmitir)", Count(d => d.Status == FiscalStatus.Cancelled && d.AuthorizedAt == null).ToString()),
             new($"Tributos nos XMLs ({tax.Files} notas)", $"vNF {M(tax.Nf)} • ICMS {M(tax.Icms)} • PIS {M(tax.Pis)} • COFINS {M(tax.Cofins)} • IBS {M(tax.Ibs)} • CBS {M(tax.Cbs)} • aprox. tributos {M(tax.TotTrib)}"),
             new("Divergências", divergences.Count.ToString())],
            ["Tributos: somados dos XMLs autorizados guardados no arquivo fiscal (notas sem XML arquivado não entram).", "Veja o relatório \"Divergências\" para o detalhe de cada diferença."]);
    }

    public async Task<ReportTable> CancellationsAsync(AccountingPeriod p, Guid? storeId)
    {
        var sales = (await SalesAsync(p, storeId)).Where(s => s.Status == SaleStatus.Cancelled).ToList();
        var docs = await DocsBySaleAsync(sales.Select(s => s.Id).ToList());
        var users = await UserNamesAsync();
        var by = await db.Sales.AsNoTracking().Where(s => sales.Select(x => x.Id).Contains(s.Id)).Select(s => new { s.Id, s.CancelledByUserId }).ToDictionaryAsync(s => s.Id, s => s.CancelledByUserId);
        return new("cancelamentos", "Cancelamentos de venda", p.Label, ["venda_em", "cancelada_em", "venda", "valor", "motivo", "cancelada_por", "documento_fiscal"],
            sales.Select(s => new[] { p.Local(s.At), s.CancelledAt is { } c ? p.Local(c) : "—", Short(s.Id), M(s.Total), s.CancelReason ?? "",
                by.GetValueOrDefault(s.Id) is { } u ? users.GetValueOrDefault(u, "?") : "—",
                docs.TryGetValue(s.Id, out var d) ? FiscalArchiveService.StatusKey(d.Status, d.AuthorizedAt) + (d.Number is { } n ? $" (NFC-e {n})" : "") : "sem documento" }).ToList(),
            [new("Cancelamentos", sales.Count.ToString()), new("Valor", M(sales.Sum(s => s.Total)))], []);
    }

    public async Task<ReportTable> ReturnsAsync(AccountingPeriod p, Guid? storeId)
    {
        var rets = await db.SaleReturns.AsNoTracking().Where(r => r.CreatedAt >= p.From && r.CreatedAt < p.To)
            .Join(db.Sales.AsNoTracking().Where(s => storeId == null || s.StoreId == storeId), r => r.SaleId, s => s.Id, (r, s) => new { r, s.ReceivedAt })
            .OrderBy(x => x.r.CreatedAt).ToListAsync();
        var users = await UserNamesAsync();
        static string Method(string m) => m switch { "cash" => "dinheiro", "original" => "estorno no meio original", "store_credit" => "crédito na loja", _ => m };
        return new("devolucoes", "Devoluções", p.Label, ["devolvido_em", "venda_em", "venda", "valor", "forma_reembolso", "voltou_ao_estoque", "motivo", "usuario"],
            rets.Select(x => new[] { p.Local(x.r.CreatedAt), p.Local(x.ReceivedAt), Short(x.r.SaleId), M(x.r.RefundCents), Method(x.r.RefundMethod), x.r.Restock ? "sim" : "não", x.r.Reason,
                users.GetValueOrDefault(x.r.UserId, "?") }).ToList(),
            [new("Devoluções", rets.Count.ToString()), new("Valor", M(rets.Sum(x => x.r.RefundCents)))],
            ["Nota fiscal de devolução/entrada depende do provedor fiscal e da orientação do contador."]);
    }

    private async Task<List<(DocRow D, SaleRow S)>> DocsInPeriodAsync(AccountingPeriod p, Guid? storeId)
    {
        var sales = await SalesAsync(p, storeId);
        var docs = await DocsBySaleAsync(sales.Select(s => s.Id).ToList());
        return sales.Where(s => docs.ContainsKey(s.Id)).Select(s => (docs[s.Id], s)).ToList();
    }

    public async Task<ReportTable> RejectedAsync(AccountingPeriod p, Guid? storeId)
    {
        var list = (await DocsInPeriodAsync(p, storeId)).Where(x => x.D.Status is FiscalStatus.Rejected or FiscalStatus.Denied).ToList();
        return new("rejeitados", "Documentos rejeitados e denegados", p.Label, ["venda_em", "venda", "valor", "situacao", "numero", "tentativas", "mensagem_sefaz"],
            list.Select(x => new[] { p.Local(x.S.At), Short(x.S.Id), M(x.S.Total), FiscalArchiveService.StatusKey(x.D.Status, x.D.AuthorizedAt), x.D.Number?.ToString() ?? "—",
                x.D.Attempts.ToString(), x.D.LastMessage ?? "" }).ToList(),
            [new("Rejeitados", list.Count(x => x.D.Status == FiscalStatus.Rejected).ToString()), new("Denegados", list.Count(x => x.D.Status == FiscalStatus.Denied).ToString())],
            ["Rejeitada: corrija o cadastro (NCM, CFOP, CST…) e transmita de novo. Denegada: a numeração fica usada e a nota não tem validade — fale com o contador."]);
    }

    public async Task<ReportTable> PendingAsync(AccountingPeriod p, Guid? storeId)
    {
        var all = await DocsInPeriodAsync(p, storeId);
        var archived = await ArchivedAsync(all.Select(x => x.D.Id));
        var rows = new List<string[]>();
        foreach (var (d, s) in all)
        {
            string? why = d.Status switch
            {
                FiscalStatus.PendingTransmission => "aguardando transmissão",
                FiscalStatus.Contingency => "emitida em contingência — falta transmitir à SEFAZ",
                _ => null,
            };
            if (why is null && NeedsAuthorizationXml(d.Status, d.AuthorizedAt) && !archived.Contains((d.Id, FiscalXmlKind.Authorization)))
                why = "XML original não arquivado";
            if (why is null && d.Status == FiscalStatus.Cancelled && d.AuthorizedAt != null && !archived.Contains((d.Id, FiscalXmlKind.Cancellation)))
                why = "XML do cancelamento não arquivado";
            if (why is null) continue;
            rows.Add([p.Local(s.At), Short(s.Id), M(s.Total), FiscalArchiveService.StatusKey(d.Status, d.AuthorizedAt), why, d.NextAttemptAt is { } n ? p.Local(n) : "—", d.LastMessage ?? ""]);
        }
        return new("pendentes", "Documentos pendentes", p.Label, ["venda_em", "venda", "valor", "situacao", "pendencia", "proxima_tentativa", "ultima_mensagem"], rows,
            [new("Pendências", rows.Count.ToString())], []);
    }

    public async Task<ReportTable> ProductsAsync(AccountingPeriod p, Guid? storeId)
    {
        var ids = (await SalesAsync(p, storeId)).Where(s => s.Status == SaleStatus.Completed).Select(s => s.Id).ToList();
        var items = new List<SaleItem>();
        foreach (var chunk in ids.Chunk(1000)) items.AddRange(await db.SaleItems.AsNoTracking().Where(i => chunk.Contains(i.SaleId)).ToListAsync());
        var skus = await db.Products.AsNoTracking().Select(x => new { x.Id, x.Sku, x.Ncm }).ToDictionaryAsync(x => x.Id);
        var rows = items.GroupBy(i => i.ProductId).Select(g => new
        {
            g.Key, name = g.First().ProductName, qty = g.Sum(i => i.Quantity), gross = g.Sum(i => (long)Math.Round(i.UnitPriceCents * i.Quantity)), disc = g.Sum(i => i.DiscountCents),
            total = g.Sum(i => i.TotalCents), cost = g.Sum(i => (long)Math.Round(i.UnitCostCents * i.Quantity)),
        }).OrderByDescending(x => x.total).ToList();
        return new("produtos", "Vendas por produto", p.Label, ["sku", "produto", "ncm", "quantidade", "valor_bruto", "descontos", "total", "custo", "margem"],
            rows.Select(x => new[] { skus.GetValueOrDefault(x.Key)?.Sku ?? "", x.name, skus.GetValueOrDefault(x.Key)?.Ncm ?? "", x.qty.ToString("0.###", Br), M(x.gross), M(x.disc), M(x.total), M(x.cost), M(x.total - x.cost) }).ToList(),
            [new("Produtos", rows.Count.ToString()), new("Total", M(rows.Sum(x => x.total))), new("Custo", M(rows.Sum(x => x.cost)))],
            ["Desconto no total da venda não está distribuído por item neste relatório."]);
    }

    public async Task<ReportTable> FinanceAsync(AccountingPeriod p)
    {
        var titles = await db.FinancialTitles.AsNoTracking().Where(t => t.Status == TitleStatus.Paid && t.PaidOn >= p.FirstDay && t.PaidOn <= p.LastDay).OrderBy(t => t.PaidOn).ToListAsync();
        long In(TitleKind k) => titles.Where(t => t.Kind == k).Sum(t => t.PaidAmountCents ?? t.AmountCents);
        return new("financeiro", "Movimentações financeiras (contas pagas e recebidas)", p.Label, ["pago_em", "tipo", "categoria", "descricao", "fornecedor_cliente", "valor", "forma"],
            titles.Select(t => new[] { t.PaidOn?.ToString("dd/MM/yyyy") ?? "", t.Kind == TitleKind.Payable ? "pagar" : "receber", t.Category, t.Description, t.Counterparty ?? "",
                M(t.PaidAmountCents ?? t.AmountCents), t.PaymentMethod ?? "" }).ToList(),
            [new("Recebido", M(In(TitleKind.Receivable))), new("Pago", M(In(TitleKind.Payable))), new("Saldo", M(In(TitleKind.Receivable) - In(TitleKind.Payable)))], []);
    }

    // ---------------- Visão geral ----------------

    public async Task<object> OverviewAsync(AccountingPeriod p, Guid? storeId)
    {
        var all = await DocsInPeriodAsync(p, storeId);
        var archived = await ArchivedAsync(all.Select(x => x.D.Id));
        var sales = await SalesAsync(p, storeId);
        var ids = sales.Where(s => s.Status == SaleStatus.Completed).Select(s => s.Id).ToList();
        var pays = await db.SalePayments.AsNoTracking().Where(x => ids.Contains(x.SaleId)).GroupBy(x => x.Method).Select(g => new { g.Key, cents = g.Sum(x => x.AmountCents) }).ToListAsync();
        int C(Func<DocRow, bool> f) => all.Count(x => f(x.D));
        long V(Func<DocRow, bool> f) => all.Where(x => f(x.D)).Sum(x => x.S.Total);
        var missingXml = all.Count(x => NeedsAuthorizationXml(x.D.Status, x.D.AuthorizedAt) && !archived.Contains((x.D.Id, FiscalXmlKind.Authorization)));
        var openCash = await db.CashSessions.AsNoTracking().CountAsync(c => c.Status == CashSessionStatus.Open && c.OpenedAt < p.To && (storeId == null || c.StoreId == storeId));
        var lastDelivery = await db.AccountantDeliveries.AsNoTracking().OrderByDescending(d => d.CreatedAt).FirstOrDefaultAsync();
        var closing = await db.FiscalMonthClosings.AsNoTracking().FirstOrDefaultAsync(c => c.Year == p.LastDay.Year && c.Month == p.LastDay.Month);
        var pending = new List<string>();
        if (C(d => d.Status == FiscalStatus.PendingTransmission) is > 0 and var pt) pending.Add($"{pt} nota(s) aguardando transmissão");
        if (C(d => d.Status == FiscalStatus.Contingency) is > 0 and var ct) pending.Add($"{ct} nota(s) em contingência sem transmitir");
        if (C(d => d.Status == FiscalStatus.Rejected) is > 0 and var rj) pending.Add($"{rj} nota(s) rejeitada(s) para corrigir");
        if (missingXml > 0) pending.Add($"{missingXml} nota(s) autorizada(s) sem XML original arquivado");
        if (openCash > 0) pending.Add($"{openCash} caixa(s) ainda aberto(s)");
        return new
        {
            period = p.Label, from = p.FirstDay, to = p.LastDay, fiscalEnabled = await FiscalEnabledAsync(),
            authorized = C(d => d.Status == FiscalStatus.Authorized), cancelled = C(d => d.Status == FiscalStatus.Cancelled && d.AuthorizedAt != null),
            rejected = C(d => d.Status == FiscalStatus.Rejected), denied = C(d => d.Status == FiscalStatus.Denied),
            pending = C(d => d.Status is FiscalStatus.PendingTransmission or FiscalStatus.Contingency), missingXml,
            authorizedCents = V(d => d.Status == FiscalStatus.Authorized), cancelledCents = V(d => d.Status == FiscalStatus.Cancelled && d.AuthorizedAt != null),
            salesCents = sales.Where(s => s.Status == SaleStatus.Completed).Sum(s => s.Total), salesCount = ids.Count,
            payments = pays.Select(x => new { method = MethodName(x.Key), x.cents }).OrderByDescending(x => x.cents),
            closing = closing is null ? "não iniciado" : closing.Status == ClosingStatus.Closed ? "concluído" : "em andamento",
            lastDelivery = lastDelivery is null ? null : new { lastDelivery.CreatedAt, lastDelivery.Year, lastDelivery.Month, lastDelivery.Status, lastDelivery.Recipients },
            pendencies = pending,
        };
    }

    // ---------------- CSV ----------------

    public static string Csv(ReportTable t)
    {
        static string Cell(string v)
        {
            v = v.Replace("\r", " ").Replace("\n", " ");
            if (v.Length > 0 && "=+-@".Contains(v[0]) && !decimal.TryParse(v, NumberStyles.Number, Br, out _)) v = "'" + v; // evita fórmula no Excel
            return v.Contains(';') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
        var sb = new StringBuilder("﻿");
        sb.Append(string.Join(";", t.Headers.Select(Cell))).Append("\r\n");
        foreach (var r in t.Rows) sb.Append(string.Join(";", r.Select(Cell))).Append("\r\n");
        if (t.Totals.Count > 0)
        {
            sb.Append("\r\n");
            foreach (var x in t.Totals) sb.Append(Cell("TOTAL: " + x.Label)).Append(';').Append(Cell(x.Value)).Append("\r\n");
        }
        foreach (var n in t.Notes) sb.Append(Cell("Obs.: " + n)).Append("\r\n");
        return sb.ToString();
    }
}

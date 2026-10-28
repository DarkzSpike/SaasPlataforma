using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using Comercial.Desktop.Core;

namespace Comercial.Desktop;

public sealed record Kpi(string Title, string Value, string Sub, string Glyph, string Tone);
public sealed record Bar(string Label, double RevenueHeight, double ProfitHeight, string Tooltip);
public sealed record PeriodOption(string Key, string Title);

/// <summary>Página da área do dono: dados vêm do servidor (que revalida permissão e módulo).</summary>
public abstract class OwnerPage : ObservableObject
{
    private bool _loading;
    private string _error = "";
    private PeriodOption _period;
    protected OwnerPage(ShellViewModel shell, string title, string subtitle)
    {
        Shell = shell; Title = title; Subtitle = subtitle;
        _period = Periods[0];
        RefreshCommand = new RelayCommand(_ => LoadAsync());
    }
    public ShellViewModel Shell { get; }
    protected AppServices S => Shell.S;
    public string Title { get; }
    public string Subtitle { get; }
    public bool Loading { get => _loading; set => Set(ref _loading, value); }
    public string Error { get => _error; set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public RelayCommand RefreshCommand { get; }
    public virtual bool HasPeriod => false;
    public IReadOnlyList<PeriodOption> Periods { get; } = [new("today", "Hoje"), new("7d", "7 dias"), new("30d", "30 dias"), new("month", "Este mês")];
    public PeriodOption Period { get => _period; set { if (Set(ref _period, value)) _ = LoadAsync(); } }

    protected (DateTimeOffset From, DateTimeOffset To) Range()
    {
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Date, now.Offset);
        return Period.Key switch
        {
            "7d" => (today.AddDays(-6), now.AddMinutes(1)),
            "30d" => (today.AddDays(-29), now.AddMinutes(1)),
            "month" => (new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset), now.AddMinutes(1)),
            _ => (today, now.AddMinutes(1)),
        };
    }

    protected static string Q(DateTimeOffset d) => Uri.EscapeDataString(d.ToString("O"));

    /// <summary>Exportação CSV (módulo Relatórios premium): o servidor gera o arquivo e registra na auditoria.</summary>
    public bool CanExport => S.Session.Can("reports.export", "relatorios_premium");

    protected async Task ExportCsvAsync(string path, string name)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"{name}-{DateTime.Now:yyyyMMdd-HHmm}.csv", Filter = "Planilha CSV (*.csv)|*.csv" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bytes = await S.Api.GetBytesAsync(path) ?? [];
            await System.IO.File.WriteAllBytesAsync(dlg.FileName, bytes);
        }
        catch (ApiException ex) { Error = "Não foi possível exportar: " + ex.Code; }
        catch (HttpRequestException) { Error = "Exportar exige conexão com o servidor."; }
    }

    public async Task LoadAsync()
    {
        Loading = true; Error = "";
        try { await LoadCoreAsync(); Shell.MarkOnline(true); }
        catch (ApiException ex)
        {
            Error = ex.Code switch
            {
                "module_not_enabled" => "Este módulo não está contratado para a sua empresa.",
                "permission_denied" => "Seu usuário não tem permissão para ver esta área.",
                _ => "Não foi possível carregar: " + ex.Code,
            };
        }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor. Esta área precisa de internet."; Shell.MarkOnline(false); }
        finally { Loading = false; }
    }

    protected abstract Task LoadCoreAsync();

    public static IEnumerable<Bar> BuildBars(IEnumerable<(string Label, long Revenue, long Profit)> days)
    {
        var list = days.ToList();
        var max = Math.Max(1, list.Count == 0 ? 1 : list.Max(d => d.Revenue));
        return list.Select(d => new Bar(d.Label, 160.0 * d.Revenue / max, 160.0 * Math.Max(0, d.Profit) / max,
            $"{d.Label}: vendas {AppServices.Money(d.Revenue)} • lucro {AppServices.Money(d.Profit)}"));
    }
}

public sealed class DashboardViewModel(ShellViewModel shell) : OwnerPage(shell, "Painel do dono", "Resumo da loja com os dados já sincronizados")
{
    public ObservableCollection<Kpi> Kpis { get; } = [];
    public ObservableCollection<Bar> Bars { get; } = [];
    public ObservableCollection<SaleRow> Recent { get; } = [];
    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }
    public bool HasBars => Bars.Count > 0;

    protected override async Task LoadCoreAsync()
    {
        var d = await S.Api.GetAsync("/api/v1/mobile/dashboard");
        long? Opt(string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        Kpis.Clear();
        Kpis.Add(new Kpi("Faturamento hoje", AppServices.Money(d.GetProperty("todayRevenueCents").GetInt64()), $"bruto • {d.GetProperty("todaySalesCount").GetInt32()} venda(s)", "\uE7BF", "accent"));
        if (Opt("todayGrossProfitCents") is { } tp) Kpis.Add(new Kpi("Lucro do dia", AppServices.Money(tp), "vendas − custo dos produtos", "\uE8C7", "primary"));
        Kpis.Add(new Kpi("Faturamento no mês", AppServices.Money(d.GetProperty("monthRevenueCents").GetInt64()), $"bruto • {d.GetProperty("monthSalesCount").GetInt32()} venda(s)", "\uE787", "accent"));
        if (Opt("monthGrossProfitCents") is { } mp) Kpis.Add(new Kpi("Lucro do mês", AppServices.Money(mp), "vendas − custo dos produtos", "\uE8C7", "primary"));
        Kpis.Add(new Kpi("Caixas abertos", d.GetProperty("openCashSessions").GetInt32().ToString(), "agora", Icons.Bank, "neutral"));
        var shortToday = d.GetProperty("todayCashShortageCents").GetInt64();
        var shortMonth = d.GetProperty("monthCashShortageCents").GetInt64();
        var shortCount = d.GetProperty("monthCashShortageCount").GetInt32();
        Kpis.Add(new Kpi("Quebra de caixa", AppServices.Money(shortMonth),
            shortCount == 0 ? "nenhuma falta no mês" : $"hoje {AppServices.Money(shortToday)} · {shortCount} fechamento(s) no mês",
            Icons.Money, shortMonth > 0 ? "danger" : "neutral"));
        var low = d.GetProperty("lowStockProducts").GetInt32();
        Kpis.Add(new Kpi("Estoque baixo", low.ToString(), low == 0 ? "tudo certo" : "produto(s) para repor", "\uE7B8", low == 0 ? "neutral" : "warn"));
        Note = d.GetProperty("note").GetString() ?? "";

        Bars.Clear();
        if (S.Session.Can("reports.financial", "lucratividade"))
        {
            var now = DateTimeOffset.Now;
            var from = new DateTimeOffset(now.Date, now.Offset).AddDays(-13);
            var p = await S.Api.GetAsync($"/api/v1/reports/profitability?from={Q(from)}&to={Q(now.AddMinutes(1))}");
            var byDay = p.GetProperty("daily").EnumerateArray().ToDictionary(x => DateOnly.Parse(x.GetProperty("date").GetString()!), x => x);
            var days = Enumerable.Range(0, 14).Select(i => DateOnly.FromDateTime(from.Date.AddDays(i))).Select(day =>
                byDay.TryGetValue(day, out var x)
                    ? (day.ToString("dd/MM"), x.GetProperty("revenueCents").GetInt64(), x.GetProperty("grossProfitCents").GetInt64())
                    : (day.ToString("dd/MM"), 0L, 0L));
            foreach (var b in BuildBars(days)) Bars.Add(b);
        }
        Raise(nameof(HasBars));

        Recent.Clear();
        if (S.Session.Can("sales.read"))
            foreach (var s in (await S.Api.GetAsync("/api/v1/sales")).EnumerateArray().Take(6)) Recent.Add(SaleRow.From(s));
    }
}

public sealed record SaleRow(Guid Id, string When, string Operator, string Items, string Payment, string Total, string Status, bool Cancelled, bool Offline)
{
    public static readonly string[] MethodNames = ["", "Dinheiro", "Débito", "Crédito", "PIX"];

    public static SaleRow From(JsonElement s)
    {
        var pays = s.GetProperty("payments").EnumerateArray().Select(p => p.GetProperty("method").GetInt32()).Distinct()
            .Select(m => m is > 0 and < 5 ? MethodNames[m] : "Outro");
        var cancelled = s.GetProperty("status").GetString() == "Cancelled";
        return new SaleRow(s.GetProperty("id").GetGuid(), s.GetProperty("receivedAt").GetDateTimeOffset().LocalDateTime.ToString("dd/MM HH:mm"),
            s.GetProperty("operatorName").GetString() ?? "—",
            $"{s.GetProperty("quantity").GetDecimal().ToString("0.###", AppServices.PtBr)} un. ({s.GetProperty("itemsCount").GetInt32()} prod.)",
            string.Join(" + ", pays), AppServices.Money(s.GetProperty("totalCents").GetInt64()),
            cancelled ? "Cancelada" : "Concluída", cancelled, s.GetProperty("createdOffline").GetBoolean());
    }
}

public sealed record DetailLine(string Name, string Quantity, string Unit, string Total, Guid ItemId = default, decimal Qty = 0, long UnitCents = 0, long TotalCents = 0);

public sealed class HistoryViewModel : OwnerPage
{
    private SaleRow? _selected;
    private string _detailTitle = "", _detailInfo = "", _summary = "";
    private JsonElement? _detailSale;
    public HistoryViewModel(ShellViewModel shell) : base(shell, "Histórico de vendas", "Todas as vendas recebidas pelo servidor, inclusive as feitas offline")
    {
        CancelSaleCommand = RelayCommand.Sync(_ => AskCancel(), _ => Selected is { Cancelled: false } && S.Session.Can("sales.cancel"));
        ReturnCommand = RelayCommand.Sync(_ => ReturnForm(), _ => Selected is { Cancelled: false } && S.Session.Can("sales.return", "trocas"));
        ReprintCommand = RelayCommand.Sync(_ => Reprint(), _ => Selected is not null && Detail.Count > 0);
        ExportCommand = new RelayCommand(_ => { var (f, t) = Range(); return ExportCsvAsync($"/api/v1/reports/export/sales.csv?from={Q(f)}&to={Q(t)}", "vendas"); });
    }
    public RelayCommand ReturnCommand { get; }
    public RelayCommand ReprintCommand { get; }
    public RelayCommand ExportCommand { get; }
    public bool CanReturn => S.Session.Can("sales.return", "trocas");

    private void Reprint()
    {
        var s = _detailSale!.Value;
        var paid = s.GetProperty("payments").EnumerateArray().Sum(p => p.GetProperty("amountCents").GetInt64());
        ReceiptPrinter.Print(new Receipt(Selected!.When is { } w && DateTime.TryParseExact(w, "dd/MM HH:mm", AppServices.PtBr, System.Globalization.DateTimeStyles.None, out var d) ? d : DateTime.Now,
            Detail.Select(l => new ReceiptLine(l.Name, l.Qty, l.UnitCents, l.TotalCents)).ToList(), s.GetProperty("totalCents").GetInt64(),
            s.GetProperty("discountCents").GetInt64(), paid, Selected.Payment, Reference: "2ª via"));
    }

    /// <summary>Devolução parcial/total: uma linha de quantidade por item, retorno ao estoque e forma de reembolso.</summary>
    private void ReturnForm()
    {
        var row = Selected!;
        var f = new FormViewModel { Title = "Devolução de itens", Message = $"Venda de {row.When} • {row.Total}. Informe só as quantidades devolvidas.", ConfirmText = "Registrar devolução" };
        foreach (var l in Detail) f.Fields.Add(new FormField("item:" + l.ItemId, $"{l.Name} (vendido: {l.Quantity})", "", Icons.Box, "0"));
        f.Fields.Add(new FormField("restock", "O produto volta para o estoque?", "Sim, está em condição de venda", options: ["Sim, está em condição de venda", "Não (avariado/defeito)"]));
        f.Fields.Add(new FormField("refund", "Reembolso", "Dinheiro do caixa", options: ["Dinheiro do caixa", "Mesma forma do pagamento", "Crédito na loja"]));
        f.Fields.Add(new FormField("reason", "Motivo", "", Icons.Edit));
        Shell.ShowForm(f, async form =>
        {
            var items = new List<object>();
            foreach (var fl in form.Fields.Where(x => x.Key.StartsWith("item:") && x.Value.Trim().Length > 0))
            {
                if (!decimal.TryParse(fl.Value, System.Globalization.NumberStyles.Number, AppServices.PtBr, out var q) || q < 0) { form.Error = "Quantidade inválida."; return false; }
                if (q > 0) items.Add(new { saleItemId = Guid.Parse(fl.Key[5..]), quantity = q });
            }
            if (items.Count == 0) { form.Error = "Informe a quantidade de ao menos um item."; return false; }
            if (form["reason"].Length < 5) { form.Error = "Informe o motivo."; return false; }
            var refund = form["refund"] switch { "Dinheiro do caixa" => "cash", "Crédito na loja" => "store_credit", _ => "original" };
            try
            {
                var r = await S.Api.PostAsync($"/api/v1/sales/{row.Id}/returns", new { items, reason = form["reason"], restock = form["restock"].StartsWith("Sim"), refundMethod = refund });
                Shell.Pos.Status = $"Devolução registrada: devolver {AppServices.Money(r.GetProperty("refundCents").GetInt64())} ao cliente.";
            }
            catch (ApiException ex) when (ex.Code is "invalid_quantity" or "no_open_cash")
            {
                form.Error = ex.Code == "no_open_cash" ? "Abra o seu caixa para devolver em dinheiro." : "Quantidade maior que a disponível para devolução.";
                return false;
            }
            await LoadAsync();
            return true;
        });
    }
    public override bool HasPeriod => true;
    public ObservableCollection<SaleRow> Sales { get; } = [];
    public ObservableCollection<DetailLine> Detail { get; } = [];
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public string DetailTitle { get => _detailTitle; set => Set(ref _detailTitle, value); }
    public string DetailInfo { get => _detailInfo; set => Set(ref _detailInfo, value); }
    public bool HasSelection => Selected is not null;
    public bool IsEmpty => Sales.Count == 0 && !Loading;
    public RelayCommand CancelSaleCommand { get; }
    public SaleRow? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { Raise(nameof(HasSelection)); if (value is not null) _ = LoadDetailAsync(value); } }
    }

    protected override async Task LoadCoreAsync()
    {
        var (from, to) = Range();
        var list = await S.Api.GetAsync($"/api/v1/sales?from={Q(from)}&to={Q(to)}");
        Sales.Clear();
        long total = 0; var count = 0;
        foreach (var s in list.EnumerateArray())
        {
            Sales.Add(SaleRow.From(s));
            if (s.GetProperty("status").GetString() != "Cancelled") { total += s.GetProperty("totalCents").GetInt64(); count++; }
        }
        Summary = $"{count} venda(s) concluída(s) • {AppServices.Money(total)}" + (Sales.Count > count ? $" • {Sales.Count - count} cancelada(s)" : "");
        Selected = Sales.FirstOrDefault();
        Raise(nameof(IsEmpty));
    }

    private async Task LoadDetailAsync(SaleRow row)
    {
        try
        {
            var s = await S.Api.GetAsync($"/api/v1/sales/{row.Id}");
            ApplyDetail(row, s);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException) { DetailInfo = "Não foi possível carregar os itens."; }
    }

    public void ApplyDetail(SaleRow row, JsonElement s)
    {
        Detail.Clear();
        _detailSale = s;
        foreach (var i in s.GetProperty("items").EnumerateArray())
            Detail.Add(new DetailLine(i.GetProperty("productName").GetString()!, i.GetProperty("quantity").GetDecimal().ToString("0.###", AppServices.PtBr),
                AppServices.Money(i.GetProperty("unitPriceCents").GetInt64()), AppServices.Money(i.GetProperty("totalCents").GetInt64()),
                i.TryGetProperty("id", out var iid) ? iid.GetGuid() : Guid.Empty, i.GetProperty("quantity").GetDecimal(),
                i.GetProperty("unitPriceCents").GetInt64(), i.GetProperty("totalCents").GetInt64()));
        DetailTitle = $"Venda de {row.When} • {row.Total}";
        var disc = s.GetProperty("discountCents").GetInt64();
        var change = s.GetProperty("changeCents").GetInt64();
        DetailInfo = $"Operador: {row.Operator} • Pagamento: {row.Payment}" + (disc > 0 ? $" • Desconto: {AppServices.Money(disc)}" : "")
            + (change > 0 ? $" • Troco: {AppServices.Money(change)}" : "") + (row.Offline ? " • registrada offline" : "")
            + (row.Cancelled ? $" • CANCELADA: {s.GetProperty("cancelReason").GetString()}" : "")
            + (s.TryGetProperty("returnedCents", out var ret) && ret.GetInt64() > 0 ? $" • DEVOLVIDO: {AppServices.Money(ret.GetInt64())}" : "");
    }

    private void AskCancel()
    {
        var row = Selected!;
        Shell.ShowPrompt(new PromptViewModel
        {
            Title = "Cancelar venda", Message = $"Venda de {row.When} no valor de {row.Total}. O estoque volta e o dinheiro sai do caixa. A venda continua no histórico como cancelada.",
            TextLabel = "Motivo do cancelamento", ConfirmText = "Cancelar venda",
        }, async p =>
        {
            if (p.Text.Trim().Length < 5) { p.Error = "Informe o motivo (mínimo 5 caracteres)."; return false; }
            try
            {
                await S.Api.PostAsync($"/api/v1/sales/{row.Id}/cancel", new { reason = p.Text.Trim() });
                await LoadAsync();
                return true;
            }
            catch (ApiException ex)
            {
                p.Error = ex.Code switch
                {
                    "no_open_cash" => "Abra um caixa para devolver o valor em dinheiro.",
                    "fiscal_cancel_required" => "Documento fiscal autorizado: cancele pelo módulo fiscal.",
                    _ => "Não foi possível cancelar: " + ex.Code,
                };
            }
            catch (HttpRequestException) { p.Error = "Cancelar exige conexão com o servidor."; }
            return false;
        });
    }
}

public sealed record ProductProfitRow(string Name, string Quantity, string Revenue, string Cost, string Profit, string Margin, double MarginWidth, bool Negative);

public sealed class ProfitViewModel(ShellViewModel shell) : OwnerPage(shell, "Lucratividade", "Lucro bruto e margem por produto, com o custo registrado em cada venda")
{
    private string _note = "", _discountNote = "";
    public override bool HasPeriod => true;
    public ObservableCollection<Kpi> Kpis { get; } = [];
    public ObservableCollection<ProductProfitRow> Products { get; } = [];
    public ObservableCollection<Bar> Bars { get; } = [];
    public string Note { get => _note; set => Set(ref _note, value); }
    public string DiscountNote { get => _discountNote; set => Set(ref _discountNote, value); }
    public bool HasBars => Bars.Count > 1;
    public RelayCommand ExportCommand => new(_ => { var (f, t) = Range(); return ExportCsvAsync($"/api/v1/reports/export/profitability.csv?from={Q(f)}&to={Q(t)}", "lucratividade"); });

    protected override async Task LoadCoreAsync()
    {
        var (from, to) = Range();
        Apply(await S.Api.GetAsync($"/api/v1/reports/profitability?from={Q(from)}&to={Q(to)}"));
    }

    public void Apply(JsonElement r)
    {
        var t = r.GetProperty("totals");
        Kpis.Clear();
        Kpis.Add(new Kpi("Faturamento", AppServices.Money(t.GetProperty("revenueCents").GetInt64()), $"{t.GetProperty("salesCount").GetInt32()} venda(s)", "\uE7BF", "accent"));
        Kpis.Add(new Kpi("Custo dos produtos", AppServices.Money(t.GetProperty("costCents").GetInt64()), "custo histórico", "\uE7B8", "neutral"));
        Kpis.Add(new Kpi("Lucro bruto", AppServices.Money(t.GetProperty("grossProfitCents").GetInt64()), "estimado (sem despesas)", "\uE8C7", "primary"));
        Kpis.Add(new Kpi("Margem bruta", t.GetProperty("marginPercent").GetDecimal().ToString("0.0", AppServices.PtBr) + "%", "sobre o faturamento", "\uE9D2", "primary"));
        if (t.TryGetProperty("netResultCents", out var net) && net.ValueKind == JsonValueKind.Number)
            Kpis.Add(new Kpi("Resultado l\u00EDquido", AppServices.Money(net.GetInt64()), $"ap\u00F3s despesas de {AppServices.Money(t.GetProperty("expensesCents").GetInt64())}", Icons.Money, net.GetInt64() >= 0 ? "primary" : "warn"));
        if (t.TryGetProperty("returnsCents", out var rets) && rets.GetInt64() > 0)
            Kpis[0] = Kpis[0] with { Sub = $"{t.GetProperty("salesCount").GetInt32()} venda(s) \u2022 devolu\u00E7\u00F5es {AppServices.Money(rets.GetInt64())}" };
        var disc = t.GetProperty("saleLevelDiscountCents").GetInt64();
        DiscountNote = disc > 0 ? $"Descontos no total das vendas (não atribuídos a produtos): {AppServices.Money(disc)}" : "";
        Products.Clear();
        foreach (var p in r.GetProperty("products").EnumerateArray())
        {
            var m = p.GetProperty("marginPercent").GetDecimal();
            Products.Add(new ProductProfitRow(p.GetProperty("name").GetString()!, p.GetProperty("quantity").GetDecimal().ToString("0.###", AppServices.PtBr),
                AppServices.Money(p.GetProperty("revenueCents").GetInt64()), AppServices.Money(p.GetProperty("costCents").GetInt64()),
                AppServices.Money(p.GetProperty("grossProfitCents").GetInt64()), m.ToString("0.0", AppServices.PtBr) + "%",
                Math.Clamp((double)m, 0, 100) * 1.2, m < 0));
        }
        Bars.Clear();
        foreach (var b in BuildBars(r.GetProperty("daily").EnumerateArray().Select(x => (DateOnly.Parse(x.GetProperty("date").GetString()!).ToString("dd/MM"),
                     x.GetProperty("revenueCents").GetInt64(), x.GetProperty("grossProfitCents").GetInt64()))))
            Bars.Add(b);
        Raise(nameof(HasBars));
        Note = r.GetProperty("note").GetString() ?? "";
    }
}

/// <summary>Linha do estoque. No modo inventário, o operador digita a quantidade contada.</summary>
public sealed class StockRow(Guid productId, string name, string sku, string? barcode, decimal quantity, decimal min, long priceCents, long costCents, bool hasImage) : ObservableObject
{
    private string _counted = "";
    private System.Windows.Media.ImageSource? _thumb;
    public Guid ProductId { get; } = productId;
    public string Name { get; } = name;
    public string Sku { get; } = sku;
    public string? Barcode { get; } = barcode;
    public decimal QuantityValue { get; } = quantity;
    public decimal MinValue { get; } = min;
    public long PriceCents { get; } = priceCents;
    public long CostCents { get; } = costCents;
    public decimal? MarkupPercent { get; init; }
    public bool OpenPrice { get; init; }
    public bool HasImage { get; } = hasImage;
    // Dados preservados na edição (unidade e fiscais).
    public string Unit { get; init; } = "UN";
    public string? Ncm { get; init; }
    public string? Cest { get; init; }
    public string? Cfop { get; init; }
    public int FiscalOrigin { get; init; }
    public string? IcmsCst { get; init; }
    public string? IbsCbsCst { get; init; }
    public string? IbsCbsClass { get; init; }
    public string Quantity => QuantityValue.ToString("0.###", AppServices.PtBr);
    public string Min => MinValue.ToString("0.###", AppServices.PtBr);
    public string Price => AppServices.Money(PriceCents);
    public bool Low => QuantityValue <= MinValue;
    public System.Windows.Media.ImageSource? Thumb { get => _thumb; set { Set(ref _thumb, value); Raise(nameof(HasThumb)); } }
    public bool HasThumb => Thumb is not null;
    public string Counted { get => _counted; set { if (Set(ref _counted, value)) { Raise(nameof(Difference)); Raise(nameof(DiffNegative)); Raise(nameof(DiffPositive)); } } }
    public decimal? CountedValue => decimal.TryParse(Counted, System.Globalization.NumberStyles.Number, AppServices.PtBr, out var v) && v >= 0 ? v : null;
    public string Difference => CountedValue is { } c ? (c - QuantityValue).ToString("+0.###;-0.###;0", AppServices.PtBr) : "";
    public bool DiffNegative => CountedValue is { } c && c < QuantityValue;
    public bool DiffPositive => CountedValue is { } c && c > QuantityValue;
}

public sealed class StockViewModel : OwnerPage
{
    private readonly List<StockRow> _all = [];
    private string _filter = "", _summary = "";
    private bool _onlyLow, _inventoryMode;
    public StockViewModel(ShellViewModel shell) : base(shell, "Estoque", "Saldo por produto, entradas, cadastro com foto e inventário")
    {
        NewProductCommand = RelayCommand.Sync(_ => ProductForm(null), _ => CanWrite);
        EditProductCommand = RelayCommand.Sync(p => ProductForm(p as StockRow), _ => CanWrite);
        EntryCommand = RelayCommand.Sync(p => { if (p is StockRow r) EntryForm(r); }, _ => S.Session.Can("stock.entry"));
        StartInventoryCommand = RelayCommand.Sync(_ => { foreach (var r in _all) r.Counted = ""; InventoryMode = true; }, _ => S.Session.Can("stock.adjust"));
        CancelInventoryCommand = RelayCommand.Sync(_ => InventoryMode = false);
        SaveInventoryCommand = RelayCommand.Sync(_ => SaveInventory());
        ExportCommand = new RelayCommand(_ => ExportCsvAsync($"/api/v1/reports/export/stock.csv?storeId={S.Settings.StoreId}", "estoque"));
    }
    public ObservableCollection<StockRow> Items { get; } = [];

    // ---------------- Importações, validades e transferências ----------------
    private static readonly string[] ImportOptions = ["NF-e de compra (arquivo XML do fornecedor)", "Planilha de produtos (CSV do Excel)"];
    public RelayCommand ImportCommand => RelayCommand.Sync(_ =>
    {
        var f = new FormViewModel { Title = "Importar", Message = "Escolha o tipo de arquivo.", ConfirmText = "Escolher arquivo" };
        f.Fields.Add(new FormField("kind", "Tipo", ImportOptions[0], options: ImportOptions));
        Shell.ShowForm(f, form =>
        {
            var xml = form["kind"] == ImportOptions[0];
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = xml ? "NF-e (*.xml)|*.xml" : "Planilha CSV (*.csv)|*.csv" };
            if (dlg.ShowDialog() != true) return Task.FromResult(false);
            if (new System.IO.FileInfo(dlg.FileName).Length > 3_000_000) { form.Error = "Arquivo grande demais."; return Task.FromResult(false); }
            var text = System.IO.File.ReadAllText(dlg.FileName);
            Shell.Form = null;
            if (xml) _ = PreviewNfeAsync(text); else _ = ImportCsvAsync(text);
            return Task.FromResult(false);
        });
    }, _ => S.Session.Can("stock.entry"));

    private async Task PreviewNfeAsync(string xml)
    {
        try
        {
            var pv = await S.Api.PostAsync("/api/v1/purchases/nfe/preview", new { xml });
            var items = pv.GetProperty("items").EnumerateArray().ToList();
            var matched = items.Count(i => i.GetProperty("matchedProductId").ValueKind != JsonValueKind.Null);
            if (pv.GetProperty("alreadyImported").GetBoolean()) { Error = "Esta NF-e já foi importada."; return; }
            Shell.ShowPrompt(new PromptViewModel
            {
                Title = $"NF-e {pv.GetProperty("number").GetString()} — {pv.GetProperty("supplierName").GetString()}",
                Message = $"{items.Count} item(ns), total {AppServices.Money(pv.GetProperty("totalCents").GetInt64())}. {matched} já existem (pelo código de barras) e terão custo e estoque atualizados; "
                          + $"{items.Count - matched} serão cadastrados com o preço = custo + margem. Lote e validade da nota também são registrados.",
                ValueLabel = "Margem para os produtos novos (%)", ConfirmText = "Importar e dar entrada no estoque",
            }, async p =>
            {
                var markup = decimal.TryParse(p.Value.Replace("%", "").Trim(), System.Globalization.NumberStyles.Number, AppServices.PtBr, out var m) ? m : 50m;
                try
                {
                    var r = await S.Api.PostAsync("/api/v1/purchases/nfe/import", new { xml, storeId = S.Settings.StoreId, markupPercent = markup, createMissing = true });
                    await LoadAsync();
                    Summary = $"NF-e importada: {r.GetProperty("created").GetInt32()} produto(s) novo(s), {r.GetProperty("updated").GetInt32()} atualizado(s).";
                    return true;
                }
                catch (ApiException ex) { p.Error = ex.Code == "already_imported" ? "Esta NF-e já foi importada." : "Falha: " + ex.Code; }
                catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
                return false;
            });
        }
        catch (ApiException ex) { Error = ex.Code == "invalid_xml" ? "O arquivo não é um XML de NF-e válido." : "Falha: " + ex.Code; }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
    }

    private async Task ImportCsvAsync(string csv)
    {
        try
        {
            var r = await S.Api.PostAsync("/api/v1/products/import-csv", new { csv, storeId = S.Settings.StoreId });
            await LoadAsync();
            var errors = r.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
            Summary = $"Planilha: {r.GetProperty("created").GetInt32()} novo(s), {r.GetProperty("updated").GetInt32()} atualizado(s)" + (errors.Count > 0 ? $", {errors.Count} linha(s) com erro." : ".");
            if (errors.Count > 0) Error = string.Join(" • ", errors.Take(5)) + (errors.Count > 5 ? " ..." : "");
        }
        catch (ApiException ex) { Error = ex.Code == "invalid_csv" ? "Planilha inválida: precisa das colunas sku; nome; preco (separadas por ponto e vírgula)." : "Falha: " + ex.Code; }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
    }

    public RelayCommand ExpiringCommand => new(async _ =>
    {
        try
        {
            var list = (await S.Api.GetAsync("/api/v1/stock/expiring?days=30")).EnumerateArray().ToList();
            var text = list.Count == 0 ? "Nenhum lote vence nos próximos 30 dias."
                : string.Join("\n", list.Take(15).Select(b => $"{(b.GetProperty("expired").GetBoolean() ? "VENCIDO " : "")}{DateOnly.Parse(b.GetProperty("expiresOn").GetString()!):dd/MM/yyyy} • {b.GetProperty("name").GetString()} • lote {b.GetProperty("batchCode").GetString()} • {b.GetProperty("quantityRemaining").GetDecimal():0.###} un"))
                  + (list.Count > 15 ? $"\n... e mais {list.Count - 15}" : "");
            Shell.ShowPrompt(new PromptViewModel { Title = "Validades (próximos 30 dias)", Message = text, ConfirmText = "Fechar" }, _ => Task.FromResult(true));
        }
        catch (ApiException ex) { Error = "Falha: " + ex.Code; }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
    });

    public RelayCommand TransferCommand => new(async _ =>
    {
        var stores = (await S.Api.GetAsync("/api/v1/stores")).EnumerateArray().Where(s => s.GetProperty("id").GetGuid() != S.Settings.StoreId)
            .Select(s => (Id: s.GetProperty("id").GetGuid(), Name: s.GetProperty("name").GetString()!)).ToList();
        if (stores.Count == 0) { Error = "Sua empresa tem só uma loja: não há para onde transferir."; return; }
        var f = new FormViewModel { Title = "Transferir para outra loja", Message = "Sai desta loja e entra na outra na mesma hora. Itens: SKU*quantidade separados por ponto e vírgula.", ConfirmText = "Transferir" };
        f.Fields.Add(new FormField("to", "Loja de destino", stores[0].Name, options: stores.Select(s => s.Name).ToList()));
        f.Fields.Add(new FormField("items", "Itens", "", Icons.Box, "SKU*quantidade; SKU*quantidade"));
        f.Fields.Add(new FormField("notes", "Observação", "", Icons.Edit, "opcional"));
        Shell.ShowForm(f, async form =>
        {
            var items = new List<object>();
            foreach (var part in form["items"].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var bits = part.Split('*', StringSplitOptions.TrimEntries);
                var hit = S.Db.SearchProducts(bits[0], 5).FirstOrDefault(h => h.Sku == bits[0] || h.Barcode == bits[0]);
                if (hit.Id == Guid.Empty || bits.Length < 2 || !TryQty(bits[1], out var q) || q <= 0) { form.Error = $"Item inválido: {part}"; return false; }
                items.Add(new { productId = hit.Id, quantity = q });
            }
            if (items.Count == 0) { form.Error = "Informe os itens."; return false; }
            try
            {
                var r = await S.Api.PostAsync("/api/v1/stock/transfers", new { fromStoreId = S.Settings.StoreId, toStoreId = stores.First(s => s.Name == form["to"]).Id, items, notes = form["notes"] });
                await LoadAsync();
                Summary = $"Transferência nº {r.GetProperty("number").GetInt32()} registrada.";
                return true;
            }
            catch (ApiException ex) { form.Error = "Falha: " + ex.Code; }
            catch (HttpRequestException) { form.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }, _ => S.Session.Can("stock.adjust"));

    private static readonly string[] MoreOptions = ["Gerar grade (tamanho/cor)", "Montar kit (combo)", "Códigos de barras extras"];
    public RelayCommand MoreCommand => RelayCommand.Sync(p =>
    {
        if (p is not StockRow row) return;
        var f = new FormViewModel { Title = row.Name, Message = "O que você quer fazer com este produto?", ConfirmText = "Continuar" };
        f.Fields.Add(new FormField("what", "Ação", MoreOptions[0], options: MoreOptions));
        Shell.ShowForm(f, form =>
        {
            var what = Array.IndexOf(MoreOptions, form["what"]);
            Shell.Form = null;
            if (what == 0) GradeForm(row); else if (what == 1) KitForm(row); else CodesForm(row);
            return Task.FromResult(false);
        });
    }, _ => CanWrite);

    private void GradeForm(StockRow row)
    {
        var f = new FormViewModel { Title = "Grade de " + row.Name, Message = "Cria uma variação para cada combinação, com estoque e código próprios. Ex.: Tamanho = P, M, G e Cor = Azul, Preto → 6 variações.", ConfirmText = "Gerar variações" };
        f.Fields.Add(new FormField("a1", "Atributo 1", "Tamanho", Icons.Edit));
        f.Fields.Add(new FormField("v1", "Valores do atributo 1 (separados por vírgula)", "", Icons.Edit, "P, M, G, GG"));
        f.Fields.Add(new FormField("a2", "Atributo 2 (opcional)", "Cor", Icons.Edit));
        f.Fields.Add(new FormField("v2", "Valores do atributo 2", "", Icons.Edit, "Azul, Preto"));
        f.Fields.Add(new FormField("price", "Preço das variações (vazio = o mesmo do produto)", "", Icons.Money));
        Shell.ShowForm(f, async form =>
        {
            static List<string> Vals(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var attrs = new Dictionary<string, List<string>>();
            if (form["a1"].Length > 0 && Vals(form["v1"]).Count > 0) attrs[form["a1"]] = Vals(form["v1"]);
            if (form["a2"].Length > 0 && Vals(form["v2"]).Count > 0) attrs[form["a2"]] = Vals(form["v2"]);
            if (attrs.Count == 0) { form.Error = "Informe pelo menos um atributo com valores."; return false; }
            long? price = null;
            if (form["price"].Length > 0) { if (!PromptViewModel.TryMoney(form["price"], out var pc)) { form.Error = "Preço inválido."; return false; } price = pc; }
            try
            {
                var r = await S.Api.PostAsync($"/api/v1/products/{row.ProductId}/variants", new { attributes = attrs, priceCents = price });
                await LoadAsync();
                return true;
            }
            catch (ApiException ex) { form.Error = ex.Code == "sku_taken" ? "Já existe produto com um dos códigos gerados." : "Falha: " + ex.Code; }
            catch (HttpRequestException) { form.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }

    private void KitForm(StockRow row)
    {
        var f = new FormViewModel { Title = "Kit: " + row.Name, Message = "Vender o kit baixa o estoque dos componentes. Informe SKU*quantidade separados por ponto e vírgula. Ex.: SHA01*1; CON01*2", ConfirmText = "Salvar kit" };
        f.Fields.Add(new FormField("items", "Componentes", "", Icons.Box, "SKU*quantidade; SKU*quantidade"));
        Shell.ShowForm(f, async form =>
        {
            var list = new List<object>();
            foreach (var part in form["items"].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var bits = part.Split('*', StringSplitOptions.TrimEntries);
                var qty = bits.Length > 1 && decimal.TryParse(bits[1], System.Globalization.NumberStyles.Number, AppServices.PtBr, out var q) ? q : 1m;
                var hit = S.Db.SearchProducts(bits[0], 5).FirstOrDefault(h => h.Sku == bits[0] || h.Barcode == bits[0]);
                if (hit.Id == Guid.Empty) { form.Error = $"Componente \"{bits[0]}\" não encontrado."; return false; }
                list.Add(new { productId = hit.Id, quantity = qty });
            }
            try
            {
                await S.Api.PutAsync($"/api/v1/products/{row.ProductId}/components", list);
                await LoadAsync();
                return true;
            }
            catch (ApiException ex) { form.Error = ex.Code == "invalid_kit" ? "Kit inválido: componentes precisam ser produtos simples e diferentes do kit." : "Falha: " + ex.Code; }
            catch (HttpRequestException) { form.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }

    private void CodesForm(StockRow row) => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Código de barras extra", Message = $"Adicione outro código para {row.Name} (ex.: código da caixa ou do fornecedor). Para remover, comece com - (ex.: -7891234567890).",
        TextLabel = "Código", ConfirmText = "Salvar",
    }, async p =>
    {
        var code = p.Text.Trim();
        try
        {
            if (code.StartsWith('-')) await S.Api.PostAsync($"/api/v1/products/{row.ProductId}/barcodes/remove", new { code = code[1..] });
            else await S.Api.PostAsync($"/api/v1/products/{row.ProductId}/barcodes", new { code });
            return true;
        }
        catch (ApiException ex) { p.Error = ex.Code == "barcode_taken" ? "Este código já pertence a outro produto." : ex.Code == "invalid_barcode" ? "Código inválido." : "Falha: " + ex.Code; }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        return false;
    });

    /// <summary>Etiquetas de gôndola dos produtos listados (filtro atual) na impressora de etiquetas (ZPL).</summary>
    public RelayCommand LabelsCommand => RelayCommand.Sync(_ => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Imprimir etiquetas", Message = $"{Items.Count} produto(s) da lista atual (use a busca para filtrar). Impressora: {S.Settings.Devices.LabelPrinterName ?? "não configurada — veja Dispositivos"}.",
        ValueLabel = "Cópias de cada etiqueta", ConfirmText = "Imprimir",
    }, p =>
    {
        if (string.IsNullOrWhiteSpace(S.Settings.Devices.LabelPrinterName)) { p.Error = "Configure a impressora de etiquetas em Dispositivos."; return Task.FromResult(false); }
        var copies = int.TryParse(p.Value.Trim(), out var c) && c is > 0 and <= 50 ? c : 1;
        try
        {
            var zpl = Comercial.Desktop.Core.Hardware.LabelZpl.Build(Items.Take(500).Select(r => new Comercial.Desktop.Core.Hardware.LabelItem(r.Name, r.PriceCents, r.Barcode, r.Unit)), copies: copies);
            RawPrinter.Send(S.Settings.Devices.LabelPrinterName!, System.Text.Encoding.UTF8.GetBytes(zpl), "Etiquetas");
            return Task.FromResult(true);
        }
        catch (InvalidOperationException ex) { p.Error = ex.Message; return Task.FromResult(false); }
    }), _ => Items.Count > 0);
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) Apply(); } }
    public bool OnlyLow { get => _onlyLow; set { if (Set(ref _onlyLow, value)) Apply(); } }
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public bool InventoryMode { get => _inventoryMode; set { if (Set(ref _inventoryMode, value)) Raise(nameof(NormalMode)); } }
    public bool NormalMode => !InventoryMode;
    private bool CanWrite => S.Session.Can("products.write");
    public RelayCommand NewProductCommand { get; }
    public RelayCommand EditProductCommand { get; }
    public RelayCommand EntryCommand { get; }
    public RelayCommand StartInventoryCommand { get; }
    public RelayCommand CancelInventoryCommand { get; }
    public RelayCommand SaveInventoryCommand { get; }
    public RelayCommand ExportCommand { get; }

    protected override async Task LoadCoreAsync()
    {
        var rows = await S.Api.GetAsync($"/api/v1/stock/balances?storeId={S.Settings.StoreId}");
        SetRows(rows.EnumerateArray().Select(r => new StockRow(r.GetProperty("productId").GetGuid(), r.GetProperty("name").GetString()!, r.GetProperty("sku").GetString()!,
            r.TryGetProperty("barcode", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
            r.GetProperty("quantity").GetDecimal(), r.GetProperty("minStock").GetDecimal(), r.GetProperty("priceCents").GetInt64(),
            r.GetProperty("costCents").GetInt64(), r.GetProperty("hasImage").GetBoolean())
        {
            Unit = Str(r, "unit") ?? "UN", Ncm = Str(r, "ncm"), Cest = Str(r, "cest"), Cfop = Str(r, "cfop"),
            MarkupPercent = r.TryGetProperty("markupPercent", out var mk) && mk.ValueKind == JsonValueKind.Number ? mk.GetDecimal() : null,
            OpenPrice = r.TryGetProperty("openPrice", out var opn) && opn.ValueKind == JsonValueKind.True,
            FiscalOrigin = r.TryGetProperty("fiscalOrigin", out var fo) && fo.ValueKind == JsonValueKind.Number ? fo.GetInt32() : 0,
            IcmsCst = Str(r, "icmsCst"), IbsCbsCst = Str(r, "ibsCbsCst"), IbsCbsClass = Str(r, "ibsCbsClass"),
        }));
        foreach (var r in _all.Where(r => r.HasImage)) _ = LoadThumbAsync(r);
    }

    private async Task LoadThumbAsync(StockRow r)
    {
        try { if (await S.Api.GetBytesAsync($"/api/v1/products/{r.ProductId}/image") is { } bytes) r.Thumb = FormViewModel.Load(bytes); }
        catch (Exception ex) when (ex is ApiException or HttpRequestException) { /* miniatura é opcional */ }
    }

    public void SetRows(IEnumerable<StockRow> rows)
    {
        _all.Clear();
        _all.AddRange(rows.OrderByDescending(r => r.Low).ThenBy(r => r.Name));
        Summary = $"{_all.Count} produto(s) • {_all.Count(r => r.Low)} com estoque baixo";
        Apply();
    }

    private void Apply()
    {
        Items.Clear();
        foreach (var r in _all.Where(r => (!OnlyLow || r.Low) && (Filter.Length == 0 || r.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                     || r.Sku.Contains(Filter, StringComparison.OrdinalIgnoreCase) || (r.Barcode?.Contains(Filter) ?? false))))
            Items.Add(r);
    }

    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static readonly string[] Units = ["UN", "KG", "G", "L", "ML", "M", "CX", "PC"];

    private static bool TryQty(string s, out decimal v) => decimal.TryParse(s, System.Globalization.NumberStyles.Number, AppServices.PtBr, out v) && v >= 0;

    /// <summary>Cadastro (row = null) ou edição de produto, com foto opcional.</summary>
    /// <summary>% digitada ("100", "35,5" ou "35.5%").</summary>
    private static bool TryPercent(string s, out decimal v)
    {
        v = 0;
        var t = s.Replace("%", "").Trim().Replace('.', ',');
        return t.Length > 0 && decimal.TryParse(t, System.Globalization.NumberStyles.Number, AppServices.PtBr, out v) && v >= 0 && v <= Comercial.Shared.Pricing.MaxMarkupPercent;
    }

    private void ProductForm(StockRow? row)
    {
        var f = new FormViewModel
        {
            Title = row is null ? "Novo produto" : "Editar produto",
            Message = row is null ? "Cadastre o produto e, se quiser, a quantidade que já está na loja." : "Alterações de preço ficam registradas na auditoria.",
            ConfirmText = row is null ? "Cadastrar produto" : "Salvar alterações", AllowImage = true,
        };
        f.Fields.Add(new FormField("name", "Nome do produto", row?.Name ?? "", Icons.Box));
        f.Fields.Add(new FormField("sku", "Código interno (SKU)", row?.Sku ?? "", Icons.Edit));
        f.Fields.Add(new FormField("barcode", "Código de barras", row?.Barcode ?? "", Icons.Chart, "opcional"));
        // Quanto o dono pagou + % de lucro = preço de venda calculado (ex.: R$ 10,00 + 100% = R$ 20,00).
        var costField = new FormField("cost", "Quanto você pagou pelo produto (custo R$)", row is null ? "" : (row.CostCents / 100m).ToString("0.00", AppServices.PtBr), Icons.Money, "ex.: 10,00");
        var markupField = new FormField("markup", "% de lucro em cima do custo", row?.MarkupPercent is { } mk ? mk.ToString("0.##", AppServices.PtBr) : "", Icons.Chart, "ex.: 100 (deixe vazio para digitar o preço)");
        var priceField = new FormField("price", "Preço de venda (R$)", row is null ? "" : (row.PriceCents / 100m).ToString("0.00", AppServices.PtBr), Icons.Money);
        f.Fields.Add(costField);
        f.Fields.Add(markupField);
        f.Fields.Add(priceField);
        void Recalc()
        {
            var hasCost = PromptViewModel.TryMoney(costField.Value, out var c);
            if (TryPercent(markupField.Value, out var m))
            {
                priceField.IsReadOnly = true;
                if (!hasCost) { priceField.Note = "Custo inválido."; return; }
                var p = Comercial.Shared.Pricing.PriceFromMarkup(c, m);
                priceField.Value = (p / 100m).ToString("0.00", AppServices.PtBr);
                priceField.Note = $"Calculado automaticamente • lucro de {AppServices.Money(p - c)} por unidade";
            }
            else
            {
                priceField.IsReadOnly = false;
                priceField.Note = markupField.Value.Trim().Length > 0 ? "% inválida: use um número, ex.: 100"
                    : hasCost && PromptViewModel.TryMoney(priceField.Value, out var pv) && Comercial.Shared.Pricing.MarkupFromPrice(c, pv) is { } imp
                        ? $"Equivale a {imp.ToString("0.##", AppServices.PtBr)}% de lucro ({AppServices.Money(pv - c)} por unidade)" : "";
            }
        }
        costField.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FormField.Value)) Recalc(); };
        markupField.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FormField.Value)) Recalc(); };
        priceField.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FormField.Value) && !priceField.IsReadOnly) Recalc(); };
        Recalc();
        f.Fields.Add(new FormField("open", "Preço livre (valor digitado no caixa: código=preço)", row?.OpenPrice == true ? "Sim" : "Não", options: ["Não", "Sim"])
            { Note = "Use para itens que não são cadastrados um a um, ex.: código 100 \"Diversos\" e no caixa 100=9,90." });
        f.Fields.Add(new FormField("min", "Estoque mínimo", row?.Min ?? "5", Icons.Box));
        f.Fields.Add(new FormField("unit", "Unidade", row?.Unit ?? "UN", options: Units));
        f.Fields.Add(new FormField("ncm", "NCM (8 dígitos — obrigatório para NFC-e)", row?.Ncm ?? "", Icons.Edit, "ex.: 61091000"));
        f.Fields.Add(new FormField("cest", "CEST (se houver substituição tributária)", row?.Cest ?? "", Icons.Edit, "opcional"));
        if (row is null) f.Fields.Add(new FormField("qty", "Quantidade inicial na loja", "", Icons.Add, "0"));
        if (row?.Thumb is System.Windows.Media.Imaging.BitmapImage thumb) f.Preview = thumb;

        Shell.ShowForm(f, async form =>
        {
            if (form["name"].Length == 0 || form["sku"].Length == 0) { form.Error = "Informe o nome e o código interno."; return false; }
            if (!PromptViewModel.TryMoney(form["price"], out var price) || !PromptViewModel.TryMoney(form["cost"], out var cost)) { form.Error = "Preço ou custo inválido."; return false; }
            decimal? markup = null;
            if (form["markup"].Length > 0) { if (!TryPercent(form["markup"], out var mv)) { form.Error = "% de lucro inválida (ex.: 100)."; return false; } markup = mv; }
            if (!TryQty(form["min"].Length == 0 ? "0" : form["min"], out var min)) { form.Error = "Estoque mínimo inválido."; return false; }
            decimal qty = 0;
            if (row is null && form["qty"].Length > 0 && !TryQty(form["qty"], out qty)) { form.Error = "Quantidade inicial inválida."; return false; }
            var body = new
            {
                sku = form["sku"], barcode = form["barcode"].Length == 0 ? null : form["barcode"], name = form["name"], unit = form["unit"], priceCents = price, costCents = cost, minStock = min,
                markupPercent = markup, openPrice = form["open"] == "Sim",
                ncm = form["ncm"].Length == 0 ? null : form["ncm"], cest = form["cest"].Length == 0 ? null : form["cest"],
                cfop = row?.Cfop, fiscalOrigin = row?.FiscalOrigin ?? 0, icmsCst = row?.IcmsCst, ibsCbsCst = row?.IbsCbsCst, ibsCbsClass = row?.IbsCbsClass,
            };
            Guid id;
            try
            {
                if (row is null) id = (await S.Api.PostAsync("/api/v1/products", body)).GetProperty("id").GetGuid();
                else { await S.Api.PutAsync($"/api/v1/products/{row.ProductId}", body); id = row.ProductId; }
            }
            catch (ApiException ex) when (ex.Code is "invalid_fiscal" or "sku_taken" or "invalid_unit")
            {
                form.Error = ex.Code switch { "sku_taken" => "Já existe produto com este código interno.", "invalid_fiscal" => "NCM deve ter 8 dígitos e CEST 7 dígitos.", _ => "Unidade inválida." };
                return false;
            }
            if (qty > 0)
                await S.Api.PostAsync("/api/v1/stock/entries", new { storeId = S.Settings.StoreId, productId = id, quantity = qty, unitCostCents = cost, reason = "estoque inicial" });
            if (form.ImagePath is { } path)
                await S.Api.UploadAsync($"/api/v1/products/{id}/image", await System.IO.File.ReadAllBytesAsync(path), System.IO.Path.GetFileName(path), FormViewModel.ContentTypeOf(path));
            await LoadAsync();
            return true;
        });
    }

    private void EntryForm(StockRow row)
    {
        var f = new FormViewModel { Title = "Entrada de mercadoria", Message = $"{row.Name} — em estoque: {row.Quantity}", ConfirmText = "Registrar entrada" };
        f.Fields.Add(new FormField("qty", "Quantidade recebida", "", Icons.Add));
        f.Fields.Add(new FormField("cost", "Custo unitário (R$)", (row.CostCents / 100m).ToString("0.00", AppServices.PtBr), Icons.Money));
        f.Fields.Add(new FormField("reason", "Nota fiscal / fornecedor", "", Icons.Edit, "opcional"));
        f.Fields.Add(new FormField("batch", "Lote", "", Icons.Edit, "opcional"));
        f.Fields.Add(new FormField("expires", "Validade (dd/mm/aaaa)", "", Icons.Edit, "opcional — gera aviso antes de vencer"));
        Shell.ShowForm(f, async form =>
        {
            if (!TryQty(form["qty"], out var qty) || qty <= 0) { form.Error = "Informe a quantidade recebida."; return false; }
            if (!PromptViewModel.TryMoney(form["cost"], out var cost)) { form.Error = "Custo inválido."; return false; }
            DateOnly? expires = null;
            if (form["expires"].Length > 0)
            {
                if (!DateOnly.TryParseExact(form["expires"], "dd/MM/yyyy", AppServices.PtBr, System.Globalization.DateTimeStyles.None, out var ex)) { form.Error = "Validade no formato dd/mm/aaaa."; return false; }
                expires = ex;
            }
            await S.Api.PostAsync("/api/v1/stock/entries", new
            {
                storeId = S.Settings.StoreId, productId = row.ProductId, quantity = qty, unitCostCents = cost, reason = form["reason"].Length == 0 ? "entrada de mercadoria" : form["reason"],
                batchCode = form["batch"].Length == 0 ? null : form["batch"], expiresOn = expires?.ToString("yyyy-MM-dd"),
            });
            await LoadAsync();
            return true;
        });
    }

    private void SaveInventory()
    {
        var counted = _all.Where(r => r.Counted.Trim().Length > 0).ToList();
        if (counted.Count == 0) { Error = "Digite a quantidade contada de pelo menos um produto."; return; }
        if (counted.Any(r => r.CountedValue is null)) { Error = "Há quantidades contadas inválidas."; return; }
        var changes = counted.Count(r => r.CountedValue != r.QuantityValue);
        Shell.ShowPrompt(new PromptViewModel
        {
            Title = "Concluir inventário",
            Message = $"{counted.Count} produto(s) contado(s), {changes} com diferença. O saldo será ajustado para o valor contado e cada ajuste fica registrado no histórico.",
            TextLabel = "Motivo / responsável pela contagem", ConfirmText = "Ajustar estoque",
        }, async p =>
        {
            if (p.Text.Trim().Length < 5) { p.Error = "Informe o motivo (mínimo 5 caracteres)."; return false; }
            try
            {
                await S.Api.PostAsync("/api/v1/stock/inventory", new
                {
                    storeId = S.Settings.StoreId, reason = p.Text.Trim(),
                    counts = counted.Select(r => new { productId = r.ProductId, countedQuantity = r.CountedValue!.Value }).ToList(),
                });
                InventoryMode = false;
                await LoadAsync();
                return true;
            }
            catch (ApiException ex) { p.Error = ex.Code == "permission_denied" ? "Seu usuário não pode ajustar o estoque." : "Falha: " + ex.Code; }
            catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }
}

public sealed record CashRow(string Opened, string Closed, string Status, string Opening, string Expected, string Declared, string Difference, bool DiffNegative, bool DiffPositive, string Justification);

public sealed class CashHistoryViewModel(ShellViewModel shell) : OwnerPage(shell, "Caixas", "Aberturas, fechamentos e diferenças de caixa")
{
    public override bool HasPeriod => true;
    public ObservableCollection<CashRow> Sessions { get; } = [];

    protected override async Task LoadCoreAsync()
    {
        var (from, to) = Range();
        var list = await S.Api.GetAsync($"/api/v1/cash/sessions?from={Q(from)}&to={Q(to)}");
        Sessions.Clear();
        foreach (var c in list.EnumerateArray()) Sessions.Add(From(c));
    }

    public static CashRow From(JsonElement c)
    {
        static string? M(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? AppServices.Money(v.GetInt64()) : null;
        var diff = c.TryGetProperty("differenceCents", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0;
        var open = c.GetProperty("status").GetInt32() == 1;
        return new CashRow(c.GetProperty("openedAt").GetDateTimeOffset().LocalDateTime.ToString("dd/MM HH:mm"),
            c.TryGetProperty("closedAt", out var cl) && cl.ValueKind == JsonValueKind.String ? cl.GetDateTimeOffset().LocalDateTime.ToString("dd/MM HH:mm") : "—",
            open ? "Aberto" : "Fechado", AppServices.Money(c.GetProperty("openingCents").GetInt64()),
            M(c, "expectedCashCents") ?? "—", M(c, "declaredCashCents") ?? "—", open ? "—" : AppServices.Money(diff), diff < 0, diff > 0,
            c.TryGetProperty("justification", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString()! : "");
    }
}

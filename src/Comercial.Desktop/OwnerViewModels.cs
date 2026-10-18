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
        Kpis.Add(new Kpi("Vendas hoje", AppServices.Money(d.GetProperty("todayRevenueCents").GetInt64()), $"{d.GetProperty("todaySalesCount").GetInt32()} venda(s)", "\uE7BF", "accent"));
        if (Opt("todayGrossProfitCents") is { } tp) Kpis.Add(new Kpi("Lucro bruto hoje", AppServices.Money(tp), "estimado (sem despesas)", "\uE8C7", "primary"));
        Kpis.Add(new Kpi("Vendas no mês", AppServices.Money(d.GetProperty("monthRevenueCents").GetInt64()), $"{d.GetProperty("monthSalesCount").GetInt32()} venda(s)", "\uE787", "accent"));
        if (Opt("monthGrossProfitCents") is { } mp) Kpis.Add(new Kpi("Lucro bruto no mês", AppServices.Money(mp), "estimado (sem despesas)", "\uE8C7", "primary"));
        Kpis.Add(new Kpi("Caixas abertos", d.GetProperty("openCashSessions").GetInt32().ToString(), "agora", Icons.Bank, "neutral"));
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

public sealed record DetailLine(string Name, string Quantity, string Unit, string Total);

public sealed class HistoryViewModel : OwnerPage
{
    private SaleRow? _selected;
    private string _detailTitle = "", _detailInfo = "", _summary = "";
    public HistoryViewModel(ShellViewModel shell) : base(shell, "Histórico de vendas", "Todas as vendas recebidas pelo servidor, inclusive as feitas offline")
    {
        CancelSaleCommand = RelayCommand.Sync(_ => AskCancel(), _ => Selected is { Cancelled: false } && S.Session.Can("sales.cancel"));
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
        foreach (var i in s.GetProperty("items").EnumerateArray())
            Detail.Add(new DetailLine(i.GetProperty("productName").GetString()!, i.GetProperty("quantity").GetDecimal().ToString("0.###", AppServices.PtBr),
                AppServices.Money(i.GetProperty("unitPriceCents").GetInt64()), AppServices.Money(i.GetProperty("totalCents").GetInt64())));
        DetailTitle = $"Venda de {row.When} • {row.Total}";
        var disc = s.GetProperty("discountCents").GetInt64();
        var change = s.GetProperty("changeCents").GetInt64();
        DetailInfo = $"Operador: {row.Operator} • Pagamento: {row.Payment}" + (disc > 0 ? $" • Desconto: {AppServices.Money(disc)}" : "")
            + (change > 0 ? $" • Troco: {AppServices.Money(change)}" : "") + (row.Offline ? " • registrada offline" : "")
            + (row.Cancelled ? $" • CANCELADA: {s.GetProperty("cancelReason").GetString()}" : "");
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
    public bool HasImage { get; } = hasImage;
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
    }
    public ObservableCollection<StockRow> Items { get; } = [];
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

    protected override async Task LoadCoreAsync()
    {
        var rows = await S.Api.GetAsync($"/api/v1/stock/balances?storeId={S.Settings.StoreId}");
        SetRows(rows.EnumerateArray().Select(r => new StockRow(r.GetProperty("productId").GetGuid(), r.GetProperty("name").GetString()!, r.GetProperty("sku").GetString()!,
            r.TryGetProperty("barcode", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
            r.GetProperty("quantity").GetDecimal(), r.GetProperty("minStock").GetDecimal(), r.GetProperty("priceCents").GetInt64(),
            r.GetProperty("costCents").GetInt64(), r.GetProperty("hasImage").GetBoolean())));
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

    private static bool TryQty(string s, out decimal v) => decimal.TryParse(s, System.Globalization.NumberStyles.Number, AppServices.PtBr, out v) && v >= 0;

    /// <summary>Cadastro (row = null) ou edição de produto, com foto opcional.</summary>
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
        f.Fields.Add(new FormField("price", "Preço de venda (R$)", row is null ? "" : (row.PriceCents / 100m).ToString("0.00", AppServices.PtBr), Icons.Money));
        f.Fields.Add(new FormField("cost", "Custo (R$)", row is null ? "" : (row.CostCents / 100m).ToString("0.00", AppServices.PtBr), Icons.Money));
        f.Fields.Add(new FormField("min", "Estoque mínimo", row?.Min ?? "5", Icons.Box));
        if (row is null) f.Fields.Add(new FormField("qty", "Quantidade inicial na loja", "", Icons.Add, "0"));
        if (row?.Thumb is System.Windows.Media.Imaging.BitmapImage thumb) f.Preview = thumb;

        Shell.ShowForm(f, async form =>
        {
            if (form["name"].Length == 0 || form["sku"].Length == 0) { form.Error = "Informe o nome e o código interno."; return false; }
            if (!PromptViewModel.TryMoney(form["price"], out var price) || !PromptViewModel.TryMoney(form["cost"], out var cost)) { form.Error = "Preço ou custo inválido."; return false; }
            if (!TryQty(form["min"].Length == 0 ? "0" : form["min"], out var min)) { form.Error = "Estoque mínimo inválido."; return false; }
            decimal qty = 0;
            if (row is null && form["qty"].Length > 0 && !TryQty(form["qty"], out qty)) { form.Error = "Quantidade inicial inválida."; return false; }
            var body = new { sku = form["sku"], barcode = form["barcode"].Length == 0 ? null : form["barcode"], name = form["name"], unit = "UN", priceCents = price, costCents = cost, minStock = min };
            Guid id;
            if (row is null) id = (await S.Api.PostAsync("/api/v1/products", body)).GetProperty("id").GetGuid();
            else { await S.Api.PutAsync($"/api/v1/products/{row.ProductId}", body); id = row.ProductId; }
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
        Shell.ShowForm(f, async form =>
        {
            if (!TryQty(form["qty"], out var qty) || qty <= 0) { form.Error = "Informe a quantidade recebida."; return false; }
            if (!PromptViewModel.TryMoney(form["cost"], out var cost)) { form.Error = "Custo inválido."; return false; }
            await S.Api.PostAsync("/api/v1/stock/entries", new { storeId = S.Settings.StoreId, productId = row.ProductId, quantity = qty, unitCostCents = cost, reason = form["reason"].Length == 0 ? "entrada de mercadoria" : form["reason"] });
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

using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using Comercial.Desktop.Core;
using Comercial.Shared.Branding;
using Comercial.Shared.Contracts;

namespace Comercial.Desktop;

public sealed class MainViewModel : ObservableObject
{
    private object _current;
    public AppServices Services { get; }
    public object Current { get => _current; set => Set(ref _current, value); }

    public MainViewModel(AppServices? services = null)
    {
        Services = services ?? new AppServices();
        // Identidade em cache (última válida sincronizada) para o modo offline.
        if (Services.Db.Get("branding.config") is { } cached)
            AppServices.ApplyBranding(JsonSerializer.Deserialize<BrandingConfig>(cached, ApiClient.Json) ?? new());
        _current = new LoginViewModel(this);
    }
}

/// <summary>Diálogo simples sobreposto à tela (valor + texto), usado para caixa e cancelamento.</summary>
public sealed class PromptViewModel : ObservableObject
{
    private string _value = "", _text = "", _error = "";
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string? ValueLabel { get; init; }
    public string? TextLabel { get; init; }
    public string ConfirmText { get; init; } = "Confirmar";
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public string Error { get => _error; set => Set(ref _error, value); }
    public bool HasValue => ValueLabel is not null;
    public bool HasText => TextLabel is not null;
    public RelayCommand? ConfirmCommand { get; set; }
    public RelayCommand? CancelCommand { get; set; }

    public static bool TryMoney(string s, out long cents)
    {
        cents = 0;
        if (string.IsNullOrWhiteSpace(s)) return true;
        if (!decimal.TryParse(s.Replace("R$", "").Trim(), System.Globalization.NumberStyles.Number, AppServices.PtBr, out var v) || v < 0) return false;
        cents = (long)Math.Round(v * 100);
        return true;
    }
}

public sealed class LoginViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _tenantCode, _email = "", _status = "";
    private bool _busy;
    public string TenantCode { get => _tenantCode; set => Set(ref _tenantCode, value); }
    public string Email { get => _email; set => Set(ref _email, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public RelayCommand LoginCommand { get; }

    public LoginViewModel(MainViewModel main)
    {
        _main = main;
        _tenantCode = main.Services.Settings.TenantCode ?? "";
        LoginCommand = new RelayCommand(p => LoginAsync(((PasswordBox)p!).Password));
    }

    private async Task LoginAsync(string password)
    {
        var s = _main.Services;
        if (TenantCode.Trim().Length == 0 || Email.Trim().Length == 0 || password.Length == 0)
        {
            Status = "Preencha o código da empresa, o e-mail e a senha.";
            return;
        }
        Busy = true;
        try
        {
            Status = "Conectando...";
            if (s.Settings.DeviceId is null)
            {
                // Primeira execução: ativa este terminal (exige usuário com permissão devices.manage).
                await s.Api.LoginAsync(TenantCode.Trim(), Email.Trim(), password, null);
                var device = await s.Api.PostAsync("/api/v1/devices", new { name = Environment.MachineName, platform = "windows" });
                s.Settings.DeviceId = device.GetProperty("id").GetGuid();
            }
            await s.Api.LoginAsync(TenantCode.Trim(), Email.Trim(), password, s.Settings.DeviceId);
            s.Settings.TenantCode = TenantCode.Trim();
            var stores = await s.Api.GetAsync("/api/v1/stores");
            s.Settings.StoreId ??= stores[0].GetProperty("id").GetGuid();
            s.Settings.Save();
            if (OperatingSystem.IsWindows()) TokenVault.Save(s.TokenPath, s.Api.RefreshToken!);

            var pem = s.Settings.LicensePublicKeyPem ?? (await s.Api.GetAsync("/api/v1/license/public-key")).GetProperty("pem").GetString()!;
            s.License = new LicenseGuard(s.Db, pem, s.Settings.DeviceId!.Value);
            await RefreshOnlineDataAsync(s);
            s.Pos = new PosEngine(s.Db, s.License, TimeProvider.System);
            s.Sync = new SyncAgent(s.Db, s.Api, s.Settings.DeviceId.Value, TimeProvider.System);
            _main.Current = new ShellViewModel(_main, Email.Trim());
        }
        catch (ApiException ex)
        {
            Status = ex.Code switch
            {
                "invalid_credentials" => "Usuário ou senha inválidos.",
                "account_locked" => "Conta bloqueada temporariamente.",
                "device_not_active" => "Este terminal foi desativado. Contate o responsável.",
                "permission_denied" => "Ative o terminal com um usuário gerente ou proprietário.",
                "device_limit" => "Limite de terminais do plano atingido.",
                _ => "Falha: " + ex.Code,
            };
        }
        catch (HttpRequestException)
        {
            Status = "Sem conexão com o servidor. O primeiro acesso precisa de internet.";
        }
        finally { Busy = false; }
    }

    public static async Task RefreshOnlineDataAsync(AppServices s)
    {
        s.License!.Store(await s.Api.GetLicenseGrantAsync());
        s.Session = SessionInfo.From(await s.Api.GetAsync("/api/v1/me"));
        var branding = await s.Api.GetAsync("/api/v1/branding");
        var cfg = branding.GetProperty("config").Deserialize<BrandingConfig>(ApiClient.Json)!;
        s.Db.Set("branding.config", JsonSerializer.Serialize(cfg, ApiClient.Json));
        AppServices.ApplyBranding(cfg);
        var products = await s.Api.GetAsync("/api/v1/products");
        s.Db.ReplaceProducts(products.EnumerateArray().Select(p => (p.GetProperty("id").GetGuid(), p.GetProperty("sku").GetString()!,
            p.TryGetProperty("barcode", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
            p.GetProperty("name").GetString()!, p.GetProperty("priceCents").GetInt64(), p.GetProperty("unit").GetString()!)));
        s.Online = true;
    }
}

public sealed record NavItem(string Key, string Title, string Glyph);

/// <summary>Casca após o login: cabeçalho, navegação (vendas + área do dono) e barra de status.</summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private NavItem _selected;
    private object _page;
    private PromptViewModel? _prompt;
    private string _licenseText = "", _syncText = "", _pendingText = "", _onlineText = "";
    private bool _licenseOk, _online;

    public AppServices S => _main.Services;
    public PosViewModel Pos { get; }
    public ObservableCollection<NavItem> Nav { get; } = [];
    public string UserLabel { get; }
    public NavItem SelectedNav { get => _selected; set { if (Set(ref _selected, value)) Navigate(value); } }
    public object Page { get => _page; set => Set(ref _page, value); }
    public PromptViewModel? Prompt { get => _prompt; set => Set(ref _prompt, value); }
    private SaleDoneViewModel? _saleDone;
    private FormViewModel? _form;
    public SaleDoneViewModel? SaleDone { get => _saleDone; set => Set(ref _saleDone, value); }
    public FormViewModel? Form { get => _form; set => Set(ref _form, value); }

    public void ShowSaleDone(long total, long paid, string method) =>
        SaleDone = new SaleDoneViewModel(total, paid, method, () => SaleDone = null);

    public void ShowForm(FormViewModel f, Func<FormViewModel, Task<bool>> onConfirm)
    {
        f.CancelCommand = RelayCommand.Sync(_ => Form = null);
        f.ConfirmCommand = new RelayCommand(async _ =>
        {
            f.Busy = true; f.Error = "";
            try { if (await onConfirm(f)) Form = null; }
            catch (ApiException ex) { f.Error = "Não foi possível salvar: " + ex.Code; }
            catch (System.Net.Http.HttpRequestException) { f.Error = "Sem conexão com o servidor."; }
            finally { f.Busy = false; }
        });
        Form = f;
    }
    public string LicenseText { get => _licenseText; set => Set(ref _licenseText, value); }
    public bool LicenseOk { get => _licenseOk; set => Set(ref _licenseOk, value); }
    public string SyncText { get => _syncText; set => Set(ref _syncText, value); }
    public string PendingText { get => _pendingText; set => Set(ref _pendingText, value); }
    public bool Online { get => _online; set => Set(ref _online, value); }
    public string OnlineText { get => _onlineText; set => Set(ref _onlineText, value); }
    public RelayCommand LogoutCommand { get; }

    public ShellViewModel(MainViewModel main, string userLabel, bool startTimers = true)
    {
        _main = main;
        UserLabel = userLabel;
        Pos = new PosViewModel(this, startTimers);
        var sess = S.Session;
        Nav.Add(new NavItem("pos", "Vendas", "\uE7BF"));
        if (sess.Can("reports.read", "app_proprietario")) Nav.Add(new NavItem("dashboard", "Painel", "\uE9D2"));
        if (sess.Can("sales.read")) Nav.Add(new NavItem("history", "Histórico", "\uE81C"));
        if (sess.Can("reports.financial", "lucratividade")) Nav.Add(new NavItem("profit", "Lucro", "\uE8C7"));
        if (sess.Can("stock.read", "estoque")) Nav.Add(new NavItem("stock", "Estoque", "\uE7B8"));
        if (sess.Can("cash.review", "caixa")) Nav.Add(new NavItem("cash", "Caixas", "\uE787"));
        _selected = Nav[0];
        _page = Pos;
        LogoutCommand = RelayCommand.Sync(_ => { Pos.Stop(); _main.Current = new LoginViewModel(_main); });
        RefreshStatus();
    }

    private void Navigate(NavItem item)
    {
        Page = item.Key switch
        {
            "dashboard" => Load(new DashboardViewModel(this)),
            "history" => Load(new HistoryViewModel(this)),
            "profit" => Load(new ProfitViewModel(this)),
            "stock" => Load(new StockViewModel(this)),
            "cash" => Load(new CashHistoryViewModel(this)),
            _ => Pos,
        };
    }

    private static object Load(OwnerPage page) { _ = page.LoadAsync(); return page; }

    /// <summary>Usado só pelas capturas de tela: mostra uma página já preenchida sem chamar o servidor.</summary>
    public void ShowForPreview(string key, object page)
    {
        _selected = Nav.First(n => n.Key == key);
        Raise(nameof(SelectedNav));
        Page = page;
    }

    public void ShowPrompt(PromptViewModel p, Func<PromptViewModel, Task<bool>> onConfirm)
    {
        p.CancelCommand = RelayCommand.Sync(_ => Prompt = null);
        p.ConfirmCommand = new RelayCommand(async _ => { if (await onConfirm(p)) Prompt = null; });
        Prompt = p;
    }

    public void MarkOnline(bool online)
    {
        S.Online = online;
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        var check = S.License?.Evaluate(DateTimeOffset.UtcNow);
        LicenseOk = check?.CanOperate == true;
        LicenseText = check is null ? "—" : check.CanOperate ? check.Grant!.State : "bloqueada (" + check.Reason + ")";
        Online = S.Online;
        OnlineText = S.Online ? "Sistema online" : "Sem conexão (modo offline)";
        var last = S.Db.Get("sync.last_run_utc");
        SyncText = "Última sincronização: " + (last is null ? "nunca" : DateTimeOffset.Parse(last).LocalDateTime.ToString("dd/MM/yyyy HH:mm"));
        PendingText = $"Pendente(s): {S.Db.Pending(1000).Count}";
    }
}

public sealed class CartItemVm(CartLine line) : ObservableObject
{
    public CartLine Line { get; } = line;
    public string Name => Line.Name;
    public string Quantity => Line.Quantity.ToString("0.###", AppServices.PtBr);
    public string Unit => AppServices.Money(Line.UnitPriceCents);
    public string Total => AppServices.Money(Line.TotalCents);
}

public sealed record ProductHit(Guid Id, string Name, string Code, long PriceCents)
{
    public string Price => AppServices.Money(PriceCents);
}

public sealed record PaymentOption(int Method, string Name, string Glyph);

public sealed class PosViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer? _timer;
    private string _code = "", _status = "", _paid = "";
    private Guid? _cashSession;
    private PaymentOption _method;
    private ProductHit? _selectedHit;

    public ObservableCollection<CartItemVm> Cart { get; } = [];
    public ObservableCollection<ProductHit> Suggestions { get; } = [];
    public IReadOnlyList<PaymentOption> PaymentOptions { get; } =
        [new(1, "Dinheiro", "\uE8C7"), new(2, "Cartão de débito", "\uE8C7"), new(3, "Cartão de crédito", "\uE8C7"), new(4, "PIX", "\uE8C7")];
    public string Code { get => _code; set { if (Set(ref _code, value)) Search(); } }
    public string Paid { get => _paid; set => Set(ref _paid, value); }
    public PaymentOption Method { get => _method; set => Set(ref _method, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public ProductHit? SelectedHit { get => _selectedHit; set { if (Set(ref _selectedHit, value) && value is not null) AddProduct(value); } }
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool CartEmpty => Cart.Count == 0;
    public string TotalText => AppServices.Money(Cart.Sum(c => c.Line.TotalCents));
    public string ItemsText => Cart.Count == 0 ? "" : $"{Cart.Count} item(ns)";
    public bool CashOpen => _cashSession is not null;
    public string CashText => CashOpen ? "Caixa aberto" : "Caixa fechado";
    public string CashSubText => CashOpen ? "Pronto para vender" : "Aguardando abertura";

    public RelayCommand AddCommand { get; }
    public RelayCommand FinishCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand OpenCashCommand { get; }
    public RelayCommand CloseCashCommand { get; }
    public RelayCommand OpenStoreCommand { get; }
    public RelayCommand CloseStoreCommand { get; }
    public RelayCommand SyncCommand { get; }

    private AppServices S => _shell.S;

    public PosViewModel(ShellViewModel shell, bool startTimers = true)
    {
        _shell = shell;
        _method = PaymentOptions[0];
        Cart.CollectionChanged += (_, _) => { Raise(nameof(TotalText)); Raise(nameof(CartEmpty)); Raise(nameof(ItemsText)); };
        AddCommand = RelayCommand.Sync(_ => Add());
        ClearCommand = RelayCommand.Sync(_ => Cart.Clear(), _ => Cart.Count > 0);
        RemoveCommand = RelayCommand.Sync(p => { if (p is CartItemVm i) Cart.Remove(i); });
        FinishCommand = RelayCommand.Sync(_ => Finish(), _ => Cart.Count > 0 && CashOpen && _shell.SaleDone is null);
        OpenCashCommand = RelayCommand.Sync(_ => AskOpenCash(), _ => !CashOpen);
        CloseCashCommand = RelayCommand.Sync(_ => AskCloseCash(), _ => CashOpen);
        OpenStoreCommand = RelayCommand.Sync(_ => StoreEvent(true));
        CloseStoreCommand = RelayCommand.Sync(_ => StoreEvent(false));
        SyncCommand = new RelayCommand(_ => SyncNowAsync());
        if (!startTimers) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) => await SyncNowAsync();
        _timer.Start();
        _ = LoadCashAsync();
    }

    public void Stop() => _timer?.Stop();

    /// <summary>Usado só pelas capturas de tela (modo demonstração sem servidor).</summary>
    public void SetCashForPreview(bool open) { _cashSession = open ? Guid.NewGuid() : null; RaiseCash(); }

    private void RaiseCash() { Raise(nameof(CashOpen)); Raise(nameof(CashText)); Raise(nameof(CashSubText)); }

    private void Search()
    {
        Suggestions.Clear();
        var term = Code.Trim();
        if (term.Length >= 2 && !term.Contains('*'))
            foreach (var p in S.Db.SearchProducts(term, 6))
                Suggestions.Add(new ProductHit(p.Id, p.Name, p.Barcode ?? p.Sku, p.PriceCents));
        Raise(nameof(HasSuggestions));
    }

    private void Add()
    {
        var qty = 1m;
        var code = Code.Trim();
        if (code.Contains('*') && decimal.TryParse(code.Split('*')[0], System.Globalization.NumberStyles.Number, AppServices.PtBr, out var q) && q > 0)
        { qty = q; code = code.Split('*')[1].Trim(); }
        if (code.Length == 0) { Status = "Digite o nome, o código ou leia o código de barras do produto."; return; }
        var hits = S.Db.SearchProducts(code, 2);
        var exact = hits.FirstOrDefault(h => h.Barcode == code || h.Sku == code);
        if (exact.Id == Guid.Empty && hits.Count == 1) exact = hits[0];
        if (exact.Id == Guid.Empty) { Status = hits.Count == 0 ? "Produto não encontrado no catálogo." : "Mais de um produto encontrado: escolha na lista."; return; }
        AddLine(exact.Id, exact.Name, qty, exact.PriceCents);
    }

    private void AddProduct(ProductHit p) => AddLine(p.Id, p.Name, 1, p.PriceCents);

    public void AddLine(Guid id, string name, decimal qty, long price)
    {
        var existing = Cart.FirstOrDefault(c => c.Line.ProductId == id);
        if (existing is not null)
        {
            var idx = Cart.IndexOf(existing);
            Cart[idx] = new CartItemVm(existing.Line with { Quantity = existing.Line.Quantity + qty });
        }
        else Cart.Add(new CartItemVm(new CartLine(id, name, qty, price)));
        Code = "";
        _selectedHit = null;
        Raise(nameof(SelectedHit));
        Status = "";
    }

    private void Finish()
    {
        var total = Cart.Sum(c => c.Line.TotalCents);
        if (!PromptViewModel.TryMoney(Paid, out var paid)) { Status = "Valor recebido inválido."; return; }
        if (paid == 0) paid = total;
        if (paid < total) { Status = "Valor recebido menor que o total."; return; }
        if (Method.Method != 1 && paid > total) { Status = "Cartão e PIX não geram troco: informe o valor exato."; return; }
        try
        {
            S.Pos!.RecordSale(S.Settings.StoreId!.Value, _cashSession!.Value, Cart.Select(c => c.Line).ToList(), [new SalePaymentRequest(Method.Method, paid)]);
            Status = "";
            Cart.Clear(); Paid = "";
            _shell.ShowSaleDone(total, paid, Method.Name); // só fecha quando o operador confirmar o troco
            _ = SyncNowAsync();
        }
        catch (InvalidOperationException ex) { Status = ex.Message; }
        _shell.RefreshStatus();
    }

    private void StoreEvent(bool open)
    {
        S.Pos!.RecordStoreEvent(S.Settings.StoreId!.Value, open);
        Status = open ? "Abertura da loja registrada." : "Fechamento da loja registrado.";
        _ = SyncNowAsync();
    }

    private async Task LoadCashAsync()
    {
        try
        {
            var cur = await S.Api.GetAsync("/api/v1/cash/sessions/current");
            _cashSession = cur.ValueKind == JsonValueKind.Object ? cur.GetProperty("session").GetProperty("id").GetGuid() : null;
            S.Db.Set("cash.session", _cashSession?.ToString() ?? "");
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            // offline: último caixa conhecido
            _cashSession = Guid.TryParse(S.Db.Get("cash.session"), out var saved) ? saved : null;
        }
        RaiseCash();
    }

    private void AskOpenCash() => _shell.ShowPrompt(new PromptViewModel
    {
        Title = "Abrir caixa", Message = "Informe o valor em dinheiro na gaveta (troco inicial).", ValueLabel = "Valor inicial (R$)", ConfirmText = "Abrir caixa",
    }, async p =>
    {
        if (!PromptViewModel.TryMoney(p.Value, out var cents)) { p.Error = "Valor inválido."; return false; }
        try
        {
            var r = await S.Api.PostAsync("/api/v1/cash/sessions", new { storeId = S.Settings.StoreId, openingCents = cents, deviceId = S.Settings.DeviceId });
            _cashSession = r.GetProperty("id").GetGuid();
            S.Db.Set("cash.session", _cashSession.ToString()!);
            Status = "Caixa aberto.";
            RaiseCash();
            return true;
        }
        catch (ApiException ex) { p.Error = ex.Code == "cash_already_open" ? "Já existe um caixa aberto para você." : "Não foi possível abrir: " + ex.Code; }
        catch (HttpRequestException) { p.Error = "Abrir o caixa exige conexão com o servidor."; }
        return false;
    });

    private void AskCloseCash() => _shell.ShowPrompt(new PromptViewModel
    {
        Title = "Fechar caixa", Message = "Conte o dinheiro da gaveta e informe o valor. Diferenças exigem justificativa.",
        ValueLabel = "Dinheiro contado (R$)", TextLabel = "Justificativa (se houver diferença)", ConfirmText = "Fechar caixa",
    }, async p =>
    {
        if (!PromptViewModel.TryMoney(p.Value, out var declared)) { p.Error = "Valor inválido."; return false; }
        await SyncNowAsync();
        if (S.Db.Pending(1).Count > 0) { p.Error = "Há vendas não sincronizadas. Conecte-se e sincronize antes de fechar."; return false; }
        try
        {
            var r = await S.Api.PostAsync($"/api/v1/cash/sessions/{_cashSession}/close",
                new { declaredCashCents = declared, justification = string.IsNullOrWhiteSpace(p.Text) ? null : p.Text });
            Status = $"Caixa fechado. Esperado {AppServices.Money(r.GetProperty("expectedCashCents").GetInt64())}, diferença {AppServices.Money(r.GetProperty("differenceCents").GetInt64())}.";
            _cashSession = null;
            S.Db.Set("cash.session", "");
            RaiseCash();
            return true;
        }
        catch (ApiException ex) { p.Error = ex.Code == "justification_required" ? "Há diferença de caixa: informe a justificativa." : "Falha ao fechar: " + ex.Code; }
        catch (HttpRequestException) { p.Error = "Fechar o caixa exige conexão com o servidor."; }
        return false;
    });

    public async Task SyncNowAsync()
    {
        if (S.Sync is null) return;
        try
        {
            var r = await S.Sync.RunOnceAsync();
            try { await LoginViewModel.RefreshOnlineDataAsync(S); } catch (ApiException) { }
            if (r.Rejected > 0) Status = $"{r.Rejected} operação(ões) recusada(s) pelo servidor — revisão do gerente necessária.";
            if (r.ClockSkew) Status = "Atenção: o relógio deste computador está diferente do servidor.";
            _shell.MarkOnline(true);
        }
        catch (ApiException ex) when (ex.Status == 401)
        {
            try { await S.Api.RefreshAsync(); if (OperatingSystem.IsWindows()) TokenVault.Save(S.TokenPath, S.Api.RefreshToken!); }
            catch { Status = "Sessão expirada: faça login novamente."; }
        }
        catch (HttpRequestException) { _shell.MarkOnline(false); }
        _shell.RefreshStatus();
    }
}

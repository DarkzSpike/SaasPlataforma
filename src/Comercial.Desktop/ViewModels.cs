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
    public AppServices Services { get; } = new();
    public object Current { get => _current; set => Set(ref _current, value); }

    public MainViewModel()
    {
        // Identidade em cache (última válida sincronizada) para o modo offline.
        if (Services.Db.Get("branding.config") is { } cached)
            Services.ApplyBranding(JsonSerializer.Deserialize<BrandingConfig>(cached, ApiClient.Json) ?? new());
        _current = new LoginViewModel(this);
    }
}

public sealed class LoginViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _tenantCode, _email = "", _status = "";
    public string TenantCode { get => _tenantCode; set => Set(ref _tenantCode, value); }
    public string Email { get => _email; set => Set(ref _email, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public RelayCommand LoginCommand { get; }

    public LoginViewModel(MainViewModel main)
    {
        _main = main;
        _tenantCode = main.Services.Settings.TenantCode ?? "";
        LoginCommand = new RelayCommand(p => LoginAsync(((PasswordBox)p!).Password), _ => TenantCode.Length > 0 && Email.Length > 0);
    }

    private async Task LoginAsync(string password)
    {
        var s = _main.Services;
        try
        {
            Status = "Conectando...";
            if (s.Settings.DeviceId is null)
            {
                // Primeira execução: ativa este terminal (exige usuário com permissão devices.manage).
                await s.Api.LoginAsync(TenantCode, Email, password, null);
                var device = await s.Api.PostAsync("/api/v1/devices", new { name = Environment.MachineName, platform = "windows" });
                s.Settings.DeviceId = device.GetProperty("id").GetGuid();
            }
            await s.Api.LoginAsync(TenantCode, Email, password, s.Settings.DeviceId);
            s.Settings.TenantCode = TenantCode;
            var stores = await s.Api.GetAsync("/api/v1/stores");
            s.Settings.StoreId ??= stores[0].GetProperty("id").GetGuid();
            s.Settings.Save();
            if (OperatingSystem.IsWindows()) TokenVault.Save(s.TokenPath, s.Api.RefreshToken!);

            var pem = s.Settings.LicensePublicKeyPem ?? (await s.Api.GetAsync("/api/v1/license/public-key")).GetProperty("pem").GetString()!;
            s.License = new LicenseGuard(s.Db, pem, s.Settings.DeviceId!.Value);
            await RefreshOnlineDataAsync(s);
            s.Pos = new PosEngine(s.Db, s.License, TimeProvider.System);
            s.Sync = new SyncAgent(s.Db, s.Api, s.Settings.DeviceId.Value, TimeProvider.System);
            _main.Current = new PosViewModel(_main);
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
    }

    public static async Task RefreshOnlineDataAsync(AppServices s)
    {
        s.License!.Store(await s.Api.GetLicenseGrantAsync());
        var branding = await s.Api.GetAsync("/api/v1/branding");
        var cfg = branding.GetProperty("config").Deserialize<BrandingConfig>(ApiClient.Json)!;
        s.Db.Set("branding.config", JsonSerializer.Serialize(cfg, ApiClient.Json));
        s.ApplyBranding(cfg);
        var products = await s.Api.GetAsync("/api/v1/products");
        s.Db.ReplaceProducts(products.EnumerateArray().Select(p => (p.GetProperty("id").GetGuid(), p.GetProperty("sku").GetString()!,
            p.TryGetProperty("barcode", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
            p.GetProperty("name").GetString()!, p.GetProperty("priceCents").GetInt64(), p.GetProperty("unit").GetString()!)));
    }
}

public sealed class CartItemVm(CartLine line) : ObservableObject
{
    public CartLine Line { get; } = line;
    public string Name => Line.Name;
    public decimal Quantity => Line.Quantity;
    public string Unit => Money(Line.UnitPriceCents);
    public string Total => Money(Line.TotalCents);
    public static string Money(long cents) => (cents / 100m).ToString("C", new System.Globalization.CultureInfo("pt-BR"));
}

public sealed class PosViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private string _code = "", _status = "", _licenseText = "", _syncText = "", _paid = "";
    private Guid? _cashSession;
    private int _method = 1;

    public ObservableCollection<CartItemVm> Cart { get; } = [];
    public string Code { get => _code; set => Set(ref _code, value); }
    public string Paid { get => _paid; set => Set(ref _paid, value); }
    public int Method { get => _method; set => Set(ref _method, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string LicenseText { get => _licenseText; set => Set(ref _licenseText, value); }
    public string SyncText { get => _syncText; set => Set(ref _syncText, value); }
    public string TotalText => CartItemVm.Money(Cart.Sum(c => c.Line.TotalCents));
    public string CashText => _cashSession is null ? "Caixa fechado" : "Caixa aberto";

    public RelayCommand AddCommand { get; }
    public RelayCommand FinishCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand OpenCashCommand { get; }
    public RelayCommand CloseCashCommand { get; }
    public RelayCommand OpenStoreCommand { get; }
    public RelayCommand CloseStoreCommand { get; }
    public RelayCommand SyncCommand { get; }

    private AppServices S => _main.Services;

    public PosViewModel(MainViewModel main)
    {
        _main = main;
        AddCommand = RelayCommand.Sync(_ => Add(), _ => Code.Length > 0);
        ClearCommand = RelayCommand.Sync(_ => { Cart.Clear(); Raise(nameof(TotalText)); });
        FinishCommand = RelayCommand.Sync(_ => Finish(), _ => Cart.Count > 0 && _cashSession is not null);
        OpenCashCommand = new RelayCommand(_ => OpenCashAsync(), _ => _cashSession is null);
        CloseCashCommand = new RelayCommand(_ => CloseCashAsync(), _ => _cashSession is not null);
        OpenStoreCommand = RelayCommand.Sync(_ => { S.Pos!.RecordStoreEvent(S.Settings.StoreId!.Value, true); Status = "Abertura da loja registrada (será sincronizada)."; RefreshStatus(); });
        CloseStoreCommand = RelayCommand.Sync(_ => { S.Pos!.RecordStoreEvent(S.Settings.StoreId!.Value, false); Status = "Fechamento da loja registrado (será sincronizado)."; RefreshStatus(); });
        SyncCommand = new RelayCommand(_ => SyncNowAsync());
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) => await SyncNowAsync();
        _timer.Start();
        _ = LoadCashAsync();
        RefreshStatus();
    }

    private void Add()
    {
        var qty = 1m;
        var code = Code.Trim();
        if (code.Contains('*') && decimal.TryParse(code.Split('*')[0], out var q)) { qty = q; code = code.Split('*')[1]; }
        var p = S.Db.FindProduct(code);
        if (p is null) { Status = "Produto não encontrado no catálogo local."; return; }
        Cart.Add(new CartItemVm(new CartLine(p.Value.Id, p.Value.Name, qty, p.Value.PriceCents)));
        Code = "";
        Raise(nameof(TotalText));
    }

    private void Finish()
    {
        var total = Cart.Sum(c => c.Line.TotalCents);
        var paid = decimal.TryParse(Paid, out var v) ? (long)(v * 100) : total;
        try
        {
            S.Pos!.RecordSale(S.Settings.StoreId!.Value, _cashSession!.Value, Cart.Select(c => c.Line).ToList(), [new SalePaymentRequest(Method, paid)]);
            Status = paid > total ? $"Venda registrada. Troco: {CartItemVm.Money(paid - total)}" : "Venda registrada.";
            Cart.Clear(); Paid = "";
            Raise(nameof(TotalText));
            _ = SyncNowAsync();
        }
        catch (InvalidOperationException ex) { Status = ex.Message; }
        RefreshStatus();
    }

    private async Task LoadCashAsync()
    {
        try
        {
            var cur = await S.Api.GetAsync("/api/v1/cash/sessions/current");
            _cashSession = cur.ValueKind == JsonValueKind.Object ? cur.GetProperty("session").GetProperty("id").GetGuid() : null;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException) { _cashSession = null; }
        if (_cashSession is { } id) S.Db.Set("cash.session", id.ToString());
        else if (S.Db.Get("cash.session") is { } saved) _cashSession = Guid.Parse(saved); // offline: último caixa conhecido
        Raise(nameof(CashText));
    }

    private async Task OpenCashAsync()
    {
        try
        {
            var r = await S.Api.PostAsync("/api/v1/cash/sessions", new { storeId = S.Settings.StoreId, openingCents = 0, deviceId = S.Settings.DeviceId });
            _cashSession = r.GetProperty("id").GetGuid();
            S.Db.Set("cash.session", _cashSession.ToString()!);
            Status = "Caixa aberto.";
        }
        catch (ApiException ex) { Status = "Não foi possível abrir o caixa: " + ex.Code; }
        catch (HttpRequestException) { Status = "Abertura de caixa exige conexão."; }
        Raise(nameof(CashText));
    }

    private async Task CloseCashAsync()
    {
        await SyncNowAsync();
        if (S.Db.Pending(1).Count > 0) { Status = "Sincronize as vendas pendentes antes de fechar o caixa."; return; }
        var declared = decimal.TryParse(Paid, out var v) ? (long)(v * 100) : 0;
        try
        {
            var r = await S.Api.PostAsync($"/api/v1/cash/sessions/{_cashSession}/close", new { declaredCashCents = declared, justification = "Informado no PDV" });
            Status = $"Caixa fechado. Diferença: {CartItemVm.Money(r.GetProperty("differenceCents").GetInt64())}";
            _cashSession = null;
            S.Db.Set("cash.session", "");
        }
        catch (ApiException ex) { Status = "Falha ao fechar caixa: " + ex.Code; }
        catch (HttpRequestException) { Status = "Fechamento de caixa exige conexão."; }
        Raise(nameof(CashText));
    }

    private async Task SyncNowAsync()
    {
        try
        {
            var r = await S.Sync!.RunOnceAsync();
            try { await LoginViewModel.RefreshOnlineDataAsync(S); } catch (ApiException) { }
            if (r.Rejected > 0) Status = $"{r.Rejected} operação(ões) recusada(s) pelo servidor — revisão do gerente necessária.";
            if (r.ClockSkew) Status = "Atenção: relógio deste computador está diferente do servidor.";
        }
        catch (ApiException ex) when (ex.Status == 401)
        {
            try { await S.Api.RefreshAsync(); if (OperatingSystem.IsWindows()) TokenVault.Save(S.TokenPath, S.Api.RefreshToken!); } catch { Status = "Sessão expirada: faça login novamente."; }
        }
        catch (HttpRequestException) { /* offline: tenta no próximo ciclo */ }
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var check = S.License!.Evaluate(DateTimeOffset.UtcNow);
        LicenseText = check.CanOperate
            ? $"Licença: {check.Grant!.State} • válida offline até {check.Grant.OfflineValidUntil.LocalDateTime:g}"
            : $"Licença bloqueada: {check.Reason}";
        var pending = S.Db.Pending(1000).Count;
        var last = S.Db.Get("sync.last_run_utc");
        SyncText = $"Pendentes: {pending} • Última sincronização: {(last is null ? "nunca" : DateTimeOffset.Parse(last).LocalDateTime.ToString("g"))}";
    }
}

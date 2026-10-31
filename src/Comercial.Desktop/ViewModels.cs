using System.Net.Http.Json;
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

    // Botão flutuante do WhatsApp de atendimento: número vem do painel admin (guardado para funcionar offline).
    private string _waNumber = WhatsappLink.DefaultNumber, _waMessage = WhatsappLink.DefaultMessage;
    public bool HasWhatsapp => WhatsappLink.Build(_waNumber, _waMessage) is not null;
    public string WhatsappTip => "Dúvidas? Fale com o suporte no WhatsApp";
    public RelayCommand WhatsappCommand => RelayCommand.Sync(_ =>
    {
        var shell = Current as ShellViewModel;
        var url = WhatsappLink.Build(_waNumber, _waMessage, shell is null ? null : Services.Settings.TenantCode, shell?.UserLabel);
        if (url is null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { /* sem navegador padrão: nada a fazer */ }
    });

    private void LoadWhatsappCache()
    {
        if (Services.Db.Get("support.whatsapp") is { } n) _waNumber = n;
        if (Services.Db.Get("support.whatsapp_message") is { } m) _waMessage = m;
    }

    public async Task RefreshWhatsappAsync()
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(Services.Settings.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(10) };
            var s = await http.GetFromJsonAsync<JsonElement>("/api/v1/public/support");
            _waNumber = s.GetProperty("enabled").GetBoolean() ? s.GetProperty("whatsapp").GetString() ?? "" : "";
            _waMessage = s.GetProperty("message").GetString() ?? WhatsappLink.DefaultMessage;
            Services.Db.Set("support.whatsapp", _waNumber);
            Services.Db.Set("support.whatsapp_message", _waMessage);
            Raise(nameof(HasWhatsapp));
        }
        catch (Exception) { /* offline: mantém o último número salvo */ }
    }

    public MainViewModel(AppServices? services = null)
    {
        Services = services ?? new AppServices();
        LoadWhatsappCache();
        // Identidade em cache (última válida sincronizada) para o modo offline.
        if (Services.Db.Get("branding.config") is { } cached)
            AppServices.ApplyBranding(JsonSerializer.Deserialize<BrandingConfig>(cached, ApiClient.Json) ?? new(), Services.BrandAsset);
        _current = new LoginViewModel(this);
        _ = RefreshWhatsappAsync();
    }
}

/// <summary>Diálogo simples sobreposto à tela (valor + texto), usado para caixa e cancelamento.</summary>
public sealed class PromptViewModel : ObservableObject
{
    private string _value = "", _text = "", _error = "", _user = "";
    public string Title { get; init; } = "";
    /// <summary>Campos de autorização (e-mail e senha de quem autoriza). A senha vem do PasswordBox, sem binding.</summary>
    public string? UserLabel { get; init; }
    public string? PasswordLabel { get; init; }
    public string User { get => _user; set => Set(ref _user, value); }
    public string Password { get; set; } = "";
    public bool HasUser => UserLabel is not null;
    public bool HasPassword => PasswordLabel is not null;
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

    // "Esqueci minha senha": código enviado por e-mail + nova senha.
    private bool _resetMode, _codeSent;
    private string _resetCode = "";
    public bool ResetMode { get => _resetMode; set { if (Set(ref _resetMode, value)) RaiseModes(); } }
    public bool LoginMode => !ResetMode && !SignupMode;
    public bool NotSignup => !SignupMode;
    private void RaiseModes() { Raise(nameof(LoginMode)); Raise(nameof(NotSignup)); }

    // Verificação em duas etapas: o campo aparece quando o servidor pede (totp_required).
    private bool _needTotp;
    private string _totpCode = "";
    public bool NeedTotp { get => _needTotp; set => Set(ref _needTotp, value); }
    public string TotpCode { get => _totpCode; set => Set(ref _totpCode, value); }

    // "Criar conta grátis": dados da loja -> código no e-mail -> empresa criada com teste grátis.
    private bool _signupMode, _signupSent;
    private string _companyName = "", _ownerName = "", _document = "", _signupCode = "";
    private Guid _signupId;
    public bool SignupMode { get => _signupMode; set { if (Set(ref _signupMode, value)) RaiseModes(); } }
    public bool SignupSent { get => _signupSent; set { if (Set(ref _signupSent, value)) Raise(nameof(NotSignupSent)); } }
    public bool NotSignupSent => !SignupSent;
    public string CompanyName { get => _companyName; set => Set(ref _companyName, value); }
    public string OwnerName { get => _ownerName; set => Set(ref _ownerName, value); }
    public string Document { get => _document; set => Set(ref _document, value); }
    public string SignupCode { get => _signupCode; set => Set(ref _signupCode, value); }
    public RelayCommand SignupCommand => RelayCommand.Sync(_ => { SignupMode = true; ResetMode = false; SignupSent = false; Status = "Crie a conta da sua loja. Você recebe um código no e-mail para confirmar."; });
    public RelayCommand CancelSignupCommand => RelayCommand.Sync(_ => { SignupMode = false; Status = ""; });
    public RelayCommand StartSignupCommand => new(async p =>
    {
        var pwd = (p as PasswordBox)?.Password ?? "";
        if (CompanyName.Trim().Length == 0 || OwnerName.Trim().Length == 0 || !Email.Contains('@')) { Status = "Preencha nome da loja, seu nome e e-mail."; return; }
        try
        {
            var r = await _main.Services.Api.PostAnonymousJsonAsync("/api/v1/public/signup", new { companyName = CompanyName.Trim(), ownerName = OwnerName.Trim(),
                document = Document.Trim().Length == 0 ? null : Document.Trim(), email = Email.Trim(), password = pwd });
            _signupId = r.GetProperty("signupId").GetGuid();
            SignupSent = true;
            Status = "Enviamos um código de 6 dígitos para " + Email.Trim() + " (vale 30 minutos). Confira também o spam.";
        }
        catch (ApiException ex)
        {
            Status = ex.Code switch
            {
                "weak_password" => "Senha fraca: use 10+ caracteres com letras e números.",
                "invalid_email" => "E-mail inválido.",
                "invalid_document" => "CPF/CNPJ inválido (pode deixar em branco).",
                "signup_disabled" => "Cadastro pelo aplicativo está desativado. Fale com o fornecedor do sistema.",
                "too_many_attempts" or "rate_limited" => "Muitas tentativas. Tente de novo mais tarde.",
                _ => ex.Status == 429 ? "Muitas tentativas. Aguarde um minuto." : "Não foi possível cadastrar: " + ex.Code,
            };
        }
        catch (HttpRequestException) { Status = "Sem conexão com o servidor."; }
    });
    public RelayCommand VerifySignupCommand => new(async p =>
    {
        try
        {
            var r = await _main.Services.Api.PostAnonymousJsonAsync("/api/v1/public/signup/verify", new { signupId = _signupId, code = SignupCode.Trim() });
            TenantCode = r.GetProperty("tenantCode").GetString()!;
            var trial = r.GetProperty("trialEndsAt").GetDateTimeOffset();
            SignupMode = false; SignupSent = false; SignupCode = ""; (p as PasswordBox)?.Clear();
            Status = $"Conta criada! O código da sua empresa é \"{TenantCode}\" (anote). Teste grátis até {trial.LocalDateTime:dd/MM/yyyy}. Entre com sua senha.";
        }
        catch (ApiException ex) { Status = ex.Code == "invalid_code" ? "Código inválido ou expirado." : "Falha: " + ex.Code; }
        catch (HttpRequestException) { Status = "Sem conexão com o servidor."; }
    });
    public bool CodeSent { get => _codeSent; set => Set(ref _codeSent, value); }
    public string ResetCode { get => _resetCode; set => Set(ref _resetCode, value); }
    public RelayCommand ForgotCommand => RelayCommand.Sync(_ => { ResetMode = true; CodeSent = false; Status = "Informe o código da empresa e seu e-mail para receber um código."; });

    /// <summary>Servidor que este caixa usa (rede local, túnel de demonstração ou VPS). Exibido no rodapé do login.</summary>
    public string ServerText => "Servidor: " + _main.Services.Settings.ApiBaseUrl;

    private bool _serverMode;
    private string _serverInput = "", _serverError = "";
    public bool ServerMode { get => _serverMode; set { if (Set(ref _serverMode, value)) Raise(nameof(NotServerMode)); } }
    public bool NotServerMode => !ServerMode;
    public string ServerInput { get => _serverInput; set => Set(ref _serverInput, value); }
    public string ServerError { get => _serverError; set => Set(ref _serverError, value); }
    public RelayCommand ServerCommand => RelayCommand.Sync(_ => { ServerInput = _main.Services.Settings.ApiBaseUrl; ServerError = ""; ServerMode = true; });
    public RelayCommand CancelServerCommand => RelayCommand.Sync(_ => ServerMode = false);

    /// <summary>
    /// Troca o servidor sem reinstalar: confere se o endereço responde, salva e reabre o caixa.
    /// Recusa se houver vendas ainda não enviadas (elas pertencem ao servidor anterior).
    /// </summary>
    public RelayCommand SaveServerCommand => new(async _ =>
    {
        var s = _main.Services;
        ServerError = "";
        if (!ServerAddress.TryNormalize(ServerInput, out var url, out var error)) { ServerError = error!; return; }
        var pending = s.Db.Pending(1000).Count;
        if (pending > 0 && url != s.Settings.ApiBaseUrl)
        { ServerError = $"Há {pending} operação(ões) ainda não enviada(s) ao servidor atual. Sincronize antes de trocar."; return; }
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(12) };
            using var res = await http.GetAsync("/health");
            if (!res.IsSuccessStatusCode) { ServerError = $"O servidor respondeu {(int)res.StatusCode}. Confira o endereço."; return; }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { ServerError = "Não foi possível conectar nesse endereço."; return; }
        if (url != s.Settings.ApiBaseUrl)
        {
            // Outro servidor = outra empresa/terminal: o registro deste caixa no servidor anterior não vale lá.
            s.Settings.ApiBaseUrl = url; s.Settings.DeviceId = null; s.Settings.StoreId = null; s.Settings.LicensePublicKeyPem = null;
            s.Settings.Save();
            try { System.IO.File.Delete(s.TokenPath); } catch (System.IO.IOException) { }
        }
        System.Diagnostics.Process.Start(Environment.ProcessPath!);
        System.Windows.Application.Current.Shutdown();
    });
    public RelayCommand CancelResetCommand => RelayCommand.Sync(_ => { ResetMode = false; Status = ""; });
    public RelayCommand SendCodeCommand => new(async _ =>
    {
        if (TenantCode.Trim().Length == 0 || !Email.Contains('@')) { Status = "Preencha o código da empresa e o e-mail."; return; }
        try
        {
            await _main.Services.Api.PostAnonymousAsync("/api/v1/auth/forgot", new { tenantCode = TenantCode.Trim(), email = Email.Trim() });
            CodeSent = true;
            Status = "Se o e-mail estiver cadastrado, enviamos um código (vale 30 minutos). Confira também o spam.";
        }
        catch (ApiException ex) { Status = ex.Status == 429 ? "Muitas tentativas. Aguarde um minuto." : "Não foi possível enviar: " + ex.Code; }
        catch (HttpRequestException) { Status = "Sem conexão com o servidor."; }
    });
    public RelayCommand ResetCommand => new(async p =>
    {
        var pwd = (p as PasswordBox)?.Password ?? "";
        try
        {
            await _main.Services.Api.PostAnonymousAsync("/api/v1/auth/reset", new { tenantCode = TenantCode.Trim(), email = Email.Trim(), code = ResetCode.Trim(), newPassword = pwd });
            ResetMode = false; ResetCode = "";
            Status = "Senha alterada. Entre com a nova senha.";
        }
        catch (ApiException ex)
        {
            Status = ex.Code switch
            {
                "invalid_code" => "Código inválido ou expirado. Peça um novo.",
                "weak_password" => "Senha fraca: use 10+ caracteres com letras e números.",
                _ => "Não foi possível trocar a senha: " + ex.Code,
            };
        }
        catch (HttpRequestException) { Status = "Sem conexão com o servidor."; }
        finally { (p as PasswordBox)?.Clear(); }
    });

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
            // Outra empresa neste computador: o terminal salvo não vale para ela.
            if (!string.Equals(s.Settings.TenantCode, TenantCode.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                s.Settings.DeviceId = null;
                s.Settings.StoreId = null;
            }
            var activated = false;
            if (s.Settings.DeviceId is null) { await ActivateDeviceAsync(s, password); activated = true; }
            try
            {
                await s.Api.LoginAsync(TenantCode.Trim(), Email.Trim(), password, s.Settings.DeviceId, TotpCode);
            }
            catch (ApiException ex) when (ex.Code == "invalid_totp" && activated)
            {
                // O mesmo código do autenticador não vale duas vezes: depois de ativar o terminal, é preciso o próximo.
                TotpCode = "";
                Status = "Terminal ativado! Espere o código do autenticador mudar e digite o novo para entrar.";
                return;
            }
            catch (ApiException ex) when (ex.Code == "device_not_active")
            {
                // Terminal salvo foi desativado (ou não existe mais). Um dono/gerente pode cadastrar este
                // computador como um NOVO terminal; o antigo continua revogado. Operador comum recebe permission_denied.
                s.Settings.DeviceId = null;
                s.Settings.StoreId = null;
                await ActivateDeviceAsync(s, password);
                Status = "Terminal ativado. " + (NeedTotp ? "Digite o PRÓXIMO código do autenticador e entre de novo." : "Entre de novo.");
                if (NeedTotp) { TotpCode = ""; return; }
                await s.Api.LoginAsync(TenantCode.Trim(), Email.Trim(), password, s.Settings.DeviceId);
            }
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
            var shell = new ShellViewModel(_main, Email.Trim());
            _main.Current = shell;
            _ = shell.CheckUpdateAsync(pem);
        }
        catch (ApiException ex)
        {
            if (ex.Code is "totp_required" or "invalid_totp") { NeedTotp = true; TotpCode = ""; }
            Status = ex.Code switch
            {
                "invalid_credentials" => "Usuário ou senha inválidos.",
                "account_locked" => "Conta bloqueada temporariamente.",
                "device_not_active" => "Este terminal foi desativado. Contate o responsável.",
                "permission_denied" => "Este computador ainda não é um terminal ativo. Entre uma vez com o dono ou um gerente para ativá-lo.",
                "device_limit" => "Limite de terminais do plano atingido.",
                "totp_required" => "Digite o código de 6 dígitos do aplicativo autenticador.",
                "invalid_totp" => "Código do autenticador incorreto (ou já usado). Espere o próximo.",
                _ => "Falha: " + ex.Code,
            };
        }
        catch (HttpRequestException)
        {
            Status = "Sem conexão com o servidor. O primeiro acesso precisa de internet.";
        }
        finally { Busy = false; }
    }

    /// <summary>Cadastra este computador como terminal da empresa (exige permissão devices.manage no servidor).</summary>
    private async Task ActivateDeviceAsync(AppServices s, string password)
    {
        Status = "Ativando este computador como terminal...";
        await s.Api.LoginAsync(TenantCode.Trim(), Email.Trim(), password, null, TotpCode);
        var device = await s.Api.PostAsync("/api/v1/devices", new { name = Environment.MachineName, platform = "windows" });
        s.Settings.DeviceId = device.GetProperty("id").GetGuid();
        s.Settings.TenantCode = TenantCode.Trim();
        s.Settings.Save();
    }

    public static async Task RefreshOnlineDataAsync(AppServices s)
    {
        s.License!.Store(await s.Api.GetLicenseGrantAsync());
        s.Session = SessionInfo.From(await s.Api.GetAsync("/api/v1/me"));
        var branding = await s.Api.GetAsync("/api/v1/branding");
        var cfg = branding.GetProperty("config").Deserialize<BrandingConfig>(ApiClient.Json)!;
        s.Db.Set("branding.config", JsonSerializer.Serialize(cfg, ApiClient.Json));
        // Imagens da empresa: baixa só as que ainda não estão no computador (ficam no banco local, valem offline).
        foreach (var id in new[] { cfg.LogoAssetId, cfg.LogoDarkAssetId, cfg.IconAssetId, cfg.LoginImageAssetId, cfg.LoginBackgroundAssetId }.OfType<Guid>())
        {
            if (s.BrandAsset(id) is not null) continue;
            try { if (await s.Api.GetBytesAsync($"/api/v1/branding/assets/{id}") is { Length: > 0 } bytes) s.Db.Set("brand.asset." + id.ToString("N"), Convert.ToBase64String(bytes)); }
            catch (Exception ex) when (ex is ApiException or HttpRequestException) { /* tenta no próximo login; usa o padrão */ }
        }
        AppServices.ApplyBranding(cfg, s.BrandAsset);
        var products = await s.Api.GetAsync("/api/v1/catalog/products");
        s.Db.ReplaceProducts(products.EnumerateArray().Select(p => (p.GetProperty("id").GetGuid(), p.GetProperty("sku").GetString()!,
            p.TryGetProperty("barcode", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
            p.GetProperty("name").GetString()!, p.GetProperty("priceCents").GetInt64(), p.GetProperty("unit").GetString()!)));
        s.Db.Set("catalog.openprice", JsonSerializer.Serialize(products.EnumerateArray()
            .Where(p => p.TryGetProperty("openPrice", out var op) && op.ValueKind == JsonValueKind.True).Select(p => p.GetProperty("id").GetGuid())));
        if (s.Session.Can("fiscal.emit", "fiscal"))
            try { s.Db.Set("fiscal.mode", (await s.Api.GetAsync("/api/v1/fiscal/mode")).GetProperty("mode").GetString() ?? "sempre"); }
            catch (ApiException) { /* sem permissão/módulo: mantém o último */ }
        try
        {
            var codes = await s.Api.GetAsync("/api/v1/catalog/barcodes");
            s.Db.ReplaceCodes(codes.EnumerateArray().Select(c => (c.GetProperty("code").GetString()!, c.GetProperty("productId").GetGuid())));
            s.Db.Set("promotions", (await s.Api.GetAsync("/api/v1/promotions/active")).GetRawText());
        }
        catch (ApiException) { /* sem permissão/módulo: segue sem códigos extras e promoções */ }
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
    public NavItem SelectedNav { get => _selected; set { if (Set(ref _selected, value) && value is not null) Navigate(value); } }
    public object Page { get => _page; set => Set(ref _page, value); }
    public PromptViewModel? Prompt { get => _prompt; set => Set(ref _prompt, value); }
    private SaleDoneViewModel? _saleDone;
    private FormViewModel? _form;
    public SaleDoneViewModel? SaleDone { get => _saleDone; set => Set(ref _saleDone, value); }
    /// <summary>A loja não emite NFC-e (descoberto na 1ª venda): as próximas imprimem o comprovante na hora, sem esperar.</summary>
    public bool NfceOff { get; set; }
    private PixPayViewModel? _pixPay;
    public PixPayViewModel? PixPay { get => _pixPay; set => Set(ref _pixPay, value); }
    public FormViewModel? Form { get => _form; set => Set(ref _form, value); }

    public void ShowSaleDone(long total, long paid, string method, Receipt? receipt = null) =>
        SaleDone = new SaleDoneViewModel(total, paid, method, () => SaleDone = null, receipt);

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
        var Nav = AvailableNav; // monta a lista completa; o menu visível sai dela (ordem e ícones escondidos na engrenagem)
        Nav.Add(new NavItem("pos", "Vendas", "\uE7BF"));
        if (sess.Can("reports.read", "app_proprietario")) Nav.Add(new NavItem("dashboard", "Painel", "\uE9D2"));
        if (sess.Can("sales.read")) Nav.Add(new NavItem("history", "Histórico", "\uE81C"));
        if (sess.Can("reports.financial", "lucratividade")) Nav.Add(new NavItem("profit", "Lucro", "\uE8C7"));
        if (sess.Can("stock.read", "estoque")) Nav.Add(new NavItem("stock", "Estoque", "\uE7B8"));
        if (sess.Can("purchases.manage", "compras") || sess.Can("suppliers.manage", "fornecedores")) Nav.Add(new NavItem("purchases", "Compras", "\uE7BF"));
        if (sess.Can("customers.read", "crm")) Nav.Add(new NavItem("customers", "Clientes", "\uE716"));
        if (sess.Can("finance.read", "contas") || sess.Can("finance.read", "financeiro")) Nav.Add(new NavItem("finance", "Financeiro", "\uE8C7"));
        if (sess.Can("cash.review", "caixa")) Nav.Add(new NavItem("cash", "Caixas", "\uE787"));
        if (sess.Can("sync.reconcile")) Nav.Add(new NavItem("pending", "Pend\u00EAncias", "\uE7BA"));
        if (sess.Can("reports.financial")) Nav.Add(new NavItem("alerts", "Avisos", "\uEA8F"));
        if (sess.Can("users.manage")) Nav.Add(new NavItem("employees", "Funcionários", "\uE77B"));
        if (sess.Can("reports.read", "relatorios_premium")) Nav.Add(new NavItem("reports", "Relatórios", "\uE9F9"));
        if (sess.Can("sales.create", "pdv")) Nav.Add(new NavItem("quotes", "Orçamentos", "\uE8A5"));
        if (sess.Can("products.write", "pdv")) Nav.Add(new NavItem("promotions", "Promoções", "\uE8EC"));
        if (sess.Can("messaging.manage", "campanhas")) Nav.Add(new NavItem("campaigns", "Campanhas", "\uE789"));
        if (sess.Can("fiscal.read", "fiscal")) Nav.Add(new NavItem("fiscal", "Fiscal", "\uE7C3"));
        if (sess.Can("cash.operate")) Nav.Add(new NavItem("devices", "Dispositivos", "\uE749"));
        if (sess.Can("payments.configure", "pdv")) Nav.Add(new NavItem("receipts", "Recebimentos", "\uE8C7"));
        if (sess.Can("billing.manage")) Nav.Add(new NavItem("invoices", "Faturas", "\uE8A5"));
        if (sess.Can("support.approve")) Nav.Add(new NavItem("support", "Suporte", "\uE897"));
        Nav.Add(new NavItem("account", "Minha conta", "\uE77B"));
        Nav.Add(new NavItem("help", "Ajuda", "\uE897"));
        ApplyNavPrefs();
        _selected = this.Nav[0];
        _page = Pos;
        LogoutCommand = RelayCommand.Sync(_ => { Pos.Stop(); StopUpdateChecks(); _main.Current = new LoginViewModel(_main); });
        RefreshStatus();
    }

    /// <summary>Todas as áreas que este usuário pode abrir (o menu mostra só as não escondidas, na ordem escolhida).</summary>
    public List<NavItem> AvailableNav { get; } = [];

    /// <summary>Aplica a ordem e os ícones escondidos salvos neste computador. "Vendas" nunca some.</summary>
    public void ApplyNavPrefs()
    {
        var order = S.Settings.NavOrder;
        var hidden = S.Settings.NavHidden.Where(k => k != "pos").ToHashSet();
        var sorted = AvailableNav.OrderBy(n => order.IndexOf(n.Key) is var i && i >= 0 ? i : order.Count + AvailableNav.IndexOf(n)).ToList();
        var current = _selected?.Key;
        Nav.Clear();
        foreach (var n in sorted.Where(n => !hidden.Contains(n.Key))) Nav.Add(n);
        if (current is not null && Nav.FirstOrDefault(n => n.Key == current) is { } keep) { _selected = keep; Raise(nameof(SelectedNav)); }
    }

    /// <summary>Engrenagem do cabeçalho: menu lateral deste computador e WhatsApp da loja.</summary>
    public RelayCommand OpenSettingsCommand => RelayCommand.Sync(_ =>
    {
        _selected = null!;
        Raise(nameof(SelectedNav));
        Page = Load(new SettingsViewModel(this));
    });

    private void Navigate(NavItem item)
    {
        Page = item.Key switch
        {
            "dashboard" => Load(new DashboardViewModel(this)),
            "history" => Load(new HistoryViewModel(this)),
            "profit" => Load(new ProfitViewModel(this)),
            "stock" => Load(new StockViewModel(this)),
            "cash" => Load(new CashHistoryViewModel(this)),
            "purchases" => Load(new PurchasesViewModel(this)),
            "customers" => Load(new CustomersViewModel(this)),
            "finance" => Load(new FinanceViewModel(this)),
            "pending" => Load(new PendingViewModel(this)),
            "alerts" => Load(new AlertsViewModel(this)),
            "employees" => Load(new EmployeesViewModel(this)),
            "invoices" => Load(new InvoicesViewModel(this)),
            "receipts" => Load(new StorePaymentsViewModel(this)),
            "fiscal" => Load(new FiscalViewModel(this)),
            "quotes" => Load(new QuotesViewModel(this)),
            "reports" => Load(new ReportsViewModel(this)),
            "promotions" => Load(new PromotionsViewModel(this)),
            "campaigns" => Load(new CampaignsViewModel(this)),
            "devices" => Load(new DevicesViewModel(this)),
            "support" => Load(new SupportViewModel(this)),
            "account" => Load(new AccountViewModel(this)),
            "help" => Load(new HelpViewModel(this)),
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

    public void ShowPrompt(PromptViewModel p, Func<PromptViewModel, Task<bool>> onConfirm, Action? onCancel = null)
    {
        p.CancelCommand = RelayCommand.Sync(_ => { Prompt = null; onCancel?.Invoke(); });
        p.ConfirmCommand = new RelayCommand(async _ => { if (await onConfirm(p)) Prompt = null; });
        Prompt = p;
    }

    // ---------- Atualização automática (versão assinada + SHA-256) ----------
    private UpdateInfo? _update;
    private string _updateText = "";
    private bool _updating;
    public bool HasUpdate => _update is not null;
    public string UpdateText { get => _updateText; set => Set(ref _updateText, value); }
    public static Version CurrentVersion => typeof(ShellViewModel).Assembly.GetName().Version ?? new Version(1, 0, 0);
    public string VersionText => "Versão " + CurrentVersion.ToString(3);

    /// <summary>Usado só pelas capturas de tela.</summary>
    public void SetUpdateForPreview(string text)
    {
        _update = new UpdateInfo("1.1.0", "https://exemplo.test/setup.exe", new string('0', 64), null, false, "");
        UpdateText = text; Raise(nameof(HasUpdate));
    }

    private DispatcherTimer? _updateTimer;
    private string? _askedVersion;

    /// <summary>
    /// Confere se há versão nova no login e a cada 30 minutos. A faixa no topo fica visível e, uma vez por versão,
    /// o caixa pergunta "Atualizar agora?" (só com o carrinho vazio, para não atrapalhar uma venda).
    /// </summary>
    public async Task CheckUpdateAsync(string licensePem)
    {
        if (_updateTimer is null)
        {
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            _updateTimer.Tick += async (_, _) => await CheckUpdateAsync(licensePem);
            _updateTimer.Start();
        }
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(S.Settings.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(15) };
            var found = await Updater.CheckAsync(http, CurrentVersion, licensePem);
            if (found is null || _updating) return;
            _update = found;
            UpdateText = $"Nova versão {_update.Version} disponível" + (_update.Mandatory ? " (obrigatória)" : "") + (string.IsNullOrWhiteSpace(_update.Notes) ? "." : ": " + _update.Notes);
            Raise(nameof(HasUpdate));
            if (_askedVersion != found.Version && Prompt is null && Form is null && Pos.Cart.Count == 0)
            {
                _askedVersion = found.Version;
                ShowPrompt(new PromptViewModel
                {
                    Title = $"Nova versão {found.Version} disponível",
                    Message = (string.IsNullOrWhiteSpace(found.Notes) ? "" : found.Notes.Trim() + "\n\n")
                        + "Atualizar agora? O caixa baixa a versão nova, fecha e abre de novo sozinho (leva cerca de um minuto). "
                        + "Se preferir, atualize depois pelo botão na faixa do topo.",
                    ConfirmText = "Atualizar agora",
                }, async _ => { await RunUpdateAsync(); return true; });
            }
        }
        catch (Exception) { /* sem internet ou servidor antigo: tenta de novo daqui a 30 minutos */ }
    }

    public void StopUpdateChecks() => _updateTimer?.Stop();

    public RelayCommand UpdateCommand => new(async _ => await RunUpdateAsync());

    private async Task RunUpdateAsync()
    {
        if (_update is null || _updating) return;
        if (S.Db.Pending(1).Count > 0 && !_update.Mandatory) { UpdateText = "Há vendas aguardando sincronização. Aguarde enviar (ícone de nuvem) e clique de novo."; return; }
        _updating = true;
        try
        {
            UpdateText = "Baixando a atualização...";
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var file = await Updater.DownloadAsync(http, _update, System.IO.Path.Combine(DesktopSettings.DataDir, "updates"));
            UpdateText = "Instalando... o caixa vai fechar e abrir de novo.";
            Pos.Stop();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex) { UpdateText = "Não foi possível atualizar: " + ex.Message; }
        finally { _updating = false; }
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
    private long _promo;
    private string? _promoName;
    public CartLine Line { get; } = line;
    public long PromoCents { get => _promo; set { if (Set(ref _promo, value)) { Raise(nameof(Total)); Raise(nameof(HasPromo)); } } }
    public string? PromoName { get => _promoName; set { if (Set(ref _promoName, value)) Raise(nameof(PromoText)); } }
    public bool HasPromo => PromoCents > 0;
    public string PromoText => PromoCents > 0 ? $"{PromoName}: -{AppServices.Money(PromoCents)}" : "";
    public string Name => Line.Name;
    public string Quantity => Line.Quantity.ToString("0.###", AppServices.PtBr);
    public string Unit => AppServices.Money(Line.UnitPriceCents);
    public string Total => AppServices.Money(Line.TotalCents - PromoCents);
}

public sealed record ProductHit(Guid Id, string Name, string Code, long PriceCents)
{
    public string Price => AppServices.Money(PriceCents);
}

public sealed record PaymentOption(int Method, string Name, string Glyph);
public sealed record PaymentLine(int Method, string Name, long Cents)
{
    public string Text => $"{Name}: {AppServices.Money(Cents)}";
}
public sealed record CustomerHit(Guid Id, string Name, string Info);

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
        [new(1, "Dinheiro", "\uE8C7"), new(2, "Cartão de débito", "\uE8C7"), new(3, "Cartão de crédito", "\uE8C7"), new(4, "PIX", "\uE8C7"), new(5, "Fiado (crediário)", "\uE8C7")];
    public string Code { get => _code; set { if (Set(ref _code, value)) Search(); } }
    public string Paid { get => _paid; set => Set(ref _paid, value); }

    // Várias formas de pagamento na mesma venda (ex.: parte no PIX, resto em dinheiro).
    public ObservableCollection<PaymentLine> PaymentLines { get; } = [];
    public bool HasPaymentLines => PaymentLines.Count > 0;
    private long PaidLinesCents => PaymentLines.Sum(p => p.Cents);
    private long RemainingCents => PaymentSplit.Remaining(TotalCents, PaymentLines.Select(p => (p.Method, p.Cents)));
    public string RemainingText => HasPaymentLines ? $"Falta receber: {AppServices.Money(RemainingCents)}" : "";
    /// <summary>Destaque "Falta pagar" logo abaixo do total quando o cliente paga em partes.</summary>
    public bool HasRemaining => HasPaymentLines && RemainingCents > 0;
    public bool PaymentComplete => HasPaymentLines && RemainingCents == 0;
    public string RemainingAmountText => AppServices.Money(RemainingCents);
    public string PaidSoFarText => $"Já recebido: {AppServices.Money(PaidLinesCents)}";
    public RelayCommand AddPaymentCommand => RelayCommand.Sync(_ => AddPayment(), _ => Cart.Count > 0);
    public RelayCommand RemovePaymentCommand => RelayCommand.Sync(p => { if (p is PaymentLine l) { PaymentLines.Remove(l); RaisePayments(); } });
    private void RaisePayments()
    {
        foreach (var n in new[] { nameof(HasPaymentLines), nameof(RemainingText), nameof(HasRemaining), nameof(PaymentComplete), nameof(RemainingAmountText), nameof(PaidSoFarText) }) Raise(n);
    }

    private void AddPayment()
    {
        if (!PromptViewModel.TryMoney(Paid, out var cents)) { Status = "Valor inválido."; return; }
        var remaining = PaymentSplit.Remaining(TotalCents, PaymentLines.Select(p => (p.Method, p.Cents)));
        if (cents == 0) cents = remaining;
        if (cents <= 0) { Status = "O total já foi coberto."; return; }
        if (Method.Method != PaymentSplit.Cash && cents > remaining) { Status = $"{Method.Name} não gera troco: no máximo {AppServices.Money(remaining)}."; return; }
        var method = Method;
        void Add()
        {
            PaymentLines.Add(new PaymentLine(method.Method, method.Name, cents));
            Paid = ""; Status = "";
            RaisePayments();
        }
        if (method.Method != 5) { Add(); return; }
        if (_customer is null) { Status = "Fiado: identifique o cliente antes de lançar."; return; }
        _ = CheckCreditThenAsync(PaymentLines.Where(l => l.Method == 5).Sum(l => l.Cents) + cents, Add);
    }
    public PaymentOption Method { get => _method; set => Set(ref _method, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public ProductHit? SelectedHit { get => _selectedHit; set { if (Set(ref _selectedHit, value) && value is not null) AddProduct(value); } }
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool CartEmpty => Cart.Count == 0;
    public string TotalText => AppServices.Money(TotalCents);
    private long CartCents => Cart.Sum(c => c.Line.TotalCents);
    private long TotalCents => Math.Max(0, CartCents - PromoCents - SaleDiscountCents - LoyaltyDiscountCents);

    // Promoções e cupom: mesmo cálculo do servidor (regras guardadas no catálogo local).
    private long PromoCents => Cart.Sum(c => c.PromoCents);
    private string _coupon = "";
    public string Coupon { get => _coupon; set { if (Set(ref _coupon, value.Trim().ToUpperInvariant())) RecomputePromos(); } }
    public string PromoSummary => PromoCents > 0 ? $"Promoções: -{AppServices.Money(PromoCents)}" : "";
    private List<Comercial.Shared.Promotions.PromoRule> Rules()
    {
        try { return JsonSerializer.Deserialize<List<Comercial.Shared.Promotions.PromoRule>>(S.Db.Get("promotions") ?? "[]", ApiClient.Json) ?? []; }
        catch (JsonException) { return []; }
    }
    private bool _recomputing;
    private void RecomputePromos()
    {
        if (_recomputing) return;
        _recomputing = true;
        try
        {
            var res = Comercial.Shared.Promotions.PromotionEngine.Apply(Cart.Select(c => new Comercial.Shared.Promotions.PromoLine(c.Line.ProductId, c.Line.Quantity, c.Line.UnitPriceCents)).ToList(),
                Rules(), DateTimeOffset.Now, Coupon.Length == 0 ? null : Coupon);
            for (var i = 0; i < Cart.Count; i++) { Cart[i].PromoCents = res[i].DiscountCents; Cart[i].PromoName = res[i].RuleName; }
        }
        finally { _recomputing = false; }
        Raise(nameof(PromoSummary)); RaiseTotals();
    }

    // Desconto no total da venda: só com autorização do proprietário (ou de quem tem a permissão).
    private long _saleDiscount;
    private Guid? _discountAuthId;
    private string _discountBy = "";
    private long SaleDiscountCents => _saleDiscount <= CartCents ? _saleDiscount : 0;
    public bool HasSaleDiscount => SaleDiscountCents > 0;
    public string SaleDiscountText => HasSaleDiscount ? $"Desconto: -{AppServices.Money(SaleDiscountCents)} (autorizado por {_discountBy})" : "";
    public RelayCommand DiscountCommand => RelayCommand.Sync(_ => AskDiscount(), _ => Cart.Count > 0 && CashOpen);

    // Orçamento: salvar o carrinho como orçamento ou abrir um orçamento no carrinho.
    private Guid? _quoteId;
    public RelayCommand SaveQuoteCommand => RelayCommand.Sync(_ => _shell.ShowPrompt(new PromptViewModel
    {
        Title = "Salvar como orçamento", Message = $"{Cart.Count} item(ns), {AppServices.Money(CartCents)}. O orçamento usa os preços de hoje e pode virar venda depois.",
        ValueLabel = "Validade (dias)", TextLabel = "Nome do cliente (opcional)", ConfirmText = "Salvar orçamento",
    }, async p =>
    {
        var days = int.TryParse(p.Value.Trim(), out var d) && d is > 0 and <= 90 ? d : 7;
        try
        {
            var q = await S.Api.PostAsync("/api/v1/quotes", new
            {
                customerId = _customer?.Id, customerName = p.Text.Trim().Length > 0 ? p.Text.Trim() : null, validDays = days,
                items = Cart.Select(c => new { productId = c.Line.ProductId, quantity = c.Line.Quantity }).ToList(),
            });
            Status = $"Orçamento nº {q.GetProperty("number").GetInt32()} salvo ({AppServices.Money(q.GetProperty("totalCents").GetInt64())}). Veja em Orçamentos.";
            Cart.Clear(); ClearDiscount(); Coupon = "";
            return true;
        }
        catch (ApiException ex) { p.Error = "Falha: " + ex.Code; }
        catch (HttpRequestException) { p.Error = "Salvar orçamento exige conexão com o servidor."; }
        return false;
    }), _ => Cart.Count > 0);

    /// <summary>Carrega um orçamento no carrinho (os preços são conferidos de novo na venda).</summary>
    public void LoadQuote(Guid quoteId, IEnumerable<(Guid ProductId, string Name, decimal Qty)> items)
    {
        Cart.Clear(); ClearDiscount(); Coupon = "";
        foreach (var i in items)
            if (S.Db.GetProduct(i.ProductId) is { } p) AddLine(i.ProductId, p.Name, i.Qty, p.PriceCents);
        _quoteId = quoteId;
        Status = "Orçamento carregado. Confira e finalize a venda.";
    }
    public RelayCommand RemoveDiscountCommand => RelayCommand.Sync(_ => ClearDiscount());

    private void ClearDiscount()
    {
        _saleDiscount = 0; _discountAuthId = null; _discountBy = "";
        Raise(nameof(HasSaleDiscount)); Raise(nameof(SaleDiscountText)); RaiseTotals();
    }

    private void AskDiscount()
    {
        var self = S.Session.Can("sales.discount.authorize");
        _shell.ShowPrompt(new PromptViewModel
        {
            Title = "Desconto no total da venda",
            Message = (self ? "Você pode autorizar este desconto." : "Precisa da autorização do proprietário: ele digita o e-mail e a senha dele aqui.")
                      + $" Total atual: {AppServices.Money(CartCents)}. Informe o valor (ex.: 5,00) ou o percentual (ex.: 10%).",
            ValueLabel = "Desconto (R$ ou %)", TextLabel = "Motivo do desconto",
            UserLabel = self ? null : "E-mail do proprietário", PasswordLabel = self ? null : "Senha do proprietário",
            ConfirmText = "Aplicar desconto",
        }, async p =>
        {
            var raw = p.Value.Trim();
            long cents;
            if (raw.EndsWith('%'))
            {
                if (!decimal.TryParse(raw.TrimEnd('%').Trim(), System.Globalization.NumberStyles.Number, AppServices.PtBr, out var pct) || pct <= 0 || pct > 100)
                { p.Error = "Percentual inválido."; return false; }
                cents = (long)Math.Round(CartCents * pct / 100m);
            }
            else if (!PromptViewModel.TryMoney(raw, out cents)) { p.Error = "Valor inválido."; return false; }
            if (cents <= 0 || cents >= CartCents) { p.Error = "O desconto deve ser maior que zero e menor que o total."; return false; }
            if (p.Text.Trim().Length < 3) { p.Error = "Informe o motivo do desconto."; return false; }
            if (self)
            {
                _saleDiscount = cents; _discountAuthId = null; _discountBy = "você";
            }
            else
            {
                if (p.User.Trim().Length == 0 || p.Password.Length == 0) { p.Error = "O proprietário precisa digitar e-mail e senha."; return false; }
                try
                {
                    var r = await S.Api.PostAsync("/api/v1/sales/discount-authorizations",
                        new { approverEmail = p.User.Trim(), approverPassword = p.Password, discountCents = cents, subtotalCents = CartCents, reason = p.Text.Trim() });
                    _saleDiscount = cents; _discountAuthId = r.GetProperty("id").GetGuid(); _discountBy = r.GetProperty("approverName").GetString() ?? "proprietário";
                }
                catch (ApiException ex)
                {
                    p.Error = ex.Code switch
                    {
                        "invalid_credentials" => "E-mail ou senha do proprietário incorretos.",
                        "approver_not_allowed" => "Este usuário não pode autorizar descontos.",
                        "account_locked" => "Conta do proprietário bloqueada por tentativas erradas. Aguarde alguns minutos.",
                        _ => "Não foi possível autorizar: " + ex.Code,
                    };
                    return false;
                }
                catch (HttpRequestException) { p.Error = "A autorização de desconto precisa de conexão com o servidor."; return false; }
                finally { p.Password = ""; }
            }
            Raise(nameof(HasSaleDiscount)); Raise(nameof(SaleDiscountText)); RaiseTotals();
            Status = $"Desconto de {AppServices.Money(cents)} aplicado.";
            return true;
        });
    }

    // ---- Cliente (CRM) e pontos (fidelidade) ----
    private CustomerHit? _customer, _customerHitSel;
    private string _customerQuery = "", _redeem = "";
    private int _pointsAvailable, _centsPerPoint, _minRedeem, _maxRedeemPct;
    public bool CanUseCustomers => S.Session.Can("customers.read", "crm");
    public ObservableCollection<CustomerHit> CustomerHits { get; } = [];
    public string CustomerQuery { get => _customerQuery; set => Set(ref _customerQuery, value); }
    public CustomerHit? SelectedCustomerHit { get => _customerHitSel; set { if (Set(ref _customerHitSel, value) && value is not null) _ = SelectCustomerAsync(value); } }
    public bool HasCustomer => _customer is not null;
    public bool NoCustomer => _customer is null;
    public string CustomerText => _customer is null ? "" : _customer.Name + (_centsPerPoint > 0 ? $"  •  {_pointsAvailable} ponto(s)" : "");
    public bool CanRedeem => _customer is not null && _centsPerPoint > 0 && _pointsAvailable >= _minRedeem && _pointsAvailable > 0;
    public string RedeemHint => CanRedeem ? $"1 ponto = {AppServices.Money(_centsPerPoint)} • mínimo {_minRedeem} • até {_maxRedeemPct}% da compra" : "";
    public string Redeem { get => _redeem; set { if (Set(ref _redeem, value)) RaiseTotals(); } }
    private int RedeemPoints => int.TryParse(Redeem, out var p) && p > 0 ? p : 0;
    private long LoyaltyDiscountCents => CanRedeem ? (long)RedeemPoints * _centsPerPoint : 0;
    public string LoyaltyText => LoyaltyDiscountCents > 0 ? $"Pontos: -{AppServices.Money(LoyaltyDiscountCents)}" : "";
    public RelayCommand SearchCustomerCommand { get; private set; } = null!;
    public RelayCommand ClearCustomerCommand { get; private set; } = null!;

    private void RaiseTotals() { Raise(nameof(TotalText)); Raise(nameof(LoyaltyText)); Raise(nameof(HasSaleDiscount)); Raise(nameof(SaleDiscountText)); RaisePayments(); }

    private void RaiseCustomer()
    {
        Raise(nameof(HasCustomer)); Raise(nameof(NoCustomer)); Raise(nameof(CustomerText)); Raise(nameof(CanRedeem)); Raise(nameof(RedeemHint)); RaiseTotals();
    }

    private async Task SearchCustomersAsync()
    {
        CustomerHits.Clear();
        if (CustomerQuery.Trim().Length < 2) { Status = "Digite ao menos 2 letras, CPF ou telefone do cliente."; return; }
        try
        {
            foreach (var c in (await S.Api.GetAsync($"/api/v1/customers?q={Uri.EscapeDataString(CustomerQuery.Trim())}")).EnumerateArray().Take(8))
                CustomerHits.Add(new CustomerHit(c.GetProperty("id").GetGuid(), c.GetProperty("name").GetString()!,
                    (c.TryGetProperty("document", out var d) && d.ValueKind == JsonValueKind.String ? "CPF " + d.GetString() : "")
                    + (c.TryGetProperty("phone", out var ph) && ph.ValueKind == JsonValueKind.String ? "  " + ph.GetString() : "")));
            if (CustomerHits.Count == 0) Status = "Nenhum cliente encontrado. Cadastre na área Clientes.";
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException) { Status = "Busca de clientes precisa de conexão."; }
    }

    private async Task SelectCustomerAsync(CustomerHit c)
    {
        _customer = c; _pointsAvailable = 0; _centsPerPoint = 0; Redeem = "";
        CustomerHits.Clear(); CustomerQuery = "";
        if (S.Session.Modules.Contains("fidelidade"))
            try
            {
                var program = await S.Api.GetAsync("/api/v1/loyalty/program");
                var detail = await S.Api.GetAsync($"/api/v1/customers/{c.Id}");
                if (program.GetProperty("enabled").GetBoolean() && detail.GetProperty("loyalty").ValueKind == JsonValueKind.Object)
                {
                    _centsPerPoint = program.GetProperty("centsPerPoint").GetInt32();
                    _minRedeem = program.GetProperty("minPointsToRedeem").GetInt32();
                    _maxRedeemPct = program.GetProperty("maxRedeemPercent").GetInt32();
                    _pointsAvailable = detail.GetProperty("loyalty").GetProperty("available").GetInt32();
                }
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException) { /* sem pontos offline */ }
        RaiseCustomer();
    }

    private void ClearCustomer() { _customer = null; Redeem = ""; _pointsAvailable = 0; _centsPerPoint = 0; RaiseCustomer(); }

    public void SetCustomerForPreview(string name, int points)
    {
        _customer = new CustomerHit(Guid.NewGuid(), name, ""); _pointsAvailable = points; _centsPerPoint = 5; _minRedeem = 100; _maxRedeemPct = 50;
        RaiseCustomer();
    }
    public string ItemsText => Cart.Count == 0 ? "" : $"{Cart.Count} item(ns)";
    public bool CashOpen => _cashSession is not null;
    /// <summary>Estado da loja: true aberta, false fechada, null desconhecido (sem servidor e sem registro local).</summary>
    private bool? _storeOpen;
    public bool? StoreOpen => _storeOpen;
    public string StoreText => _storeOpen switch { true => "Loja aberta", false => "Loja fechada", _ => "Loja: estado desconhecido" };
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
    public RelayCommand WithdrawalCommand { get; }
    public RelayCommand SupplyCommand => RelayCommand.Sync(_ => AskSupply(), _ => CashOpen);
    public RelayCommand SyncCommand { get; }

    private AppServices S => _shell.S;

    public PosViewModel(ShellViewModel shell, bool startTimers = true)
    {
        _shell = shell;
        _method = PaymentOptions[0];
        Cart.CollectionChanged += (_, _) => { RecomputePromos(); Raise(nameof(CartEmpty)); Raise(nameof(ItemsText)); };
        SearchCustomerCommand = new RelayCommand(_ => SearchCustomersAsync());
        ClearCustomerCommand = RelayCommand.Sync(_ => ClearCustomer());
        AddCommand = RelayCommand.Sync(_ => Add());
        ClearCommand = RelayCommand.Sync(_ => { Cart.Clear(); ClearDiscount(); PaymentLines.Clear(); RaisePayments(); Coupon = ""; _quoteId = null; }, _ => Cart.Count > 0);
        RemoveCommand = RelayCommand.Sync(p =>
        {
            if (p is not CartItemVm i) return;
            Cart.Remove(i);
            if (!HasPaymentLines) return;
            // Tirou item depois de lançar parte do pagamento: confere se o que já foi lançado ainda cabe no total.
            if (Cart.Count == 0) { PaymentLines.Clear(); RaisePayments(); Status = "Carrinho vazio: os pagamentos lançados foram retirados."; }
            else if (PaymentLines.Where(l => l.Method != PaymentSplit.Cash).Sum(l => l.Cents) > TotalCents)
                Status = "Os pagamentos lançados passam do novo total. Remova um pagamento (X) e lance de novo.";
            else if (PaidLinesCents > TotalCents) Status = $"Troco a devolver: {AppServices.Money(PaidLinesCents - TotalCents)}. Finalize a venda.";
        });
        FinishCommand = RelayCommand.Sync(_ => Finish(), _ => Cart.Count > 0 && CashOpen && _shell.SaleDone is null);
        // Regras: caixa só abre com a loja aberta; loja só fecha com o caixa fechado (o servidor revalida para todos os caixas).
        OpenCashCommand = RelayCommand.Sync(_ => AskOpenCash(), _ => !CashOpen && _storeOpen != false);
        CloseCashCommand = RelayCommand.Sync(_ => AskCloseCash(), _ => CashOpen);
        OpenStoreCommand = new RelayCommand(_ => StoreEventAsync(true), _ => _storeOpen != true);
        CloseStoreCommand = new RelayCommand(_ => StoreEventAsync(false), _ => _storeOpen != false && !CashOpen);
        WithdrawalCommand = RelayCommand.Sync(_ => AskWithdrawal(), _ => CashOpen);
        SyncCommand = new RelayCommand(_ => SyncNowAsync());
        if (!startTimers) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) => await SyncNowAsync();
        _timer.Start();
        _ = LoadCashAsync();
    }

    public void Stop() => _timer?.Stop();

    /// <summary>Usado só pelas capturas de tela (modo demonstração sem servidor).</summary>
    public void SetCashForPreview(bool open) { _cashSession = open ? Guid.NewGuid() : null; _storeOpen = open ? true : _storeOpen; RaiseCash(); }

    private void RaiseCash()
    {
        Raise(nameof(CashOpen)); Raise(nameof(CashText)); Raise(nameof(CashSubText)); Raise(nameof(StoreOpen)); Raise(nameof(StoreText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void SetStoreOpen(bool? open)
    {
        _storeOpen = open;
        S.Db.Set("store.state", open switch { true => "open", false => "closed", _ => "" });
        RaiseCash();
    }

    private bool _issueNfce;
    /// <summary>Loja que emite NFC-e só a pedido: o operador marca quando o cliente quer a nota (desmarca sozinho após a venda).</summary>
    public bool IssueNfce { get => _issueNfce; set => Set(ref _issueNfce, value); }
    public bool NfceOnRequest => S.Db.Get("fiscal.mode") == "a_pedido" && S.Session.Can("fiscal.emit", "fiscal");
    public void RefreshFiscalMode() => Raise(nameof(NfceOnRequest));

    private void Search()
    {
        Suggestions.Clear();
        QuantityInput.TryParse(Code, out _, out var term, out _); // "3*cad" sugere "Caderno..." mantendo a quantidade
        if (term.Contains('=')) term = term[..term.IndexOf('=')].Trim(); // "100=9,90" sugere o produto 100
        if (term.Length >= 2)
            foreach (var p in S.Db.SearchProducts(term, 6))
                Suggestions.Add(new ProductHit(p.Id, p.Name, p.Barcode ?? p.Sku, p.PriceCents));
        Raise(nameof(HasSuggestions));
    }

    private void Add()
    {
        if (!QuantityInput.TryParse(Code, out var qty, out var code, out var qtyError)) { Status = qtyError!; return; }
        // Preço digitado: "100=9,90" (produto de preço livre, ex.: "Diversos").
        if (PriceInput.TrySplit(code, out var priceCode, out var typedPrice, out var priceError))
        {
            if (priceError is not null) { Status = priceError; return; }
            var match = S.Db.SearchProducts(priceCode, 5).FirstOrDefault(h => h.Sku == priceCode || h.Barcode == priceCode);
            if (match.Id == Guid.Empty) { Status = $"Código {priceCode} não encontrado. Cadastre um produto com esse código (ex.: \"Diversos\") e marque \"Preço livre\"."; return; }
            if (!S.IsOpenPrice(match.Id)) { Status = $"\"{match.Name}\" não aceita preço digitado. No cadastro do produto, marque \"Preço livre\"."; return; }
            AddLine(match.Id, match.Name, qty, typedPrice);
            return;
        }
        // Etiqueta de balança (EAN-13 começando com 2): produto pelo código + preço ou peso embutidos.
        // Produto com este código exato vem antes: códigos gerados pelo sistema (EAN-13 "200...") também começam com 2.
        var dev = S.Settings.Devices;
        var exactCode = code.Length > 0 && S.Db.SearchProducts(code, 3).Any(h => h.Barcode == code);
        if (!exactCode && Comercial.Desktop.Core.Hardware.ScaleBarcodeParser.TryParse(code, new(dev.ScaleBarcodeMode, dev.ScaleCodeDigits, 11 - dev.ScaleCodeDigits), out var sb))
        {
            if (S.Db.FindByPlu(sb.ProductCode) is not { } plu) { Status = $"Produto da balança (código {sb.ProductCode}) não encontrado. Confira o código interno (SKU)."; return; }
            var weight = sb.WeightGrams is { } g ? g / 1000m : plu.PriceCents > 0 ? Math.Round((decimal)sb.PriceCents!.Value / plu.PriceCents, 3) : 0;
            if (weight <= 0) { Status = "Etiqueta da balança sem peso/preço válido."; return; }
            AddLine(plu.Id, plu.Name, weight, plu.PriceCents);
            return;
        }
        if (code.Length == 0) { Status = "Digite o nome, o código ou leia o código de barras do produto."; return; }
        var hits = S.Db.SearchProducts(code, 2);
        var exact = hits.FirstOrDefault(h => h.Barcode == code || h.Sku == code);
        if (exact.Id == Guid.Empty && hits.Count == 1) exact = hits[0];
        if (exact.Id == Guid.Empty) { Status = hits.Count == 0 ? "Produto não encontrado no catálogo." : "Mais de um produto encontrado: escolha na lista."; return; }
        // Cadastrado pelo celular sem preço: não vende por R$ 0,00 sem querer.
        if (S.IsOpenPrice(exact.Id)) { Status = $"\"{exact.Name}\" tem preço livre: digite {exact.Sku}=valor, por exemplo {exact.Sku}=9,90."; return; }
        if (exact.PriceCents <= 0) { Status = $"\"{exact.Name}\" está sem preço. Defina o preço em Estoque (ou no app) antes de vender."; return; }
        // Produto vendido a peso com balança no caixa: lê o peso automaticamente (se não foi digitado multiplicador).
        if (qty == 1 && !Code.Contains('*') && S.Db.UnitOf(exact.Id) == "KG" && !string.IsNullOrWhiteSpace(dev.ScalePort))
        {
            var reading = SerialScale.Read(dev.ScalePort!, dev.ScaleBaud);
            if (!reading.Ok) { Status = "Balança: " + reading.Error; return; }
            qty = reading.Kilograms;
        }
        AddLine(exact.Id, exact.Name, qty, exact.PriceCents);
    }

    private void AddProduct(ProductHit p)
    {
        if (!QuantityInput.TryParse(Code, out var qty, out _, out var qtyError)) { Status = qtyError!; return; }
        if (S.IsOpenPrice(p.Id)) { Status = $"\"{p.Name}\" tem preço livre: digite {p.Code}=valor, por exemplo {p.Code}=9,90."; return; }
        AddLine(p.Id, p.Name, qty, p.PriceCents);
    }

    public void AddLine(Guid id, string name, decimal qty, long price)
    {
        var existing = Cart.FirstOrDefault(c => c.Line.ProductId == id && c.Line.UnitPriceCents == price);
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
        if (RedeemPoints > 0)
        {
            if (RedeemPoints > _pointsAvailable) { Status = $"O cliente tem só {_pointsAvailable} ponto(s)."; return; }
            if (RedeemPoints < _minRedeem) { Status = $"Resgate mínimo de {_minRedeem} pontos."; return; }
            if (LoyaltyDiscountCents > CartCents * _maxRedeemPct / 100) { Status = $"Pontos pagam no máximo {_maxRedeemPct}% da compra."; return; }
        }
        var total = TotalCents;
        if (!PromptViewModel.TryMoney(Paid, out var typed)) { Status = "Valor recebido inválido."; return; }
        // Pagamentos já lançados + o valor digitado agora (vazio = o que falta, na forma escolhida).
        var parts = PaymentLines.Select(p => (p.Method, p.Cents)).ToList();
        var remaining = PaymentSplit.Remaining(total, parts);
        // Valor menor que o que falta: lança como parte do pagamento e mostra quanto ainda falta (ex.: R$ 10 no
        // dinheiro de uma compra de R$ 20; o resto vai no PIX ou no cartão).
        if (typed > 0 && typed < remaining)
        {
            if (Method.Method == 5 && _customer is null) { Status = "Fiado: identifique o cliente antes de lançar."; return; }
            var method = Method;
            void AddPart()
            {
                PaymentLines.Add(new PaymentLine(method.Method, method.Name, typed));
                Paid = "";
                RaisePayments();
                Status = $"Recebido {AppServices.Money(typed)} em {method.Name}. Falta pagar {AppServices.Money(remaining - typed)}: escolha a forma e finalize.";
            }
            if (method.Method == 5) _ = CheckCreditThenAsync(PaymentLines.Where(l => l.Method == 5).Sum(l => l.Cents) + typed, AddPart);
            else AddPart();
            return;
        }
        if (typed > 0 || remaining > 0) parts.Add((Method.Method, typed > 0 ? typed : remaining));
        var check = PaymentSplit.Validate(total, parts);
        if (!check.Ok) { Status = check.Error!; return; }
        if (parts.Any(p => p.Method == 5) && _customer is null) { Status = "Fiado: identifique o cliente antes de finalizar."; return; }
        var onCredit = parts.Where(p => p.Method == 5).Sum(p => p.Cents);
        if (onCredit > 0) { _ = CheckCreditThenAsync(onCredit, () => ConfirmAndProceed(parts)); return; }
        ConfirmAndProceed(parts);
    }

    private bool _checkingCredit;

    /// <summary>
    /// Fiado (crediário): só segue se o cliente tiver limite disponível para o valor. Sem limite, ou sem internet para
    /// conferir, mostra o motivo na tela e a venda NÃO é finalizada (continua aberta para trocar a forma de pagamento).
    /// </summary>
    private async Task CheckCreditThenAsync(long amountCents, Action onAllowed)
    {
        if (_checkingCredit || _customer is null) return;
        _checkingCredit = true;
        Status = "Conferindo o limite do crediário...";
        string? refusal;
        try
        {
            var r = await S.Api.GetAsync($"/api/v1/pos/customers/{_customer.Id}/credit-check?amountCents={amountCents}");
            var available = r.GetProperty("availableCents").GetInt64();
            refusal = r.GetProperty("allowed").GetBoolean() ? null
                : !r.GetProperty("hasLimit").GetBoolean() ? $"{_customer.Name} não tem limite liberado para comprar no crediário."
                : $"Limite insuficiente: {_customer.Name} tem {AppServices.Money(available)} disponível e esta compra precisa de {AppServices.Money(amountCents)} no crediário.";
        }
        catch (ApiException ex) { refusal = "Não foi possível conferir o limite do crediário (" + ex.Code + ")."; }
        catch (HttpRequestException) { refusal = "Sem internet: não dá para conferir o limite do crediário agora."; }
        catch (TaskCanceledException) { refusal = "O servidor demorou para responder: não dá para conferir o limite do crediário agora."; }
        finally { _checkingCredit = false; }
        if (refusal is null) { Status = ""; onAllowed(); return; }
        Status = refusal + " A venda não foi finalizada.";
        _shell.ShowPrompt(new PromptViewModel
        {
            Title = "Crediário não autorizado",
            Message = refusal + "\n\nA venda NÃO foi finalizada. Receba o valor em outra forma de pagamento (dinheiro, PIX ou cartão) ou tire um item.",
            ConfirmText = "Entendi",
        }, _ => Task.FromResult(true));
    }

    private void ConfirmAndProceed(List<(int Method, long Cents)> parts)
    {
        // Cartão (e PIX sem QR automático) é passado fora do sistema: confirma antes de registrar. "Voltar" mantém a
        // venda aberta para tirar um item ou trocar a forma, quando o cartão é recusado ou o saldo não dá.
        var external = parts.Where(p => p.Method is 2 or 3 || (p.Method == 4 && !_pixDynamic)).ToList();
        if (external.Count > 0)
        {
            var lines = string.Join("\n", external.GroupBy(p => p.Method).Select(g => $"• {PaymentOptions.First(o => o.Method == g.Key).Name}: {AppServices.Money(g.Sum(x => x.Cents))}"));
            _shell.ShowPrompt(new PromptViewModel
            {
                Title = "Confirme o pagamento",
                Message = $"Passe na maquininha (ou confira o PIX):\n{lines}\n\nRecusado ou sem saldo? Toque em Voltar: a venda continua aberta para tirar um item ou mudar a forma de pagamento.",
                ConfirmText = "Aprovado — finalizar",
            }, _ => { Proceed(parts); return Task.FromResult(true); },
            () => Status = "Venda continua aberta: tire um item na lixeira ou escolha outra forma de pagamento.");
            return;
        }
        Proceed(parts);
    }

    private void Proceed(List<(int Method, long Cents)> parts)
    {
        var pixCents = parts.Where(p => p.Method == 4).Sum(p => p.Cents);
        if (_pixDynamic && pixCents > 0)
        {
            _ = StartPixAsync(parts, pixCents);
            return;
        }
        Complete(parts, null);
    }

    /// <summary>Gera a cobrança PIX na conta da loja e mostra o QR; a venda só é registrada depois da confirmação.</summary>
    private async Task StartPixAsync(List<(int Method, long Cents)> parts, long pixCents)
    {
        try
        {
            var charge = await S.Api.PostAsync("/api/v1/pos/pix-charges", new { cashSessionId = _cashSession, amountCents = pixCents });
            _shell.PixPay = new PixPayViewModel(S, charge,
                id => { _shell.PixPay?.Stop(); _shell.PixPay = null; Complete(parts, id); },
                msg => { _shell.PixPay?.Stop(); _shell.PixPay = null; Status = msg; });
        }
        catch (ApiException ex) { Status = ex.Code == "pix_not_configured" ? "PIX no caixa não está ativo." : "Não foi possível gerar o PIX: " + ex.Code; }
        catch (HttpRequestException) { Status = "Sem internet: PIX com QR automático indisponível. Use outra forma ou o PIX manual."; }
    }

    private void Complete(List<(int Method, long Cents)> parts, Guid? pixChargeId)
    {
        var total = TotalCents;
        var check = PaymentSplit.Validate(total, parts);
        var paid = check.PaidCents;
        var methodName = string.Join(" + ", parts.Select(p => PaymentOptions.First(o => o.Method == p.Method).Name).Distinct());
        try
        {
            var lines = Cart.Select(c => c.Line).ToList();
            var mode = S.Db.Get("fiscal.mode") ?? "sempre";
            var wantsNfce = mode == "sempre" || mode == "a_pedido" && IssueNfce;
            var saleKey = S.Pos!.RecordSale(S.Settings.StoreId!.Value, _cashSession!.Value, lines, parts.Select(p => new SalePaymentRequest(p.Method, p.Cents)).ToList(), SaleDiscountCents,
                _customer?.Id, CanRedeem ? RedeemPoints : 0, _discountAuthId, pixChargeId, Coupon.Length == 0 ? null : Coupon, _quoteId, mode == "a_pedido" ? IssueNfce : null);
            IssueNfce = false;
            var receipt = new Receipt(DateTime.Now, lines.Select(l => new ReceiptLine(l.Name, l.Quantity, l.UnitPriceCents, l.TotalCents)).ToList(),
                total, LoyaltyDiscountCents + SaleDiscountCents + PromoCents, paid, methodName, _customer?.Name);
            Status = "";
            Cart.Clear(); Paid = ""; ClearDiscount(); PaymentLines.Clear(); RaisePayments(); Coupon = ""; _quoteId = null;
            ClearCustomer();
            _shell.ShowSaleDone(total, paid, methodName, receipt); // só fecha quando o operador confirmar o troco
            var cashSale = parts.Any(p => p.Method == PaymentSplit.Cash);
            if (S.Session.Can("fiscal.emit", "fiscal") && !_shell.NfceOff && wantsNfce)
                _ = EmitNfceAsync(saleKey, receipt, cashSale, _shell.SaleDone);
            else
            {
                if (ReceiptPrinter.AutoPrint(receipt, cashSale) is { } printError) Status = printError;
                _ = SyncNowAsync();
            }
        }
        catch (InvalidOperationException ex) { Status = ex.Message; }
        _shell.RefreshStatus();
    }

    /// <summary>
    /// Loja que emite NFC-e: sincroniza a venda, pede a emissão na hora e imprime o DANFE. Sem resposta (internet caiu) ou nota
    /// não autorizada, sai o comprovante não fiscal e a nota segue na fila de emissão automática do servidor.
    /// </summary>
    private async Task EmitNfceAsync(string saleKey, Receipt receipt, bool cashSale, SaleDoneViewModel? done)
    {
        if (cashSale) ReceiptPrinter.OpenDrawer(); // gaveta abre na hora; a impressão espera a SEFAZ
        if (done is not null) done.FiscalText = "Emitindo NFC-e…";
        void Fallback(string text)
        {
            if (done is not null) done.FiscalText = text;
            if (ReceiptPrinter.AutoPrint(receipt, false) is { } err) Status = err;
        }
        try
        {
            await SyncNowAsync();
            var r = await S.Api.PostAsync($"/api/v1/fiscal/nfce/sale/{Uri.EscapeDataString(saleKey)}", new { });
            var d = r.Deserialize<Comercial.Desktop.Core.Hardware.NfceDanfe>(ApiClient.Json)!;
            if (d.Status == "sem_nfce") { _shell.NfceOff = true; Fallback(""); return; }
            if (d.Printable)
            {
                done?.SetDanfe(d);
                if (done is not null) done.FiscalText = $"NFC-e {d.Number} autorizada" + (d.Homologation ? " (homologação, sem valor fiscal)" : "");
                if (ReceiptPrinter.AutoPrintDanfe(d) is { } err) Status = err;
                return;
            }
            Fallback(d.Status switch
            {
                "rejeitada" => "NFC-e rejeitada: " + d.Message + " — corrija em Fiscal → Documentos. Saiu o comprovante não fiscal.",
                "denegada" => "NFC-e denegada pela SEFAZ: " + d.Message,
                _ => "NFC-e ainda não autorizada (" + d.Message + "). Ela será emitida automaticamente; saiu o comprovante não fiscal.",
            });
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            Fallback("Sem conexão com o servidor: a NFC-e será emitida automaticamente quando a internet voltar. Saiu o comprovante não fiscal.");
        }
    }

    private async Task StoreEventAsync(bool open)
    {
        if (!open && CashOpen) { Status = "Feche o caixa antes de fechar a loja."; return; }
        // Online: registra direto para o servidor validar na hora (ex.: outro caixa ainda aberto).
        // Sem conexão: vai para a fila e sincroniza depois.
        try
        {
            await S.Api.PostAsync("/api/v1/operating/events", new OperatingEventRequest("op-" + Guid.NewGuid().ToString("N"), S.Settings.StoreId!.Value,
                open ? 1 : 2, DateTimeOffset.Now, false, S.Settings.DeviceId));
            SetStoreOpen(open);
            Status = open ? "Loja aberta. Agora você pode abrir o caixa." : "Loja fechada.";
        }
        catch (ApiException ex)
        {
            Status = ex.Code switch
            {
                "cash_open" => "Ainda há caixa aberto nesta loja (de outro operador). Feche todos os caixas antes de fechar a loja.",
                _ => "Não foi possível registrar: " + ex.Code,
            };
        }
        catch (HttpRequestException)
        {
            S.Pos!.RecordStoreEvent(S.Settings.StoreId!.Value, open);
            SetStoreOpen(open);
            Status = (open ? "Abertura" : "Fechamento") + " da loja registrado sem internet; será enviado ao reconectar.";
        }
    }

    private async Task LoadStoreAsync()
    {
        try
        {
            var st = await S.Api.GetAsync($"/api/v1/operating/status?storeId={S.Settings.StoreId}");
            SetStoreOpen(st.GetProperty("state").GetString() == "open");
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            // Sem conexão: usa o último estado conhecido neste terminal. Sem o módulo de horários: sem regra de loja.
            _storeOpen = ex is ApiException { Code: "module_not_enabled" } ? null
                : S.Db.Get("store.state") switch { "open" => true, "closed" => false, _ => null };
            RaiseCash();
        }
    }

    private bool _pixDynamic;

    private async Task LoadCashAsync()
    {
        await LoadStoreAsync();
        try { _pixDynamic = (await S.Api.GetAsync("/api/v1/payments/settings")).GetProperty("pixEnabled").GetBoolean(); }
        catch (Exception ex) when (ex is ApiException or HttpRequestException) { _pixDynamic = false; }
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
        catch (ApiException ex)
        {
            p.Error = ex.Code switch
            {
                "cash_already_open" => "Já existe um caixa aberto para você.",
                "store_closed" => "A loja está fechada: clique em Abrir loja antes de abrir o caixa.",
                _ => "Não foi possível abrir: " + ex.Code,
            };
            if (ex.Code == "store_closed") SetStoreOpen(false);
        }
        catch (HttpRequestException) { p.Error = "Abrir o caixa exige conexão com o servidor."; }
        return false;
    });

    private void AskSupply() => _shell.ShowPrompt(new PromptViewModel
    {
        Title = "Suprimento (colocar dinheiro no caixa)",
        Message = "Use quando colocar dinheiro na gaveta durante o expediente (ex.: mais troco). Fica registrado no caixa e na auditoria.",
        ValueLabel = "Valor colocado (R$)", TextLabel = "Motivo (ex.: reforço de troco)", ConfirmText = "Registrar suprimento",
    }, async p =>
    {
        if (!PromptViewModel.TryMoney(p.Value, out var cents) || cents <= 0) { p.Error = "Informe um valor maior que zero."; return false; }
        if (p.Text.Trim().Length < 3) { p.Error = "Informe o motivo."; return false; }
        try
        {
            await S.Api.PostAsync($"/api/v1/cash/sessions/{_cashSession}/supply", new { amountCents = cents, reason = p.Text.Trim() });
            Status = $"Suprimento de {AppServices.Money(cents)} registrado.";
            ReceiptPrinter.OpenDrawer();
            return true;
        }
        catch (ApiException ex) { p.Error = "Falha no suprimento: " + ex.Code; }
        catch (HttpRequestException) { p.Error = "O suprimento exige conexão com o servidor."; }
        return false;
    });

    private void AskWithdrawal() => _shell.ShowPrompt(new PromptViewModel
    {
        Title = "Sangria (retirada do caixa)",
        Message = "Retire o dinheiro da gaveta e informe o valor. O proprietário recebe um aviso com o valor e o motivo.",
        ValueLabel = "Valor retirado (R$)", TextLabel = "Motivo (ex.: levar ao cofre, depósito no banco)", ConfirmText = "Registrar sangria",
    }, async p =>
    {
        if (!PromptViewModel.TryMoney(p.Value, out var cents) || cents <= 0) { p.Error = "Informe um valor maior que zero."; return false; }
        if (p.Text.Trim().Length < 3) { p.Error = "Informe o motivo da sangria."; return false; }
        try
        {
            await S.Api.PostAsync($"/api/v1/cash/sessions/{_cashSession}/withdrawal", new { amountCents = cents, reason = p.Text.Trim() });
            Status = $"Sangria de {AppServices.Money(cents)} registrada. O proprietário foi avisado.";
            ReceiptPrinter.OpenDrawer();
            return true;
        }
        catch (ApiException ex)
        {
            p.Error = ex.Code switch
            {
                "insufficient_cash" => "Valor maior que o dinheiro esperado na gaveta.",
                "cash_not_open" => "O caixa não está aberto.",
                _ => "Falha na sangria: " + ex.Code,
            };
        }
        catch (HttpRequestException) { p.Error = "A sangria exige conexão com o servidor (para avisar o proprietário)."; }
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
            RefreshFiscalMode();
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

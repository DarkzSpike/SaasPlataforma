using System.Net.Http;
using Comercial.Desktop.Core;

namespace Comercial.Desktop;

/// <summary>Conta de recebimento da loja (Mercado Pago dela) para o PIX com QR Code no caixa.</summary>
public sealed class StorePaymentsViewModel(ShellViewModel shell) : OwnerPage(shell, "Recebimentos", "PIX com QR Code no caixa, direto na conta Mercado Pago da sua loja")
{
    private bool _configured, _enabled, _test;
    private string _account = "", _info = "";
    public bool Configured { get => _configured; set => Set(ref _configured, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string StatusText => !Configured ? "Conta Mercado Pago não conectada." : Enabled ? $"PIX no caixa ATIVO • conta {_account}{(_test ? " (modo TESTE)" : "")}" : $"Conta {_account} conectada, PIX no caixa desligado.";
    public string Info { get => _info; set { if (Set(ref _info, value)) Raise(nameof(HasInfo)); } }
    public bool HasInfo => Info.Length > 0;

    public RelayCommand ConnectCommand => RelayCommand.Sync(_ => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Conectar conta Mercado Pago da loja",
        Message = "Cole o Access Token da conta Mercado Pago da LOJA (Suas integrações → Credenciais). O dinheiro do PIX cai direto nessa conta. Ele fica guardado criptografado.",
        PasswordLabel = "Access Token (APP_USR-... ou TEST-...)", ConfirmText = "Conectar e ativar PIX",
    }, async p =>
    {
        try
        {
            await S.Api.PutAsync("/api/v1/payments/settings", new { mpAccessToken = p.Password.Trim(), pixEnabled = true });
            await LoadAsync();
            Info = "Conta conectada e PIX no caixa ativado. Na próxima venda em PIX o QR Code aparece na tela.";
            return true;
        }
        catch (ApiException ex) { p.Error = ex.Code == "invalid_token" ? "Credencial recusada pelo Mercado Pago. Confira se copiou o Access Token inteiro." : "Falha: " + ex.Code; }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        finally { p.Password = ""; }
        return false;
    }));

    public RelayCommand ToggleCommand => new(async _ =>
    {
        Error = ""; Info = "";
        try { await S.Api.PutAsync("/api/v1/payments/settings", new { pixEnabled = !Enabled }); await LoadAsync(); Info = Enabled ? "PIX no caixa ativado." : "PIX no caixa desligado (o PIX volta a ser lançado manualmente)."; }
        catch (ApiException ex) { Error = "Falha: " + ex.Code; }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
    });

    protected override async Task LoadCoreAsync()
    {
        var s = await S.Api.GetAsync("/api/v1/payments/settings");
        Configured = s.GetProperty("configured").GetBoolean();
        Enabled = s.GetProperty("pixEnabled").GetBoolean();
        _test = s.GetProperty("isTestToken").GetBoolean();
        _account = s.TryGetProperty("accountLabel", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String ? a.GetString()! : "";
        Raise(nameof(StatusText));
    }
}

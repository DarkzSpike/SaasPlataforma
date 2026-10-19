using System.Net.Http;
using System.Windows.Media.Imaging;
using Comercial.Desktop.Core;
using QRCoder;

namespace Comercial.Desktop;

/// <summary>Minha conta: verificação em duas etapas (Google Authenticator, Microsoft Authenticator...).</summary>
public sealed class AccountViewModel(ShellViewModel shell) : OwnerPage(shell, "Minha conta", "Segurança do seu acesso")
{
    private bool _enabled, _settingUp;
    private string _secret = "", _code = "", _info = "";
    private BitmapImage? _qr;
    public string UserEmail => Shell.UserLabel;
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) { Raise(nameof(StatusText)); Raise(nameof(Disabled)); Raise(nameof(CanStart)); } } }
    public bool Disabled => !Enabled;
    public bool SettingUp { get => _settingUp; set { if (Set(ref _settingUp, value)) Raise(nameof(CanStart)); } }
    public bool CanStart => !Enabled && !SettingUp;
    public string StatusText => Enabled ? "Verificação em duas etapas LIGADA" : "Verificação em duas etapas desligada";
    /// <summary>Chave em grupos de 4 para digitar no aplicativo quando não der para ler o QR Code.</summary>
    public string Secret { get => _secret; set => Set(ref _secret, value); }
    public string Code { get => _code; set => Set(ref _code, value); }
    public BitmapImage? Qr { get => _qr; set => Set(ref _qr, value); }
    public string Info { get => _info; set { if (Set(ref _info, value)) Raise(nameof(HasInfo)); } }
    public bool HasInfo => Info.Length > 0;

    protected override async Task LoadCoreAsync() => Enabled = (await S.Api.GetAsync("/api/v1/me/2fa")).GetProperty("enabled").GetBoolean();

    public RelayCommand SetupCommand => new(async _ => await Run(async () =>
    {
        var r = await S.Api.PostAsync("/api/v1/me/2fa/setup", new { });
        var secret = r.GetProperty("secret").GetString()!;
        Secret = string.Join(" ", Enumerable.Range(0, (secret.Length + 3) / 4).Select(i => secret.Substring(i * 4, Math.Min(4, secret.Length - i * 4))));
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(r.GetProperty("otpauth").GetString()!, QRCodeGenerator.ECCLevel.M);
        Qr = FormViewModel.Load(new PngByteQRCode(data).GetGraphic(8));
        SettingUp = true; Code = "";
        return "Abra o aplicativo autenticador no celular, leia o QR Code (ou digite a chave) e confirme com o código de 6 dígitos.";
    }));

    public RelayCommand EnableCommand => new(async _ => await Run(async () =>
    {
        await S.Api.PostAsync("/api/v1/me/2fa/enable", new { code = Code.Trim() });
        SettingUp = false; Secret = ""; Qr = null; Code = ""; Enabled = true;
        return "Pronto! A partir de agora o login pede o código do celular. Guarde bem o celular: se perder, o dono/gerente pode remover a verificação do seu usuário.";
    }));

    public RelayCommand DisableCommand => new(async _ => await Run(async () =>
    {
        await S.Api.PostAsync("/api/v1/me/2fa/disable", new { code = Code.Trim() });
        Code = ""; Enabled = false;
        return "Verificação em duas etapas desligada.";
    }));

    private async Task Run(Func<Task<string>> action)
    {
        Error = ""; Info = "";
        try { Info = await action(); }
        catch (ApiException ex)
        {
            Error = ex.Code switch
            {
                "invalid_code" => "Código incorreto. Confira se o relógio do celular está certo e tente o código atual.",
                "already_enabled" => "Já está ligada.",
                "setup_required" => "Clique em Ativar primeiro.",
                _ => "Falha: " + ex.Code,
            };
        }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
    }
}

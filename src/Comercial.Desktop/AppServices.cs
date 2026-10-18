using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Comercial.Desktop.Core;
using Comercial.Shared.Branding;

namespace Comercial.Desktop;

/// <summary>Configuração do terminal (desktop.json ao lado do executável; dados em %LOCALAPPDATA%).</summary>
public sealed class DesktopSettings
{
    public string ApiBaseUrl { get; set; } = "https://localhost:5001";
    /// <summary>Chave pública de licença fixada na instalação. Em branco: obtida do servidor (apenas desenvolvimento).</summary>
    public string? LicensePublicKeyPem { get; set; }
    public Guid? DeviceId { get; set; }
    public string? TenantCode { get; set; }
    public Guid? StoreId { get; set; }

    public static string DataDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComercialPDV");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string FilePath => Path.Combine(DataDir, "desktop.json");

    public static DesktopSettings Load()
    {
        var installed = Path.Combine(AppContext.BaseDirectory, "desktop.json");
        foreach (var p in new[] { FilePath, installed })
            if (File.Exists(p)) return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(p)) ?? new();
        return new();
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

public sealed class AppServices
{
    public DesktopSettings Settings { get; } = DesktopSettings.Load();
    public LocalDatabase Db { get; } = new(Path.Combine(DesktopSettings.DataDir, "local.db"));
    public ApiClient Api { get; }
    public LicenseGuard? License { get; set; }
    public PosEngine? Pos { get; set; }
    public SyncAgent? Sync { get; set; }
    public string TokenPath => Path.Combine(DesktopSettings.DataDir, "session.bin");

    public AppServices()
    {
        Api = new ApiClient(new HttpClient { BaseAddress = new Uri(Settings.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(20) });
    }

    /// <summary>Aplica a identidade visual publicada (ou a última válida em cache, se offline).</summary>
    public void ApplyBranding(BrandingConfig cfg)
    {
        if (BrandingRules.Validate(cfg).Count > 0) cfg = new BrandingConfig();
        var r = Application.Current.Resources;
        Brush B(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        r["PrimaryBrush"] = B(cfg.PrimaryColor);
        r["SecondaryBrush"] = B(cfg.SecondaryColor);
        r["AccentBrush"] = B(cfg.AccentColor);
        r["BackgroundBrush"] = B(cfg.BackgroundColor);
        r["SurfaceBrush"] = B(cfg.SurfaceColor);
        r["TextBrush"] = B(cfg.TextColor);
        r["BorderBrush"] = B(cfg.BorderColor);
        r["AppFont"] = new FontFamily(cfg.FontFamily + ", Segoe UI");
        r["BaseFontSize"] = (double)(14m * cfg.FontScale);
        r["ButtonRadius"] = new CornerRadius(cfg.ButtonStyle switch { "square" => 0, "pill" => 18, _ => 6 });
        r["SystemName"] = cfg.SystemName;
    }
}

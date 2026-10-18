using System.Globalization;
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

/// <summary>Quem está logado e o que a empresa contratou (vindo de /api/v1/me). Só controla a interface: o servidor revalida tudo.</summary>
public sealed class SessionInfo
{
    public string UserName { get; init; } = "";
    public string TenantName { get; init; } = "";
    public HashSet<string> Permissions { get; init; } = [];
    public HashSet<string> Modules { get; init; } = [];
    public string License { get; init; } = "";

    public bool Can(string permission, string? module = null) => Permissions.Contains(permission) && (module is null || Modules.Contains(module));

    public static SessionInfo From(JsonElement me) => new()
    {
        TenantName = me.GetProperty("tenant").GetProperty("tradeName").GetString() ?? "",
        Permissions = me.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()!).ToHashSet(),
        Modules = me.GetProperty("modules").EnumerateArray().Select(x => x.GetString()!).ToHashSet(),
        License = me.GetProperty("license").GetString() ?? "",
    };
}

public sealed class AppServices
{
    public static readonly CultureInfo PtBr = new("pt-BR");
    public DesktopSettings Settings { get; } = DesktopSettings.Load();
    public LocalDatabase Db { get; }
    public ApiClient Api { get; }
    public LicenseGuard? License { get; set; }
    public PosEngine? Pos { get; set; }
    public SyncAgent? Sync { get; set; }
    public SessionInfo Session { get; set; } = new();
    public bool Online { get; set; }
    public string TokenPath => Path.Combine(DesktopSettings.DataDir, "session.bin");

    public AppServices(string? dbPath = null)
    {
        Db = new LocalDatabase(dbPath ?? Path.Combine(DesktopSettings.DataDir, "local.db"));
        Api = new ApiClient(new HttpClient { BaseAddress = new Uri(Settings.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(20) });
    }

    public static string Money(long cents) => (cents / 100m).ToString("C", PtBr);

    /// <summary>
    /// Aplica a identidade visual publicada (ou a última válida em cache, se offline). Cores derivadas
    /// (tons claros/escuros) são calculadas aqui para manter os contrastes do tema.
    /// </summary>
    public static void ApplyBranding(BrandingConfig cfg)
    {
        if (BrandingRules.Validate(cfg).Count > 0) cfg = new BrandingConfig();
        var r = Application.Current.Resources;
        Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        var primary = C(cfg.PrimaryColor);
        var accent = C(cfg.AccentColor);
        var darkBg = C(cfg.DarkBackgroundColor);
        var darkSurface = C(cfg.DarkSurfaceColor);
        r["PrimaryColor"] = primary;
        r["PrimaryBrush"] = B(primary);
        r["PrimaryLightBrush"] = B(Mix(primary, Colors.White, 0.35));
        r["PrimarySoftBrush"] = B(Mix(primary, Colors.White, 0.85));
        r["PrimaryDarkColor"] = Mix(primary, Colors.Black, 0.45);
        r["PrimaryDeepColor"] = Mix(primary, Colors.Black, 0.72);
        Brush G(Point end, params (Color C, double O)[] stops)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = end };
            foreach (var (c, o) in stops) g.GradientStops.Add(new GradientStop(c, o));
            g.Freeze();
            return g;
        }
        r["PrimaryGradientBrush"] = G(new Point(1, 1), (Mix(primary, Colors.White, 0.12), 0), (Mix(primary, Colors.Black, 0.18), 1));
        r["LoginGradientBrush"] = G(new Point(1, 1), (Mix(primary, Colors.Black, 0.55), 0), (Mix(primary, Colors.Black, 0.78), 0.6), (Mix(primary, Colors.Black, 0.45), 1));
        r["TotalGradientBrush"] = G(new Point(1, 1), (Mix(primary, Colors.Black, 0.35), 0), (Mix(Mix(primary, darkBg, 0.6), Colors.Black, 0.2), 1));
        r["HeaderGradientBrush"] = G(new Point(1, 0), (darkSurface, 0), (Mix(darkSurface, accent, 0.12), 1));
        r["AccentBrush"] = B(accent);
        r["AccentColor"] = accent;
        r["SecondaryBrush"] = B(C(cfg.SecondaryColor));
        // Tema claro (login)
        r["LightBgBrush"] = B(C(cfg.BackgroundColor));
        r["CardBrush"] = B(C(cfg.SurfaceColor));
        r["LightTextBrush"] = B(C(cfg.TextColor));
        r["LightMutedBrush"] = B(Mix(C(cfg.TextColor), C(cfg.SurfaceColor), 0.4));
        r["LightBorderBrush"] = B(C(cfg.BorderColor));
        r["LightInputBrush"] = B(Mix(C(cfg.SurfaceColor), C(cfg.BorderColor), 0.25));
        // Tema escuro (operação)
        r["BgBrush"] = B(darkBg);
        r["BgColor"] = darkBg;
        r["SurfaceBrush"] = B(darkSurface);
        r["Surface2Brush"] = B(Mix(darkSurface, Colors.White, 0.06));
        r["Surface3Brush"] = B(Mix(darkSurface, Colors.White, 0.12));
        r["DarkBorderBrush"] = B(Mix(darkSurface, Colors.White, 0.10));
        r["TextBrush"] = B(C(cfg.DarkTextColor));
        r["MutedBrush"] = B(Mix(C(cfg.DarkTextColor), darkSurface, 0.38));
        r["AppFont"] = new FontFamily(cfg.FontFamily + ", Segoe UI");
        r["BaseFontSize"] = (double)(14m * cfg.FontScale);
        r["ButtonRadius"] = new CornerRadius(cfg.ButtonStyle switch { "square" => 0, "pill" => 22, _ => 8 });
        var name = string.IsNullOrWhiteSpace(cfg.SystemName) ? "Gestão Comercial" : cfg.SystemName.Trim();
        var cut = name.LastIndexOf(' ');
        r["SystemName"] = name;
        r["BrandFirst"] = cut > 0 ? name[..cut] + " " : name;
        r["BrandLast"] = cut > 0 ? name[(cut + 1)..] : "";
        r["WelcomeMessage"] = string.IsNullOrWhiteSpace(cfg.WelcomeMessage) ? "Bem-vindo!" : cfg.WelcomeMessage;
    }
}

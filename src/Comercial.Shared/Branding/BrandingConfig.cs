using System.Globalization;
using System.Text.RegularExpressions;

namespace Comercial.Shared.Branding;

/// <summary>
/// Identidade visual estruturada. Apenas opções aprovadas — não há campo para HTML, CSS ou
/// JavaScript arbitrário. Assets são referenciados por Id (validados no servidor).
/// </summary>
public sealed class BrandingConfig
{
    public string Theme { get; set; } = "moderno";
    public string SystemName { get; set; } = "Gestão Comercial";
    public string CompanyName { get; set; } = "";
    public string WelcomeMessage { get; set; } = "Bem-vindo";
    public string ReportHeader { get; set; } = "";
    public Guid? LogoAssetId { get; set; }
    public Guid? LogoDarkAssetId { get; set; }
    public Guid? IconAssetId { get; set; }
    public Guid? SplashAssetId { get; set; }
    public string PrimaryColor { get; set; } = "#1F5EFF";
    public string SecondaryColor { get; set; } = "#0F2A5C";
    public string AccentColor { get; set; } = "#FFB020";
    public string BackgroundColor { get; set; } = "#F6F8FB";
    public string SurfaceColor { get; set; } = "#FFFFFF";
    public string TextColor { get; set; } = "#14181F";
    public string BorderColor { get; set; } = "#D9DEE7";
    public string DarkBackgroundColor { get; set; } = "#0E1116";
    public string DarkSurfaceColor { get; set; } = "#171B22";
    public string DarkTextColor { get; set; } = "#E8ECF2";
    public string ButtonStyle { get; set; } = "rounded";
    public string CardStyle { get; set; } = "elevated";
    public string Density { get; set; } = "normal";
    public string FontFamily { get; set; } = "Inter";
    public decimal FontScale { get; set; } = 1.0m;
    public string DefaultMode { get; set; } = "system";

    public BrandingConfig Clone() => (BrandingConfig)MemberwiseClone();
}

public static partial class BrandingRules
{
    public static readonly string[] Themes = ["moderno", "minimalista", "elegante", "vibrante", "classico", "alto_contraste"];
    public static readonly string[] ApprovedFonts = ["Inter", "Roboto", "Open Sans", "Lato", "Source Sans 3", "Segoe UI"];
    public static readonly string[] ButtonStyles = ["rounded", "square", "pill"];
    public static readonly string[] CardStyles = ["flat", "elevated", "outlined"];
    public static readonly string[] Densities = ["compact", "normal", "comfortable"];
    public static readonly string[] Modes = ["light", "dark", "system"];

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"[<>{}\\]|javascript:|url\(", RegexOptions.IgnoreCase)]
    private static partial Regex Dangerous();

    public static BrandingConfig Preset(string theme)
    {
        var c = new BrandingConfig { Theme = theme };
        switch (theme)
        {
            case "minimalista":
                (c.PrimaryColor, c.SecondaryColor, c.AccentColor, c.BackgroundColor, c.ButtonStyle, c.CardStyle) = ("#222222", "#555555", "#888888", "#FFFFFF", "square", "flat"); break;
            case "elegante":
                (c.PrimaryColor, c.SecondaryColor, c.AccentColor, c.BackgroundColor, c.FontFamily) = ("#3B2F4A", "#6B5B7B", "#B8925A", "#FAF7F2", "Lato"); break;
            case "vibrante":
                (c.PrimaryColor, c.SecondaryColor, c.AccentColor, c.ButtonStyle) = ("#D6246E", "#6A1B9A", "#00A884", "pill"); break;
            case "classico":
                (c.PrimaryColor, c.SecondaryColor, c.AccentColor, c.CardStyle, c.FontFamily) = ("#1D4E89", "#2E2E2E", "#C0392B", "outlined", "Roboto"); break;
            case "alto_contraste":
                (c.PrimaryColor, c.SecondaryColor, c.AccentColor, c.BackgroundColor, c.SurfaceColor, c.TextColor, c.BorderColor) =
                    ("#000000", "#000000", "#0047AB", "#FFFFFF", "#FFFFFF", "#000000", "#000000");
                c.FontScale = 1.15m; break;
        }
        return c;
    }

    /// <summary>Valida e retorna lista de erros (vazia = válida).</summary>
    public static List<string> Validate(BrandingConfig c)
    {
        var e = new List<string>();
        void Text(string field, string? v, int max)
        {
            if (v is null) { e.Add($"{field}: obrigatório"); return; }
            if (v.Length > max) e.Add($"{field}: máximo {max} caracteres");
            if (Dangerous().IsMatch(v)) e.Add($"{field}: contém caracteres não permitidos");
        }
        void Color(string field, string? v)
        {
            if (v is null || !HexColor().IsMatch(v)) e.Add($"{field}: cor inválida (use #RRGGBB)");
        }
        void OneOf(string field, string? v, string[] allowed)
        {
            if (v is null || !allowed.Contains(v)) e.Add($"{field}: valor não permitido");
        }

        Text(nameof(c.SystemName), c.SystemName, 60);
        Text(nameof(c.CompanyName), c.CompanyName, 120);
        Text(nameof(c.WelcomeMessage), c.WelcomeMessage, 200);
        Text(nameof(c.ReportHeader), c.ReportHeader, 200);
        Color(nameof(c.PrimaryColor), c.PrimaryColor);
        Color(nameof(c.SecondaryColor), c.SecondaryColor);
        Color(nameof(c.AccentColor), c.AccentColor);
        Color(nameof(c.BackgroundColor), c.BackgroundColor);
        Color(nameof(c.SurfaceColor), c.SurfaceColor);
        Color(nameof(c.TextColor), c.TextColor);
        Color(nameof(c.BorderColor), c.BorderColor);
        Color(nameof(c.DarkBackgroundColor), c.DarkBackgroundColor);
        Color(nameof(c.DarkSurfaceColor), c.DarkSurfaceColor);
        Color(nameof(c.DarkTextColor), c.DarkTextColor);
        OneOf(nameof(c.Theme), c.Theme, Themes);
        OneOf(nameof(c.FontFamily), c.FontFamily, ApprovedFonts);
        OneOf(nameof(c.ButtonStyle), c.ButtonStyle, ButtonStyles);
        OneOf(nameof(c.CardStyle), c.CardStyle, CardStyles);
        OneOf(nameof(c.Density), c.Density, Densities);
        OneOf(nameof(c.DefaultMode), c.DefaultMode, Modes);
        if (c.FontScale < 0.85m || c.FontScale > 1.3m) e.Add("FontScale: deve estar entre 0.85 e 1.3");

        if (e.Count == 0)
        {
            if (ContrastRatio(c.TextColor, c.BackgroundColor) < 4.5) e.Add("Contraste insuficiente entre texto e fundo (tema claro, mínimo 4.5:1)");
            if (ContrastRatio(c.TextColor, c.SurfaceColor) < 4.5) e.Add("Contraste insuficiente entre texto e superfície (tema claro)");
            if (ContrastRatio(c.DarkTextColor, c.DarkBackgroundColor) < 4.5) e.Add("Contraste insuficiente no tema escuro");
        }
        return e;
    }

    public static double ContrastRatio(string a, string b)
    {
        double L(string hex)
        {
            double Ch(int i)
            {
                var v = int.Parse(hex.AsSpan(1 + i * 2, 2), NumberStyles.HexNumber) / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Ch(0) + 0.7152 * Ch(1) + 0.0722 * Ch(2);
        }
        var (l1, l2) = (L(a), L(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}

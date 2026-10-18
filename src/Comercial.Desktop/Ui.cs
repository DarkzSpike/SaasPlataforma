using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Comercial.Desktop;

/// <summary>Propriedades anexadas usadas pelos templates: ícone (glifo) e texto de ajuda dos campos.</summary>
public static class Ui
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(Ui), new PropertyMetadata(""));
    public static string GetIcon(DependencyObject o) => (string)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, string v) => o.SetValue(IconProperty, v);

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Ui), new PropertyMetadata(""));
    public static string GetPlaceholder(DependencyObject o) => (string)o.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject o, string v) => o.SetValue(PlaceholderProperty, v);
}

/// <summary>bool → Visible/Collapsed. Parâmetro "not" inverte.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var b = value switch { bool x => x, int n => n > 0, string s => s.Length > 0, null => false, _ => true };
        if (p as string == "not") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Tom do indicador (primary/accent/warn/danger/neutral) → recurso de cor.</summary>
public sealed class ToneToBrush : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c) => (value as string) switch
    {
        "primary" => Application.Current.Resources["PrimaryBrush"],
        "accent" => Application.Current.Resources["AccentBrush"],
        "warn" => Application.Current.Resources["WarnBrush"],
        "danger" => Application.Current.Resources["DangerBrush"],
        _ => Application.Current.Resources["MutedBrush"],
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

using System.Globalization;
using System.Text;

namespace Comercial.Desktop.Core.Hardware;

public sealed record LabelItem(string Name, long PriceCents, string? Barcode, string Unit = "UN");

/// <summary>
/// Etiquetas de gôndola/preço em ZPL (impressoras Zebra, Elgin L42, Argox em modo ZPL).
/// Tamanho padrão 50 x 30 mm a 203 dpi (8 pontos/mm).
/// </summary>
public static class LabelZpl
{
    private static string Esc(string s) => new(s.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
        .Select(c => c is '^' or '~' or '\\' ? ' ' : c).ToArray()); // ZPL básico: sem acentos e sem caracteres de comando

    public static string Build(IEnumerable<LabelItem> items, int widthMm = 50, int heightMm = 30, int copies = 1)
    {
        var sb = new StringBuilder();
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        foreach (var i in items)
        {
            var name = Esc(i.Name);
            if (name.Length > 28) name = name[..28];
            sb.Append("^XA^CI28")
              .Append($"^PW{widthMm * 8}^LL{heightMm * 8}")
              .Append($"^FO16,12^A0N,24,22^FD{name}^FS")
              .Append($"^FO16,44^A0N,56,52^FDR$ {(i.PriceCents / 100m).ToString("N2", pt)}^FS")
              .Append(i.Unit is "KG" ? "^FO250,64^A0N,24,22^FD/kg^FS" : "");
            if (!string.IsNullOrWhiteSpace(i.Barcode) && i.Barcode.All(char.IsAsciiDigit))
                sb.Append(i.Barcode.Length == 13 ? $"^FO16,110^BY2^BEN,90,Y,N^FD{i.Barcode[..12]}^FS" : $"^FO16,110^BY2^BCN,90,Y,N,N^FD{i.Barcode}^FS");
            sb.Append($"^PQ{Math.Clamp(copies, 1, 999)}^XZ\n");
        }
        return sb.ToString();
    }
}

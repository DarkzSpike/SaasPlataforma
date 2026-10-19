using System.Globalization;
using System.Text;

namespace Comercial.Desktop.Core.Hardware;

/// <summary>Como a balança etiquetadora monta o código EAN-13 que começa com 2 (configurável por loja).</summary>
public enum ScaleBarcodeMode { Price = 1, Weight = 2 }

public sealed record ScaleBarcodeConfig(ScaleBarcodeMode Mode = ScaleBarcodeMode.Price, int CodeDigits = 4, int ValueDigits = 7)
{
    public static readonly ScaleBarcodeConfig Default = new();
}

public sealed record ScaleBarcode(string ProductCode, long? PriceCents, int? WeightGrams);

/// <summary>
/// Etiqueta de balança (padrão das balanças Toledo, Filizola, Urano): "2" + código do produto + preço OU peso + dígito verificador.
/// Ex. (preço, 4+7): 2 0001 0001234 x → produto 0001, R$ 12,34. Código + valor somam sempre 11 dígitos.
/// </summary>
public static class ScaleBarcodeParser
{
    public static bool IsEan13Valid(string code)
    {
        if (code.Length != 13 || !code.All(char.IsAsciiDigit)) return false;
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (code[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10 == code[12] - '0';
    }

    public static bool TryParse(string? code, ScaleBarcodeConfig cfg, out ScaleBarcode result)
    {
        result = new("", null, null);
        code = (code ?? "").Trim();
        if (code.Length != 13 || code[0] != '2' || !IsEan13Valid(code)) return false;
        if (cfg.CodeDigits + cfg.ValueDigits != 11) return false;
        var product = code.Substring(1, cfg.CodeDigits);
        var value = long.Parse(code.Substring(1 + cfg.CodeDigits, cfg.ValueDigits), CultureInfo.InvariantCulture);
        result = cfg.Mode == ScaleBarcodeMode.Price ? new(product, value, null) : new(product, null, (int)value);
        return true;
    }
}

/// <summary>
/// Protocolo de balança serial de checkout (Toledo Prix / Filizola / Urano compatíveis): o PDV envia ENQ (0x05) e a balança
/// responde STX + peso em gramas (5 a 6 dígitos ASCII) + ETX. "IIIII" = instável; "NNNNN" = sobrecarga; "SSSSS" = abaixo de zero.
/// </summary>
public static class SerialScaleProtocol
{
    public const byte Enq = 0x05, Stx = 0x02, Etx = 0x03;

    public sealed record Reading(bool Ok, decimal Kilograms, string? Error);

    public static Reading Parse(byte[] frame)
    {
        var s = Encoding.ASCII.GetString(frame);
        var start = s.IndexOf((char)Stx);
        var end = s.IndexOf((char)Etx, start + 1);
        if (start < 0 || end < 0) return new(false, 0, "Resposta incompleta da balança.");
        var body = s.Substring(start + 1, end - start - 1).Trim();
        if (body.All(c => c == 'I')) return new(false, 0, "Peso instável: aguarde estabilizar.");
        if (body.All(c => c == 'N')) return new(false, 0, "Sobrecarga na balança.");
        if (body.All(c => c == 'S')) return new(false, 0, "Peso negativo: zere a balança.");
        if (!body.All(char.IsAsciiDigit) || body.Length is < 5 or > 6) return new(false, 0, "Formato de peso desconhecido.");
        var grams = int.Parse(body, CultureInfo.InvariantCulture);
        if (grams == 0) return new(false, 0, "Coloque o produto na balança.");
        return new(true, grams / 1000m, null);
    }
}

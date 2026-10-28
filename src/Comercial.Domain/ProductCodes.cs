using System.Security.Cryptography;

namespace Comercial.Domain;

/// <summary>
/// Código de barras interno para produtos sem código do fabricante: EAN-13 começando com "200" (faixa de uso
/// interno da loja, que não colide com códigos de fabricantes). Lido normalmente por qualquer leitor.
/// </summary>
public static class ProductCodes
{
    public const string InternalPrefix = "200";

    public static string NewInternalEan13()
    {
        var body = InternalPrefix + RandomNumberGenerator.GetInt32(0, 1_000_000_000).ToString("D9");
        return body + Ean13CheckDigit(body);
    }

    public static int Ean13CheckDigit(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10;
    }

    public static bool IsValidEan13(string code) =>
        code.Length == 13 && code.All(char.IsAsciiDigit) && Ean13CheckDigit(code[..12]) == code[12] - '0';

    /// <summary>Código gerado pelo sistema (não é de fabricante).</summary>
    public static bool IsInternal(string? code) => code is { Length: 13 } && code.StartsWith(InternalPrefix) && IsValidEan13(code);

    /// <summary>Código interno curto (SKU) quando o funcionário não informa: P + 7 dígitos.</summary>
    public static string NewSku() => "P" + RandomNumberGenerator.GetInt32(0, 10_000_000).ToString("D7");
}

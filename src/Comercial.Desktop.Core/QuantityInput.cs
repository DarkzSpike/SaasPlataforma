using System.Globalization;
using System.Text.RegularExpressions;

namespace Comercial.Desktop.Core;

/// <summary>
/// Multiplicador de quantidade na busca do PDV. Aceita "3*caderno", "3 * caderno", "3x caderno",
/// "*3 caderno" e "caderno*3"; vírgula decimal para itens a granel ("1,5*arroz").
/// </summary>
public static partial class QuantityInput
{
    public const decimal MaxQuantity = 9999;
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    [GeneratedRegex(@"^\s*(?<q>\d+(?:[.,]\d{1,3})?)\s*(?:\*|[xX](?=\s))\s*(?<t>.+?)\s*$")]
    private static partial Regex Prefix();          // 3*caderno · 3 x caderno

    [GeneratedRegex(@"^\s*\*\s*(?<q>\d+(?:[.,]\d{1,3})?)\s+(?<t>.+?)\s*$")]
    private static partial Regex StarFirst();       // *3 caderno

    [GeneratedRegex(@"^\s*(?<t>.+?)\s*\*\s*(?<q>\d+(?:[.,]\d{1,3})?)\s*$")]
    private static partial Regex Suffix();          // caderno*3 (só com *: "Caixa 20 x 30" é nome de produto)

    /// <summary>Separa quantidade e termo de busca. Sem multiplicador: quantidade 1 e o texto inteiro.</summary>
    /// <returns>false quando há multiplicador mas a quantidade é inválida (zero, acima do limite) ou falta o produto.</returns>
    public static bool TryParse(string input, out decimal quantity, out string term, out string? error)
    {
        quantity = 1; error = null;
        term = (input ?? "").Trim();
        if (!term.Contains('*') && !Regex.IsMatch(term, @"^\d+(?:[.,]\d+)?\s*[xX]\s")) return true;

        var m = Prefix().Match(term);
        if (!m.Success) m = StarFirst().Match(term);
        if (!m.Success) m = Suffix().Match(term);
        if (!m.Success)
        {
            error = "Use quantidade * produto, por exemplo 3*caderno.";
            return false;
        }
        var q = decimal.Parse(m.Groups["q"].Value.Replace('.', ','), NumberStyles.Number, PtBr);
        var t = m.Groups["t"].Value.Trim().TrimStart('*').Trim();
        if (t.Length == 0) { error = "Informe o produto depois da quantidade, por exemplo 3*caderno."; return false; }
        if (q <= 0 || q > MaxQuantity) { error = $"Quantidade deve ser maior que zero e no máximo {MaxQuantity:0}."; return false; }
        quantity = q; term = t;
        return true;
    }
}

/// <summary>
/// Preço digitado no caixa para produto de preço livre: "100=9,90", "100 = 9.90", "3*100=9,90" (a quantidade sai antes).
/// </summary>
public static partial class PriceInput
{
    [GeneratedRegex(@"^\s*(?<c>[^=]+?)\s*=\s*(?<p>\d{1,7}(?:[.,]\d{1,2})?)\s*$")]
    private static partial Regex CodePrice();

    /// <summary>true quando o texto tem "=": separa o código e o preço (em centavos). error preenchido se o preço for inválido.</summary>
    public static bool TrySplit(string term, out string code, out long priceCents, out string? error)
    {
        code = term; priceCents = 0; error = null;
        if (!term.Contains('=')) return false;
        var m = CodePrice().Match(term);
        if (!m.Success) { error = "Use código=preço, por exemplo 100=9,90."; return true; }
        var value = decimal.Parse(m.Groups["p"].Value.Replace('.', ','), NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"));
        code = m.Groups["c"].Value.Trim();
        priceCents = (long)Math.Round(value * 100);
        if (priceCents <= 0) error = "O preço precisa ser maior que zero.";
        return true;
    }
}

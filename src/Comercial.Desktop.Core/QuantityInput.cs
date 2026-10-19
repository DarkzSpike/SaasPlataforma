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

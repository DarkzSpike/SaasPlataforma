namespace Comercial.Domain;

/// <summary>Validações de dados fiscais (formato). A correção da tributação é responsabilidade do contador da loja.</summary>
public static class FiscalRules
{
    public static string Digits(string? s) => new((s ?? "").Where(char.IsAsciiDigit).ToArray());

    public static bool IsCnpj(string? value)
    {
        var c = Digits(value);
        if (c.Length != 14 || c.Distinct().Count() == 1) return false;
        int Dv(string b, int[] w) { var s = b.Select((ch, i) => (ch - '0') * w[i]).Sum() % 11; return s < 2 ? 0 : 11 - s; }
        var d1 = Dv(c[..12], [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
        var d2 = Dv(c[..12] + d1, [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
        return c[12] - '0' == d1 && c[13] - '0' == d2;
    }

    public static readonly HashSet<string> Ufs = ["AC", "AL", "AP", "AM", "BA", "CE", "DF", "ES", "GO", "MA", "MT", "MS", "MG", "PA", "PB", "PR", "PE", "PI", "RJ", "RN", "RS", "RO", "RR", "SC", "SP", "SE", "TO"];

    /// <summary>Valida os campos fiscais do produto; null = ok.</summary>
    public static string? ValidateProduct(string? ncm, string? cest, string? cfop, int? origin, string? icmsCst, string? ibsCbsCst, string? ibsCbsClass)
    {
        if (ncm is { Length: > 0 } && Digits(ncm).Length != 8) return "NCM deve ter 8 dígitos.";
        if (cest is { Length: > 0 } && Digits(cest).Length != 7) return "CEST deve ter 7 dígitos.";
        if (cfop is { Length: > 0 } && (Digits(cfop).Length != 4 || cfop[0] is not ('5' or '6'))) return "CFOP de saída deve ter 4 dígitos e começar com 5 ou 6.";
        if (origin is < 0 or > 8) return "Origem da mercadoria deve ser de 0 a 8.";
        if (icmsCst is { Length: > 0 } && Digits(icmsCst).Length is not (2 or 3)) return "CST/CSOSN do ICMS inválido.";
        if (ibsCbsCst is { Length: > 0 } && Digits(ibsCbsCst).Length != 3) return "CST do IBS/CBS deve ter 3 dígitos.";
        if (ibsCbsClass is { Length: > 0 } && Digits(ibsCbsClass).Length != 6) return "Classificação tributária do IBS/CBS (cClassTrib) deve ter 6 dígitos.";
        return null;
    }

    /// <summary>Código da forma de pagamento da NF-e (tPag).</summary>
    public static string PaymentCode(PaymentMethod m) => m switch
    {
        PaymentMethod.Cash => "01", PaymentMethod.CreditCard => "03", PaymentMethod.DebitCard => "04", PaymentMethod.StoreCredit => "05", PaymentMethod.Pix => "17", _ => "99",
    };
}

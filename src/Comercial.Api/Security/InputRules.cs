using Comercial.Domain;

namespace Comercial.Api.Security;

/// <summary>
/// Validação de entradas de busca e paginação. NÃO substitui consultas parametrizadas (todas as consultas passam pelo
/// EF Core, que envia os valores como parâmetros); só limita abusos e transforma entradas inválidas em erro 400 claro.
/// Apóstrofos, acentos e outros caracteres legítimos são aceitos normalmente.
/// </summary>
public static class InputRules
{
    public const int MaxSearchLength = 100;
    public const int MaxSkip = 1_000_000;

    /// <summary>Texto de busca: vazio vira null; acima de 100 caracteres é recusado (não é truncado em silêncio).</summary>
    public static string? SearchTerm(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return null;
        var t = q.Trim();
        if (t.Length > MaxSearchLength) throw new DomainException("invalid_query", $"Busca muito longa (máximo {MaxSearchLength} caracteres).");
        return t;
    }

    /// <summary>Deslocamento de paginação dentro do intervalo permitido.</summary>
    public static int Skip(int? skip) =>
        skip is null ? 0 : skip is < 0 or > MaxSkip ? throw new DomainException("invalid_paging", "Paginação inválida.") : skip.Value;
}

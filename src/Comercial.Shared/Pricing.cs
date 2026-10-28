namespace Comercial.Shared;

/// <summary>
/// Preço de venda a partir do custo e da % de lucro sobre o custo (markup).
/// Ex.: paguei R$ 10,00 + 100% = vende por R$ 20,00 (lucro de R$ 10,00 por unidade).
/// A mesma regra é usada no servidor, no caixa e no app do celular.
/// </summary>
public static class Pricing
{
    public const decimal MaxMarkupPercent = 10_000m;

    public static long PriceFromMarkup(long costCents, decimal markupPercent) =>
        (long)Math.Round(costCents * (1 + markupPercent / 100m), MidpointRounding.AwayFromZero);

    /// <summary>% de lucro de um preço já existente (null quando não há custo para comparar).</summary>
    public static decimal? MarkupFromPrice(long costCents, long priceCents) =>
        costCents <= 0 ? null : Math.Round((priceCents - costCents) * 100m / costCents, 2);
}

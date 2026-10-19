namespace Comercial.Shared.Promotions;

public enum PromoType { BuyXPayY = 1, QuantityPrice = 2, PercentOff = 3, Coupon = 4 }

/// <summary>Regra de promoção. ProductId nulo = vale para todos os produtos (só PercentOff e Coupon).</summary>
public sealed record PromoRule(Guid Id, string Name, PromoType Type, Guid? ProductId, int BuyQty, int PayQty, decimal MinQty, long UnitPriceCents,
    decimal Percent, string? CouponCode, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, bool Active = true);

public sealed record PromoLine(Guid ProductId, decimal Quantity, long UnitPriceCents);

public sealed record PromoResult(long DiscountCents, string? RuleName);

/// <summary>
/// Mesmo cálculo no servidor e no PDV: por item vale a MELHOR promoção automática (não acumulam entre si);
/// o cupom, se informado e válido, aplica um percentual sobre o que sobrar.
/// </summary>
public static class PromotionEngine
{
    public static bool IsActive(PromoRule r, DateTimeOffset now) => r.Active && (r.StartsAt is null || now >= r.StartsAt) && (r.EndsAt is null || now < r.EndsAt);

    private static long Candidate(PromoRule r, PromoLine l)
    {
        var gross = (long)decimal.Round(l.UnitPriceCents * l.Quantity, 0, MidpointRounding.AwayFromZero);
        return r.Type switch
        {
            PromoType.BuyXPayY when r.BuyQty > r.PayQty && r.PayQty >= 0 && r.BuyQty > 0 =>
                (long)decimal.Floor(decimal.Floor(l.Quantity) / r.BuyQty) * (r.BuyQty - r.PayQty) * l.UnitPriceCents,
            PromoType.QuantityPrice when l.Quantity >= r.MinQty && r.MinQty > 0 && r.UnitPriceCents < l.UnitPriceCents =>
                gross - (long)decimal.Round(r.UnitPriceCents * l.Quantity, 0, MidpointRounding.AwayFromZero),
            PromoType.PercentOff when r.Percent is > 0 and <= 100 => (long)decimal.Round(gross * r.Percent / 100m, 0, MidpointRounding.AwayFromZero),
            _ => 0,
        };
    }

    public static IReadOnlyList<PromoResult> Apply(IReadOnlyList<PromoLine> lines, IEnumerable<PromoRule> rules, DateTimeOffset now, string? coupon)
    {
        var active = rules.Where(r => IsActive(r, now)).ToList();
        var auto = active.Where(r => r.Type != PromoType.Coupon).ToList();
        var cpn = string.IsNullOrWhiteSpace(coupon) ? null
            : active.FirstOrDefault(r => r.Type == PromoType.Coupon && string.Equals(r.CouponCode, coupon.Trim(), StringComparison.OrdinalIgnoreCase));
        var result = new List<PromoResult>(lines.Count);
        foreach (var l in lines)
        {
            var gross = (long)decimal.Round(l.UnitPriceCents * l.Quantity, 0, MidpointRounding.AwayFromZero);
            var best = auto.Where(r => r.ProductId is null || r.ProductId == l.ProductId)
                .Select(r => (Rule: r, Value: Math.Min(gross, Math.Max(0, Candidate(r, l))))).OrderByDescending(x => x.Value).FirstOrDefault();
            var discount = best.Rule is null ? 0 : best.Value;
            var name = discount > 0 ? best.Rule!.Name : null;
            if (cpn is not null && (cpn.ProductId is null || cpn.ProductId == l.ProductId) && cpn.Percent is > 0 and <= 100)
            {
                var extra = (long)decimal.Round((gross - discount) * cpn.Percent / 100m, 0, MidpointRounding.AwayFromZero);
                if (extra > 0) { discount += extra; name = name is null ? cpn.Name : name + " + " + cpn.Name; }
            }
            result.Add(new PromoResult(Math.Min(gross, discount), name));
        }
        return result;
    }

    /// <summary>Valida uma regra antes de salvar; null = ok.</summary>
    public static string? Validate(PromoRule r) => r.Type switch
    {
        _ when string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 80 => "Informe o nome da promoção.",
        _ when r.EndsAt is not null && r.StartsAt is not null && r.EndsAt <= r.StartsAt => "O fim precisa ser depois do início.",
        PromoType.BuyXPayY when r.ProductId is null => "Leve X pague Y precisa de um produto.",
        PromoType.BuyXPayY when r.BuyQty is < 2 or > 100 || r.PayQty < 1 || r.PayQty >= r.BuyQty => "Use, por exemplo, leve 3 pague 2.",
        PromoType.QuantityPrice when r.ProductId is null => "Preço por quantidade precisa de um produto.",
        PromoType.QuantityPrice when r.MinQty <= 0 || r.UnitPriceCents <= 0 => "Informe a quantidade mínima e o preço unitário.",
        PromoType.PercentOff when r.Percent is <= 0 or > 90 => "Percentual de 1% a 90%.",
        PromoType.Coupon when string.IsNullOrWhiteSpace(r.CouponCode) || r.CouponCode.Length is < 3 or > 30 => "Informe o código do cupom (3 a 30 caracteres).",
        PromoType.Coupon when r.Percent is <= 0 or > 90 => "Percentual do cupom de 1% a 90%.",
        _ => null,
    };
}

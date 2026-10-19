namespace Comercial.Desktop.Core;

/// <summary>
/// Regras da venda com várias formas de pagamento (mesmas do servidor):
/// a soma precisa cobrir o total; só dinheiro gera troco; cartão/PIX/fiado não podem passar do que falta.
/// </summary>
public static class PaymentSplit
{
    public const int Cash = 1;

    public sealed record Result(bool Ok, string? Error, long PaidCents, long ChangeCents);

    public static Result Validate(long totalCents, IReadOnlyList<(int Method, long Cents)> payments)
    {
        if (payments.Count == 0) return new(false, "Informe a forma de pagamento.", 0, 0);
        if (payments.Any(p => p.Cents <= 0)) return new(false, "Valor de pagamento inválido.", 0, 0);
        var nonCash = payments.Where(p => p.Method != Cash).Sum(p => p.Cents);
        if (nonCash > totalCents) return new(false, "Cartão, PIX e fiado não geram troco: o valor passa do total.", 0, 0);
        var paid = payments.Sum(p => p.Cents);
        if (paid < totalCents) return new(false, $"Falta receber {(totalCents - paid) / 100m:C}.", paid, 0);
        var change = paid - totalCents;
        var cash = payments.Where(p => p.Method == Cash).Sum(p => p.Cents);
        if (change > cash) return new(false, "Troco maior que o valor em dinheiro.", paid, 0);
        return new(true, null, paid, change);
    }

    /// <summary>Quanto ainda falta pagar (nunca negativo).</summary>
    public static long Remaining(long totalCents, IEnumerable<(int Method, long Cents)> payments) => Math.Max(0, totalCents - payments.Sum(p => p.Cents));
}

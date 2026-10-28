/// Preço de venda a partir do custo e da % de lucro sobre o custo (mesma regra do servidor e do caixa).
/// Ex.: paguei R$ 10,00 + 100% = vende por R$ 20,00.
class Pricing {
  static const maxMarkupPercent = 10000.0;

  static int priceFromMarkup(int costCents, double markupPercent) => (costCents * (1 + markupPercent / 100)).round();

  /// % de lucro de um preço já existente (null sem custo para comparar).
  static double? markupFromPrice(int costCents, int priceCents) =>
      costCents <= 0 ? null : ((priceCents - costCents) * 100 / costCents * 100).round() / 100;

  /// "100", "35,5" ou "35.5%". Null se inválida.
  static double? parsePercent(String s) {
    final t = s.replaceAll('%', '').replaceAll(',', '.').trim();
    if (t.isEmpty) return null;
    final v = double.tryParse(t);
    return v == null || v < 0 || v > maxMarkupPercent ? null : v;
  }

  static String formatPercent(double v) => (v == v.roundToDouble() ? v.toInt().toString() : v.toStringAsFixed(2)).replaceAll('.', ',');
}

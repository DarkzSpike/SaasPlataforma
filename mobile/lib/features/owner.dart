import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../core/api_client.dart';
import '../core/session.dart';

final _money = NumberFormat.simpleCurrency(locale: 'pt_BR');
String _r(num cents) => _money.format(cents / 100);

const _methodNames = {'Cash': 'Dinheiro', 'DebitCard': 'Débito', 'CreditCard': 'Crédito', 'Pix': 'PIX', 'StoreCredit': 'Fiado', 'Other': 'Outros'};

/// Painel do dono: números do dia e do mês, lucro, quebra de caixa, gráfico do período, mais vendidos,
/// formas de pagamento, últimas vendas, estoque baixo e situação dos caixas.
class OwnerDashboardPage extends StatefulWidget {
  const OwnerDashboardPage({super.key, required this.api, required this.me, required this.onLogout});
  final ApiClient api;
  final Me me;
  final Future<void> Function() onLogout;
  @override
  State<OwnerDashboardPage> createState() => _OwnerDashboardPageState();
}

class _OwnerDashboardPageState extends State<OwnerDashboardPage> {
  Map<String, dynamic>? d;
  Map<String, dynamic>? o;
  String? error;
  int days = 7;
  DateTime? loadedAt;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final dash = await widget.api.get('/api/v1/mobile/dashboard');
      final ov = await widget.api.get('/api/v1/mobile/overview?days=$days');
      setState(() { d = dash; o = ov; error = null; loadedAt = DateTime.now(); });
    } on ApiException catch (e) {
      setState(() => error = switch (e.code) {
        'module_not_enabled' => 'O aplicativo do proprietário não está contratado para esta empresa.',
        'permission_denied' => 'Seu usuário não tem acesso ao painel.',
        _ => e.message,
      });
    } catch (_) {
      setState(() => error = 'Sem conexão. Os dados abaixo podem estar desatualizados.');
    }
  }

  String _ago(String? iso) {
    if (iso == null) return 'nunca';
    final x = DateTime.now().difference(DateTime.parse(iso).toLocal());
    if (x.inMinutes < 1) return 'agora';
    if (x.inHours < 1) return 'há ${x.inMinutes} min';
    if (x.inDays < 1) return 'há ${x.inHours} h';
    return 'há ${x.inDays} dias';
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final dash = d, ov = o;
    return Scaffold(
      appBar: AppBar(title: Text(widget.me.tenantName), actions: [
        IconButton(onPressed: _load, icon: const Icon(Icons.refresh), tooltip: 'Atualizar'),
        IconButton(onPressed: widget.onLogout, icon: const Icon(Icons.logout), tooltip: 'Sair'),
      ]),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(padding: const EdgeInsets.fromLTRB(12, 8, 12, 96), children: [
          if (error != null) Card(child: ListTile(leading: const Icon(Icons.warning_amber), title: Text(error!))),
          if (dash == null && error == null) const Padding(padding: EdgeInsets.all(40), child: Center(child: CircularProgressIndicator())),
          if (dash != null) ...[
            GridView.count(
              crossAxisCount: 2, shrinkWrap: true, physics: const NeverScrollableScrollPhysics(), childAspectRatio: 1.6,
              mainAxisSpacing: 8, crossAxisSpacing: 8,
              children: [
                _Kpi('Faturamento hoje', _r(dash['todayRevenueCents'] as num), 'bruto • ${dash['todaySalesCount']} vendas', Icons.point_of_sale, cs.primary),
                if (dash['todayGrossProfitCents'] != null)
                  _Kpi('Lucro do dia', _r(dash['todayGrossProfitCents'] as num), 'vendas − custo', Icons.trending_up, Colors.green),
                _Kpi('Faturamento no mês', _r(dash['monthRevenueCents'] as num), 'bruto • ${dash['monthSalesCount']} vendas', Icons.calendar_month, cs.primary),
                if (dash['monthGrossProfitCents'] != null)
                  _Kpi('Lucro do mês', _r(dash['monthGrossProfitCents'] as num), 'vendas − custo', Icons.savings_outlined, Colors.green),
                _Kpi('Caixas abertos', '${dash['openCashSessions']}', 'agora', Icons.lock_open, cs.secondary),
                _Kpi('Quebra de caixa', _r(dash['monthCashShortageCents'] as num), '${dash['monthCashShortageCount']} no mês',
                    Icons.money_off, (dash['monthCashShortageCents'] as num) > 0 ? cs.error : cs.secondary),
              ],
            ),
            const SizedBox(height: 8),
          ],
          if (ov != null) ...[
            _Section(
              title: 'Vendas por dia',
              trailing: SegmentedButton<int>(
                segments: const [ButtonSegment(value: 7, label: Text('7d')), ButtonSegment(value: 15, label: Text('15d')), ButtonSegment(value: 30, label: Text('30d'))],
                selected: {days},
                onSelectionChanged: (s) { setState(() => days = s.first); _load(); },
              ),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text('Faturamento ${_r(ov['revenueCents'] as num)} em ${ov['salesCount']} vendas • ticket médio ${_r(ov['averageTicketCents'] as num)}'),
                if (ov['profitCents'] != null)
                  Text('Lucro ${_r(ov['profitCents'] as num)} (vendas − custo dos produtos)', style: const TextStyle(color: Colors.green, fontWeight: FontWeight.bold)),
                if ((ov['itemsWithoutCost'] as num? ?? 0) > 0)
                  Text('Atenção: ${ov['itemsWithoutCost']} item(ns) vendido(s) sem custo cadastrado — o lucro aparece maior que o real.',
                      style: TextStyle(color: cs.error, fontSize: 12)),
                if (ov['profitCents'] != null)
                  const Padding(padding: EdgeInsets.only(top: 4), child: Text('Barra azul = faturamento • parte verde = lucro', style: TextStyle(fontSize: 11))),
                const SizedBox(height: 12),
                _Bars(rows: (ov['byDay'] as List).cast<Map<String, dynamic>>()),
              ]),
            ),
            _Section(
              title: 'Mais vendidos',
              child: (ov['topProducts'] as List).isEmpty
                  ? const Text('Nenhuma venda no período.')
                  : Column(children: [
                      for (final (i, p) in (ov['topProducts'] as List).cast<Map<String, dynamic>>().indexed)
                        ListTile(
                          dense: true, contentPadding: EdgeInsets.zero,
                          leading: CircleAvatar(radius: 14, child: Text('${i + 1}')),
                          title: Text(p['name'] as String),
                          subtitle: Text('${_qty(p['quantity'] as num)} vendidos'),
                          trailing: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.end, children: [
                            Text(_r(p['revenueCents'] as num)),
                            if (p['profitCents'] != null) Text('lucro ${_r(p['profitCents'] as num)}', style: const TextStyle(color: Colors.green, fontSize: 12)),
                          ]),
                        ),
                    ]),
            ),
            _Section(
              title: 'Formas de pagamento',
              child: Column(children: [
                for (final m in (ov['paymentMix'] as List).cast<Map<String, dynamic>>())
                  _ShareRow(label: _methodNames[m['method']] ?? m['method'] as String, value: (m['amountCents'] as num).toDouble(),
                      total: (ov['revenueCents'] as num).toDouble()),
                if ((ov['paymentMix'] as List).isEmpty) const Text('Nenhuma venda no período.'),
              ]),
            ),
            _Section(
              title: 'Últimas vendas',
              child: Column(children: [
                for (final s in (ov['recentSales'] as List).cast<Map<String, dynamic>>())
                  ListTile(
                    dense: true, contentPadding: EdgeInsets.zero,
                    title: Text(_r(s['totalCents'] as num), style: const TextStyle(fontWeight: FontWeight.bold)),
                    subtitle: Text('${s['summary']}${(s['items'] as num) > 3 ? '...' : ''}\n${s['operator']}'),
                    isThreeLine: true,
                    trailing: Text(DateFormat('dd/MM HH:mm').format(DateTime.parse(s['receivedAt'] as String).toLocal())),
                  ),
                if ((ov['recentSales'] as List).isEmpty) const Text('Nenhuma venda no período.'),
              ]),
            ),
            _Section(
              title: 'Estoque baixo (${(ov['lowStock'] as List).length})',
              child: Column(children: [
                for (final p in (ov['lowStock'] as List).cast<Map<String, dynamic>>())
                  ListTile(
                    dense: true, contentPadding: EdgeInsets.zero,
                    leading: Icon(Icons.warning_amber, color: cs.error),
                    title: Text(p['name'] as String),
                    trailing: Text('${_qty(p['quantity'] as num)} / mín. ${_qty(p['minStock'] as num)}'),
                  ),
                if ((ov['lowStock'] as List).isEmpty) const Text('Tudo acima do estoque mínimo.'),
                if ((ov['productsWithoutPrice'] as num) > 0)
                  ListTile(dense: true, contentPadding: EdgeInsets.zero, leading: const Icon(Icons.sell_outlined),
                      title: Text('${ov['productsWithoutPrice']} produto(s) sem preço — veja em Produtos → "Sem preço"')),
              ]),
            ),
          ],
          if (dash != null)
            _Section(
              title: 'Caixas (computadores)',
              child: Column(children: [
                for (final dv in (dash['devices'] as List).cast<Map<String, dynamic>>())
                  ListTile(dense: true, contentPadding: EdgeInsets.zero, leading: const Icon(Icons.computer),
                      title: Text(dv['name'] as String), subtitle: Text('Última sincronização: ${_ago(dv['lastSyncAt'] as String?)}')),
                if ((dash['devices'] as List).isEmpty) const Text('Nenhum caixa ativado ainda.'),
                Padding(padding: const EdgeInsets.only(top: 8), child: Text('Licença: ${dash['license']}. ${dash['note']}', style: Theme.of(context).textTheme.bodySmall)),
              ]),
            ),
          if (loadedAt != null && DateTime.now().difference(loadedAt!).inMinutes > 15)
            const Card(child: ListTile(leading: Icon(Icons.schedule), title: Text('Dados com mais de 15 minutos. Puxe para atualizar.'))),
        ]),
      ),
    );
  }

  static String _qty(num v) => v == v.roundToDouble() ? v.toInt().toString() : v.toStringAsFixed(3).replaceAll('.', ',');
}

class _Kpi extends StatelessWidget {
  const _Kpi(this.title, this.value, this.sub, this.icon, this.color);
  final String title, value, sub;
  final IconData icon;
  final Color color;
  @override
  Widget build(BuildContext context) => Card(
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisAlignment: MainAxisAlignment.spaceBetween, children: [
            Row(children: [Icon(icon, size: 18, color: color), const SizedBox(width: 6), Expanded(child: Text(title, overflow: TextOverflow.ellipsis))]),
            FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerLeft,
                child: Text(value, style: Theme.of(context).textTheme.titleLarge?.copyWith(color: color, fontWeight: FontWeight.bold))),
            Text(sub, style: Theme.of(context).textTheme.bodySmall),
          ]),
        ),
      );
}

class _Section extends StatelessWidget {
  const _Section({required this.title, required this.child, this.trailing});
  final String title;
  final Widget child;
  final Widget? trailing;
  @override
  Widget build(BuildContext context) => Card(
        child: Padding(
          padding: const EdgeInsets.all(14),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [Expanded(child: Text(title, style: Theme.of(context).textTheme.titleMedium)), if (trailing != null) trailing!]),
            const SizedBox(height: 8),
            child,
          ]),
        ),
      );
}

/// Gráfico de barras simples (sem biblioteca): faturamento por dia.
class _Bars extends StatelessWidget {
  const _Bars({required this.rows});
  final List<Map<String, dynamic>> rows;
  @override
  Widget build(BuildContext context) {
    final max = rows.fold<num>(0, (m, r) => (r['revenueCents'] as num) > m ? r['revenueCents'] as num : m);
    final cs = Theme.of(context).colorScheme;
    return SizedBox(
      height: 140,
      child: Row(crossAxisAlignment: CrossAxisAlignment.end, children: [
        for (final r in rows)
          Expanded(
            child: Tooltip(
              message: '${(r['date'] as String).substring(8)}/${(r['date'] as String).substring(5, 7)}: faturamento ${_r(r['revenueCents'] as num)}'
                  '${r['profitCents'] == null ? '' : ' • lucro ${_r(r['profitCents'] as num)}'} (${r['count']} vendas)',
              child: Column(mainAxisAlignment: MainAxisAlignment.end, children: [
                // Barra = faturamento do dia; parte verde de baixo = lucro (quando o usuário pode ver).
                Container(
                  margin: const EdgeInsets.symmetric(horizontal: 2),
                  height: max == 0 ? 2 : 2 + 110 * ((r['revenueCents'] as num) / max),
                  decoration: BoxDecoration(color: cs.primary, borderRadius: BorderRadius.circular(4)),
                  alignment: Alignment.bottomCenter,
                  child: r['profitCents'] == null || max == 0 || (r['revenueCents'] as num) <= 0 ? null : FractionallySizedBox(
                    heightFactor: ((r['profitCents'] as num) / (r['revenueCents'] as num)).clamp(0, 1).toDouble(),
                    widthFactor: 1,
                    child: Container(decoration: BoxDecoration(color: Colors.green, borderRadius: BorderRadius.circular(4))),
                  ),
                ),
                const SizedBox(height: 4),
                if (rows.length <= 15) Text((r['date'] as String).substring(8), style: const TextStyle(fontSize: 10)),
              ]),
            ),
          ),
      ]),
    );
  }
}

class _ShareRow extends StatelessWidget {
  const _ShareRow({required this.label, required this.value, required this.total});
  final String label;
  final double value, total;
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 4),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Row(children: [Expanded(child: Text(label)), Text(_r(value))]),
          const SizedBox(height: 4),
          LinearProgressIndicator(value: total <= 0 ? 0 : (value / total).clamp(0, 1), minHeight: 6, borderRadius: BorderRadius.circular(4)),
        ]),
      );
}


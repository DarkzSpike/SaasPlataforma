import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intl/intl.dart';

import '../core/api_client.dart';
import '../core/session.dart';

final _money = NumberFormat.simpleCurrency(locale: 'pt_BR');
final _date = DateFormat('dd/MM/yyyy');
final _qty = NumberFormat('#,##0.###', 'pt_BR');
String _brl(num cents) => _money.format(cents / 100);

/// Clientes e fiado no celular: quanto a loja tem para receber, quem está devendo, o limite de cada um
/// e o que cada cliente levou no fiado. O servidor confere permissão e limite de novo a cada pedido.
class CustomersPage extends StatefulWidget {
  const CustomersPage({super.key, required this.api, required this.me});
  final ApiClient api;
  final Me me;
  @override
  State<CustomersPage> createState() => _CustomersPageState();
}

enum _Filter { owing, limit, all }

class _CustomersPageState extends State<CustomersPage> {
  Map<String, dynamic>? data;
  String? error;
  bool loading = true;
  String search = '';
  _Filter filter = _Filter.owing;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() => loading = true);
    try {
      final d = await widget.api.get('/api/v1/customers/credit-overview');
      setState(() {
        data = d;
        error = null;
      });
    } catch (e) {
      setState(() => error = e is ApiException ? e.message : 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => loading = false);
    }
  }

  List<Map<String, dynamic>> get _rows {
    final all = (data?['customers'] as List? ?? []).cast<Map<String, dynamic>>();
    final q = search.trim().toLowerCase();
    return all.where((c) {
      if (filter == _Filter.owing && (c['openCents'] as num) <= 0) return false;
      if (filter == _Filter.limit && (c['limitCents'] as num) <= 0) return false;
      return q.isEmpty || (c['name'] as String).toLowerCase().contains(q) || ((c['phone'] as String?) ?? '').contains(q);
    }).toList();
  }

  Future<void> _open(Map<String, dynamic> c) async {
    await Navigator.push(context, MaterialPageRoute(builder: (_) => _CustomerCreditPage(api: widget.api, me: widget.me, customer: c)));
    _load();
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final rows = _rows;
    return Scaffold(
      appBar: AppBar(title: const Text('Clientes e fiado'), actions: [IconButton(onPressed: _load, icon: const Icon(Icons.refresh))]),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(padding: const EdgeInsets.fromLTRB(12, 8, 12, 96), children: [
          if (loading && data == null) const Padding(padding: EdgeInsets.all(48), child: Center(child: CircularProgressIndicator())),
          if (error != null) Card(child: ListTile(leading: Icon(Icons.warning_amber, color: cs.error), title: Text(error!))),
          if (data != null) ...[
            Row(children: [
              Expanded(child: _Tile(label: 'Total no fiado', value: _brl(data!['totalOpenCents'] as num), color: cs.primary, big: true)),
              const SizedBox(width: 8),
              Expanded(child: _Tile(label: 'Clientes devendo', value: '${data!['debtors']}', big: true)),
            ]),
            const SizedBox(height: 8),
            Row(children: [
              Expanded(child: _Tile(label: 'Vencido', value: _brl(data!['overdueCents'] as num), color: (data!['overdueCents'] as num) > 0 ? cs.error : null)),
              const SizedBox(width: 8),
              Expanded(child: _Tile(label: 'Produtos no fiado', value: _qty.format(data!['itemsQuantity'] as num))),
            ]),
            const SizedBox(height: 12),
            TextField(
              decoration: const InputDecoration(prefixIcon: Icon(Icons.search), hintText: 'Buscar cliente pelo nome ou celular', border: OutlineInputBorder(), isDense: true),
              onChanged: (v) => setState(() => search = v),
            ),
            const SizedBox(height: 8),
            SegmentedButton<_Filter>(
              segments: const [
                ButtonSegment(value: _Filter.owing, label: Text('Devendo')),
                ButtonSegment(value: _Filter.limit, label: Text('Com limite')),
                ButtonSegment(value: _Filter.all, label: Text('Todos')),
              ],
              selected: {filter},
              onSelectionChanged: (s) => setState(() => filter = s.first),
            ),
            const SizedBox(height: 8),
            if (rows.isEmpty)
              Padding(
                padding: const EdgeInsets.all(32),
                child: Text(filter == _Filter.owing ? 'Ninguém está devendo no fiado.' : 'Nenhum cliente encontrado.', textAlign: TextAlign.center),
              ),
            for (final c in rows) _CustomerRow(c: c, onTap: () => _open(c)),
            if (filter != _Filter.owing)
              const Padding(
                padding: EdgeInsets.all(12),
                child: Text('Aparecem aqui os clientes com limite de fiado ou com conta em aberto. Para liberar o fiado de outro cliente, '
                    'defina o limite dele no caixa (Clientes).', style: TextStyle(fontSize: 12)),
              ),
          ],
        ]),
      ),
    );
  }
}

class _Tile extends StatelessWidget {
  const _Tile({required this.label, required this.value, this.color, this.big = false});
  final String label, value;
  final Color? color;
  final bool big;
  @override
  Widget build(BuildContext context) => Card(
        margin: EdgeInsets.zero,
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(label, style: Theme.of(context).textTheme.bodySmall),
            const SizedBox(height: 4),
            FittedBox(child: Text(value, style: TextStyle(fontSize: big ? 22 : 17, fontWeight: FontWeight.bold, color: color))),
          ]),
        ),
      );
}

class _CustomerRow extends StatelessWidget {
  const _CustomerRow({required this.c, required this.onTap});
  final Map<String, dynamic> c;
  final VoidCallback onTap;
  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final open = c['openCents'] as num, limit = c['limitCents'] as num, overdue = c['overdueCents'] as num;
    final used = limit > 0 ? (open / limit).clamp(0, 1).toDouble() : (open > 0 ? 1.0 : 0.0);
    return Card(
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Expanded(child: Text(c['name'] as String, style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 16))),
              Text(open > 0 ? _brl(open) : 'Em dia', style: TextStyle(fontWeight: FontWeight.bold, fontSize: 16, color: open > 0 ? cs.error : Colors.green)),
            ]),
            const SizedBox(height: 6),
            LinearProgressIndicator(value: used, minHeight: 6, borderRadius: BorderRadius.circular(3), color: used >= 0.9 ? cs.error : cs.primary),
            const SizedBox(height: 6),
            Wrap(spacing: 12, runSpacing: 4, children: [
              Text(limit > 0 ? 'Limite ${_brl(limit)}' : 'Sem limite', style: const TextStyle(fontSize: 12)),
              Text('Livre ${_brl(c['availableCents'] as num)}', style: const TextStyle(fontSize: 12)),
              if (overdue > 0) Text('Vencido ${_brl(overdue)}', style: TextStyle(fontSize: 12, color: cs.error, fontWeight: FontWeight.bold)),
              if (c['billingDay'] != null) Text('Cobrança todo dia ${c['billingDay']}', style: const TextStyle(fontSize: 12)),
            ]),
          ]),
        ),
      ),
    );
  }
}

/// Fiado de um cliente: o que deve, o que levou em cada compra em aberto, editar o limite e cobrar no WhatsApp.
class _CustomerCreditPage extends StatefulWidget {
  const _CustomerCreditPage({required this.api, required this.me, required this.customer});
  final ApiClient api;
  final Me me;
  final Map<String, dynamic> customer;
  @override
  State<_CustomerCreditPage> createState() => _CustomerCreditPageState();
}

class _CustomerCreditPageState extends State<_CustomerCreditPage> {
  Map<String, dynamic>? st;
  String? error;
  bool loading = true, busy = false;

  String get _id => widget.customer['id'] as String;
  bool get _canManage => widget.me.can('finance.manage');

  @override
  void initState() {
    super.initState();
    _load();
  }

  void _snack(String m) {
    if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(m)));
  }

  Future<void> _load() async {
    try {
      final s = await widget.api.get('/api/v1/customers/$_id/credit');
      setState(() {
        st = s;
        error = null;
      });
    } catch (e) {
      setState(() => error = e is ApiException ? e.message : 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => loading = false);
    }
  }

  Future<void> _editLimit() async {
    final current = (st?['limitCents'] as num? ?? 0) / 100;
    final ctrl = TextEditingController(text: current == 0 ? '' : current.toStringAsFixed(2).replaceAll('.', ','));
    final value = await showDialog<int>(
      context: context,
      builder: (c) => AlertDialog(
        title: const Text('Limite do fiado'),
        content: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text('Até quanto ${widget.customer['name']} pode dever. Use 0 para não vender mais no fiado.'),
          const SizedBox(height: 12),
          TextField(
            controller: ctrl,
            autofocus: true,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            inputFormatters: [FilteringTextInputFormatter.allow(RegExp(r'[0-9.,]'))],
            decoration: const InputDecoration(prefixText: 'R\$ ', border: OutlineInputBorder()),
          ),
        ]),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c), child: const Text('Cancelar')),
          FilledButton(
            onPressed: () {
              final t = ctrl.text.trim().replaceAll('.', '').replaceAll(',', '.');
              final v = t.isEmpty ? 0.0 : double.tryParse(t);
              if (v == null || v < 0) return;
              Navigator.pop(c, (v * 100).round());
            },
            child: const Text('Salvar'),
          ),
        ],
      ),
    );
    if (value == null) return;
    setState(() => busy = true);
    try {
      await widget.api.put('/api/v1/customers/$_id/credit-limit', {'limitCents': value});
      await _load();
      _snack(value == 0 ? 'Fiado bloqueado para este cliente.' : 'Limite alterado para ${_brl(value)}.');
    } catch (e) {
      _snack(e is ApiException ? e.message : 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  Future<void> _charge() async {
    setState(() => busy = true);
    try {
      final p = await widget.api.get('/api/v1/customers/$_id/charge-preview');
      if (!mounted) return;
      if (p['canSend'] != true) {
        _snack((p['reason'] as String?) ?? 'Não dá para cobrar agora.');
        return;
      }
      final ok = await showDialog<bool>(
        context: context,
        builder: (c) => AlertDialog(
          title: Text('Cobrar no WhatsApp (${p['phone']})'),
          content: SingleChildScrollView(child: Text(p['text'] as String? ?? '')),
          actions: [
            TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Cancelar')),
            FilledButton.icon(onPressed: () => Navigator.pop(c, true), icon: const Icon(Icons.send), label: const Text('Enviar')),
          ],
        ),
      );
      if (ok != true) return;
      await widget.api.post('/api/v1/customers/$_id/charge', {});
      _snack('Cobrança enviada pelo WhatsApp da loja.');
    } catch (e) {
      _snack(e is ApiException ? e.message : 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final s = st;
    final purchases = (s?['purchases'] as List? ?? []).cast<Map<String, dynamic>>();
    final titles = (s?['titles'] as List? ?? []).cast<Map<String, dynamic>>();
    final openTitles = titles.where((t) => t['status'] == 'Open').toList();
    final paid = titles.where((t) => t['status'] == 'Paid').take(10).toList();
    final today = DateTime.now();
    return Scaffold(
      appBar: AppBar(title: Text(widget.customer['name'] as String)),
      body: loading
          ? const Center(child: CircularProgressIndicator())
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView(padding: const EdgeInsets.fromLTRB(12, 8, 12, 32), children: [
                if (error != null) Card(child: ListTile(leading: Icon(Icons.warning_amber, color: cs.error), title: Text(error!))),
                if (s != null) ...[
                  Row(children: [
                    Expanded(child: _Tile(label: 'Deve', value: _brl(s['openCents'] as num), color: (s['openCents'] as num) > 0 ? cs.error : Colors.green, big: true)),
                    const SizedBox(width: 8),
                    Expanded(child: _Tile(label: 'Livre para comprar', value: _brl(s['availableCents'] as num), big: true)),
                  ]),
                  const SizedBox(height: 8),
                  Card(
                    child: ListTile(
                      leading: const Icon(Icons.credit_score_outlined),
                      title: Text((s['limitCents'] as num) > 0 ? 'Limite: ${_brl(s['limitCents'] as num)}' : 'Sem limite (não compra no fiado)'),
                      subtitle: Text(_canManage ? 'Toque para mudar o limite' : 'Só quem cuida do financeiro muda o limite'),
                      trailing: _canManage ? const Icon(Icons.edit_outlined) : null,
                      onTap: _canManage && !busy ? _editLimit : null,
                    ),
                  ),
                  if (_canManage && (s['openCents'] as num) > 0)
                    Card(
                      child: ListTile(
                        leading: const Icon(Icons.chat_outlined, color: Color(0xFF25D366)),
                        title: const Text('Cobrar no WhatsApp'),
                        subtitle: const Text('Manda quanto ele deve e o que levou, pelo WhatsApp da loja'),
                        onTap: busy ? null : _charge,
                      ),
                    ),
                  if (purchases.isNotEmpty) ...[
                    Padding(padding: const EdgeInsets.fromLTRB(8, 16, 8, 4), child: Text('O que levou no fiado (em aberto)', style: TextStyle(color: cs.primary, fontWeight: FontWeight.bold))),
                    for (final p in purchases)
                      Card(
                        child: Padding(
                          padding: const EdgeInsets.all(12),
                          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                            Text('Compra de ${_date.format(DateTime.parse(p['receivedAt'] as String).toLocal())}', style: const TextStyle(fontWeight: FontWeight.bold)),
                            const SizedBox(height: 6),
                            for (final i in (p['items'] as List).cast<Map<String, dynamic>>())
                              Padding(
                                padding: const EdgeInsets.symmetric(vertical: 2),
                                child: Row(children: [
                                  Expanded(child: Text('${_qty.format(i['quantity'] as num)} x ${i['productName']}')),
                                  Text(_brl(i['totalCents'] as num)),
                                ]),
                              ),
                          ]),
                        ),
                      ),
                  ],
                  if (openTitles.isNotEmpty) ...[
                    Padding(padding: const EdgeInsets.fromLTRB(8, 16, 8, 4), child: Text('Contas em aberto', style: TextStyle(color: cs.primary, fontWeight: FontWeight.bold))),
                    Card(
                      child: Column(children: [
                        for (final t in openTitles)
                          Builder(builder: (_) {
                            final due = DateTime.parse(t['dueDate'] as String);
                            final late = due.isBefore(DateTime(today.year, today.month, today.day));
                            return ListTile(
                              dense: true,
                              title: Text(t['description'] as String),
                              subtitle: Text('Vence ${_date.format(due)}${late ? ' · VENCIDA' : ''}', style: TextStyle(color: late ? cs.error : null)),
                              trailing: Text(_brl(t['amountCents'] as num), style: const TextStyle(fontWeight: FontWeight.bold)),
                            );
                          }),
                      ]),
                    ),
                  ],
                  if (paid.isNotEmpty) ...[
                    Padding(padding: const EdgeInsets.fromLTRB(8, 16, 8, 4), child: Text('Pagamentos recentes', style: TextStyle(color: cs.primary, fontWeight: FontWeight.bold))),
                    Card(
                      child: Column(children: [
                        for (final t in paid)
                          ListTile(
                            dense: true,
                            title: Text(t['description'] as String),
                            subtitle: Text(t['paidOn'] == null ? 'Pago' : 'Pago em ${_date.format(DateTime.parse(t['paidOn'] as String))}'),
                            trailing: Text(_brl((t['paidAmountCents'] ?? t['amountCents']) as num)),
                          ),
                      ]),
                    ),
                  ],
                  if ((s['openCents'] as num) <= 0 && purchases.isEmpty)
                    const Padding(padding: EdgeInsets.all(24), child: Text('Este cliente não está devendo nada no fiado.', textAlign: TextAlign.center)),
                ],
              ]),
            ),
    );
  }
}

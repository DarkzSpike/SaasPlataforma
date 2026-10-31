import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../core/api_client.dart';
import '../core/session.dart';

final _money = NumberFormat.simpleCurrency(locale: 'pt_BR');
final _date = DateFormat('dd/MM/yyyy');
String _brl(num cents) => _money.format(cents / 100);
String _iso(DateTime d) => DateFormat('yyyy-MM-dd').format(d);

/// Financeiro no celular: resumo do mês, contas em aberto (pagar/receber) e lançamento de gastos.
/// Os lançamentos vão direto para o servidor, então aparecem na hora no Financeiro do caixa.
class FinancePage extends StatefulWidget {
  const FinancePage({super.key, required this.api, required this.me});
  final ApiClient api;
  final Me me;
  @override
  State<FinancePage> createState() => _FinancePageState();
}

class _FinancePageState extends State<FinancePage> {
  DateTime month = DateTime(DateTime.now().year, DateTime.now().month);
  Map<String, dynamic>? summary;
  List<Map<String, dynamic>> open = [];
  List<Map<String, dynamic>> paid = [];
  List<String> categories = [];
  String? error;
  bool loading = true;

  Me get me => widget.me;
  bool get _titles => me.modules.contains('contas');
  bool get _canManage => me.can('finance.manage');
  bool get _canExpense => _canManage && me.modules.contains('despesas');

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() => loading = true);
    final from = month;
    final to = DateTime(month.year, month.month + 1, 0);
    try {
      final s = await widget.api.get('/api/v1/finance/summary?from=${_iso(from)}&to=${_iso(to)}');
      final c = await widget.api.get('/api/v1/finance/categories');
      var o = <Map<String, dynamic>>[];
      var p = <Map<String, dynamic>>[];
      if (_titles) {
        // Em aberto: todos (inclusive vencidos de meses anteriores); pagos: os do mês escolhido.
        o = (await widget.api.getList('/api/v1/finance/titles?status=1')).cast<Map<String, dynamic>>();
        p = (await widget.api.getList('/api/v1/finance/titles?status=2&from=${_iso(DateTime(from.year - 1, from.month))}'))
            .cast<Map<String, dynamic>>()
            .where((t) => t['paidOn'] != null && !DateTime.parse(t['paidOn'] as String).isBefore(from) && !DateTime.parse(t['paidOn'] as String).isAfter(to))
            .toList()
          ..sort((a, b) => (b['paidOn'] as String).compareTo(a['paidOn'] as String));
      }
      setState(() {
        summary = s;
        categories = (c['expense'] as List).cast<String>();
        open = o;
        paid = p;
        error = null;
      });
    } catch (e) {
      setState(() => error = e is ApiException ? e.message : 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => loading = false);
    }
  }

  void _snack(String m) {
    if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(m)));
  }

  Future<void> _newEntry({required bool expense}) async {
    final ok = await Navigator.push<bool>(
        context, MaterialPageRoute(builder: (_) => _EntryForm(api: widget.api, categories: categories, expense: expense)));
    if (ok == true) {
      await _load();
      _snack(expense ? 'Gasto lançado. Já aparece no Financeiro do caixa.' : 'Conta a pagar lançada.');
    }
  }

  Future<void> _pay(Map<String, dynamic> t) async {
    final receivable = t['kind'] == 2;
    final method = await showDialog<String>(
        context: context,
        builder: (c) => SimpleDialog(title: Text(receivable ? 'Recebido como?' : 'Pago como?'), children: [
              for (final m in _methods) ListTile(title: Text(m), onTap: () => Navigator.pop(c, m)),
            ]));
    if (method == null) return;
    try {
      await widget.api.post('/api/v1/finance/titles/${t['id']}/pay', {'method': method});
      await _load();
      _snack(receivable ? 'Recebimento registrado.' : 'Pagamento registrado.');
    } on ApiException catch (e) {
      _snack(e.message);
    } catch (_) {
      _snack('Sem conexão com o servidor.');
    }
  }

  void _addMenu() {
    showModalBottomSheet<void>(
        context: context,
        showDragHandle: true,
        builder: (c) => SafeArea(
                child: Column(mainAxisSize: MainAxisSize.min, children: [
              if (_canExpense)
                ListTile(
                    leading: const Icon(Icons.payments_outlined),
                    title: const Text('Gasto já pago'),
                    subtitle: const Text('Ex.: conta de luz paga hoje'),
                    onTap: () {
                      Navigator.pop(c);
                      _newEntry(expense: true);
                    }),
              if (_titles)
                ListTile(
                    leading: const Icon(Icons.event_outlined),
                    title: const Text('Conta a pagar'),
                    subtitle: const Text('Ex.: boleto que vence dia 10'),
                    onTap: () {
                      Navigator.pop(c);
                      _newEntry(expense: false);
                    }),
            ])));
  }

  @override
  Widget build(BuildContext context) {
    final s = summary;
    final scheme = Theme.of(context).colorScheme;
    final title = '${_months[month.month - 1]} de ${month.year}';
    return Scaffold(
      appBar: AppBar(title: const Text('Financeiro'), actions: [IconButton(onPressed: _load, icon: const Icon(Icons.refresh))]),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(padding: const EdgeInsets.fromLTRB(12, 8, 12, 160), children: [
          Row(children: [
            IconButton(onPressed: () { month = DateTime(month.year, month.month - 1); _load(); }, icon: const Icon(Icons.chevron_left)),
            Expanded(child: Text(title, textAlign: TextAlign.center, style: Theme.of(context).textTheme.titleMedium)),
            IconButton(onPressed: () { month = DateTime(month.year, month.month + 1); _load(); }, icon: const Icon(Icons.chevron_right)),
          ]),
          if (error != null) Card(child: ListTile(leading: const Icon(Icons.warning_amber), title: Text(error!))),
          if (loading && s == null) const Padding(padding: EdgeInsets.all(32), child: Center(child: CircularProgressIndicator())),
          if (s != null) ...[
            Row(children: [
              _Tile(label: 'Pago no mês', value: _brl(s['paidCents'] as num), color: scheme.error),
              _Tile(label: 'Recebido no mês', value: _brl(s['receivedCents'] as num), color: Colors.green),
            ]),
            Row(children: [
              _Tile(label: 'A pagar (no mês)', value: _brl(s['openPayableCents'] as num)),
              _Tile(label: 'A receber (no mês)', value: _brl(s['openReceivableCents'] as num)),
            ]),
            if ((s['overdueCount'] as num) > 0)
              Card(
                  color: scheme.error.withValues(alpha: 0.15),
                  child: ListTile(
                    leading: Icon(Icons.warning_amber, color: scheme.error),
                    title: Text('${s['overdueCount']} conta(s) vencida(s)'),
                    subtitle: Text('A pagar ${_brl(s['overduePayableCents'] as num)} • a receber ${_brl(s['overdueReceivableCents'] as num)}'),
                  )),
            if ((s['expensesByCategory'] as List).isNotEmpty) ...[
              const _Header('Gastos por categoria'),
              Card(
                  child: Column(children: [
                for (final c in (s['expensesByCategory'] as List).cast<Map<String, dynamic>>())
                  ListTile(dense: true, title: Text(c['category'] as String), trailing: Text(_brl(c['cents'] as num), style: const TextStyle(fontWeight: FontWeight.w600))),
              ])),
            ],
          ],
          if (_titles && open.isNotEmpty) ...[
            const _Header('Contas em aberto'),
            for (final t in open)
              Card(
                  child: ListTile(
                leading: Icon(t['kind'] == 2 ? Icons.call_received : Icons.call_made, color: t['kind'] == 2 ? Colors.green : scheme.error),
                title: Text(t['description'] as String),
                subtitle: Text('${t['category']} • vence ${_date.format(DateTime.parse(t['dueDate'] as String))}'
                    '${(t['installmentCount'] as num) > 1 ? ' • parcela ${t['installmentNumber']}/${t['installmentCount']}' : ''}',
                    style: DateTime.parse(t['dueDate'] as String).isBefore(DateTime.now().subtract(const Duration(days: 1))) ? TextStyle(color: scheme.error) : null),
                trailing: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.end, children: [
                  Text(_brl(t['amountCents'] as num), style: const TextStyle(fontWeight: FontWeight.bold)),
                  if (_canManage)
                    InkWell(onTap: () => _pay(t), child: Padding(padding: const EdgeInsets.only(top: 4),
                        child: Text(t['kind'] == 2 ? 'Receber' : 'Pagar', style: TextStyle(color: scheme.primary, fontWeight: FontWeight.w600)))),
                ]),
              )),
          ],
          if (_titles && paid.isNotEmpty) ...[
            const _Header('Pagos e recebidos no mês'),
            Card(
                child: Column(children: [
              for (final t in paid.take(60))
                ListTile(
                  dense: true,
                  leading: Icon(t['kind'] == 2 ? Icons.call_received : Icons.call_made, size: 20, color: t['kind'] == 2 ? Colors.green : scheme.error),
                  title: Text(t['description'] as String),
                  subtitle: Text('${t['category']} • ${_date.format(DateTime.parse(t['paidOn'] as String))}${t['paymentMethod'] != null ? ' • ${t['paymentMethod']}' : ''}'),
                  trailing: Text(_brl((t['paidAmountCents'] ?? t['amountCents']) as num)),
                ),
            ])),
          ],
          if (s != null)
            Padding(padding: const EdgeInsets.all(8), child: Text(s['note'] as String? ?? '', style: Theme.of(context).textTheme.bodySmall)),
        ]),
      ),
      floatingActionButtonLocation: FloatingActionButtonLocation.startFloat,
      floatingActionButton: !(_canExpense || (_titles && _canManage))
          ? null
          : FloatingActionButton.extended(heroTag: 'novo-gasto', onPressed: _addMenu, icon: const Icon(Icons.add), label: const Text('Lançar gasto')),
    );
  }
}

const _months = ['Janeiro', 'Fevereiro', 'Março', 'Abril', 'Maio', 'Junho', 'Julho', 'Agosto', 'Setembro', 'Outubro', 'Novembro', 'Dezembro'];
const _methods = ['PIX', 'Dinheiro', 'Cartão', 'Boleto', 'Transferência'];

class _Tile extends StatelessWidget {
  const _Tile({required this.label, required this.value, this.color});
  final String label;
  final String value;
  final Color? color;
  @override
  Widget build(BuildContext context) => Expanded(
      child: Card(
          child: Padding(
              padding: const EdgeInsets.all(12),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(label, style: Theme.of(context).textTheme.bodySmall),
                const SizedBox(height: 4),
                FittedBox(child: Text(value, style: Theme.of(context).textTheme.titleLarge?.copyWith(fontWeight: FontWeight.bold, color: color))),
              ]))));
}

class _Header extends StatelessWidget {
  const _Header(this.text);
  final String text;
  @override
  Widget build(BuildContext context) =>
      Padding(padding: const EdgeInsets.fromLTRB(4, 16, 4, 6), child: Text(text, style: Theme.of(context).textTheme.titleSmall?.copyWith(fontWeight: FontWeight.bold)));
}

/// Gasto já pago (vai para /finance/expenses) ou conta a pagar com vencimento (/finance/titles).
class _EntryForm extends StatefulWidget {
  const _EntryForm({required this.api, required this.categories, required this.expense});
  final ApiClient api;
  final List<String> categories;
  final bool expense;
  @override
  State<_EntryForm> createState() => _EntryFormState();
}

class _EntryFormState extends State<_EntryForm> {
  final description = TextEditingController();
  final amount = TextEditingController();
  final installments = TextEditingController(text: '1');
  late String category = widget.categories.contains('Outras despesas') ? 'Outras despesas' : widget.categories.first;
  String method = 'PIX';
  DateTime day = DateTime.now();
  bool busy = false;
  String? error;

  static int? _cents(String s) {
    final t = s.replaceAll('R\$', '').replaceAll(' ', '').replaceAll('.', '').replaceAll(',', '.');
    final v = double.tryParse(t);
    return v == null || v <= 0 ? null : (v * 100).round();
  }

  Future<void> _pickDay() async {
    final d = await showDatePicker(context: context, initialDate: day, firstDate: DateTime(2020), lastDate: DateTime(DateTime.now().year + 5));
    if (d != null) setState(() => day = d);
  }

  Future<void> _save() async {
    final cents = _cents(amount.text);
    if (description.text.trim().isEmpty) return setState(() => error = 'Descreva o gasto (ex.: Conta de luz).');
    if (cents == null) return setState(() => error = 'Valor inválido (ex.: 150,00).');
    final n = int.tryParse(installments.text.trim()) ?? 0;
    if (!widget.expense && (n < 1 || n > 60)) return setState(() => error = 'Parcelas: de 1 a 60.');
    setState(() {
      busy = true;
      error = null;
    });
    try {
      if (widget.expense) {
        await widget.api.post('/api/v1/finance/expenses',
            {'description': description.text.trim(), 'category': category, 'amountCents': cents, 'paidOn': _iso(day), 'method': method});
      } else {
        await widget.api.post('/api/v1/finance/titles', {
          'kind': 1, 'description': description.text.trim(), 'category': category, 'firstDueDate': _iso(day), 'totalCents': cents, 'installments': n,
        });
      }
      if (mounted) Navigator.pop(context, true);
    } on ApiException catch (e) {
      setState(() => error = e.message);
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: Text(widget.expense ? 'Lançar gasto' : 'Conta a pagar')),
        body: ListView(padding: const EdgeInsets.all(16), children: [
          TextField(controller: description, textCapitalization: TextCapitalization.sentences, decoration: const InputDecoration(labelText: 'Descrição', hintText: 'Ex.: Conta de luz')),
          const SizedBox(height: 12),
          TextField(
              controller: amount,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              decoration: InputDecoration(labelText: widget.expense ? 'Valor pago (R\$)' : 'Valor total (R\$)', hintText: '0,00')),
          const SizedBox(height: 12),
          DropdownButtonFormField<String>(
            initialValue: category,
            decoration: const InputDecoration(labelText: 'Categoria'),
            items: [for (final c in widget.categories) DropdownMenuItem(value: c, child: Text(c))],
            onChanged: (v) => setState(() => category = v ?? category),
          ),
          const SizedBox(height: 12),
          if (widget.expense)
            DropdownButtonFormField<String>(
              initialValue: method,
              decoration: const InputDecoration(labelText: 'Forma de pagamento'),
              items: [for (final m in _methods) DropdownMenuItem(value: m, child: Text(m))],
              onChanged: (v) => setState(() => method = v ?? method),
            )
          else
            TextField(controller: installments, keyboardType: TextInputType.number, decoration: const InputDecoration(labelText: 'Parcelas', helperText: '1 = à vista; 3 = três parcelas mensais')),
          const SizedBox(height: 12),
          OutlinedButton.icon(
              onPressed: _pickDay,
              icon: const Icon(Icons.calendar_today_outlined),
              label: Text('${widget.expense ? 'Pago em' : 'Vence em'} ${_date.format(day)}')),
          if (error != null) Padding(padding: const EdgeInsets.only(top: 12), child: Text(error!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
          const SizedBox(height: 20),
          FilledButton.icon(onPressed: busy ? null : _save, icon: const Icon(Icons.check), label: Text(busy ? 'Salvando...' : 'Salvar')),
        ]),
      );
}

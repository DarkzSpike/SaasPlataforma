import 'package:flutter/material.dart';
import 'package:intl/intl.dart';
import 'package:url_launcher/url_launcher.dart';

import '../core/api_client.dart';

final _when = DateFormat('dd/MM/yyyy HH:mm');

/// Promoções grátis pelo WhatsApp da loja: o dono agenda, o app avisa na hora e abre o WhatsApp para cada cliente
/// com a mensagem pronta. Só recebe quem aceitou ofertas; os limites anti-bloqueio são do servidor.
class PromosPage extends StatefulWidget {
  const PromosPage({super.key, required this.api});
  final ApiClient api;
  @override
  State<PromosPage> createState() => _PromosPageState();
}

class _PromosPageState extends State<PromosPage> {
  Map<String, dynamic>? data;
  String? error;
  bool loading = true;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() => loading = true);
    try {
      final d = await widget.api.get('/api/v1/whatsapp/campaigns');
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

  Future<void> _new() async {
    final ok = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => _NewPromoPage(api: widget.api)));
    if (ok == true) _load();
  }

  static String _status(Map<String, dynamic> c) => switch (c['status']) {
        'scheduled' => 'Agendada',
        'sending' => c['manual'] == true ? 'Na hora de mandar' : 'Enviando',
        'done' => 'Concluída',
        'cancelled' => 'Cancelada',
        _ => 'Parada',
      };

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final camps = (data?['campaigns'] as List? ?? []).cast<Map<String, dynamic>>();
    final now = DateTime.now();
    return Scaffold(
      appBar: AppBar(title: const Text('Promoções pelo WhatsApp')),
      floatingActionButton: FloatingActionButton.extended(onPressed: _new, icon: const Icon(Icons.add), label: const Text('Nova promoção')),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(padding: const EdgeInsets.fromLTRB(12, 8, 12, 96), children: [
          if (loading && data == null) const Padding(padding: EdgeInsets.all(48), child: Center(child: CircularProgressIndicator())),
          if (error != null) Card(child: ListTile(leading: Icon(Icons.warning_amber, color: cs.error), title: Text(error!))),
          if (data != null)
            Card(
              child: ListTile(
                leading: const Icon(Icons.shield_outlined, color: Color(0xFF25D366)),
                title: Text('Hoje: ${data!['sentTodayManual'] ?? 0} de ${data!['manualDailyLimit'] ?? 50} mandadas'),
                subtitle: Text('${data!['eligibleNow']} cliente(s) podem receber agora. Cada um recebe no máximo 1 promoção a cada '
                    '${data!['intervalDays']} dias e só quem aceitou ofertas. Na hora marcada o app avisa você.'),
              ),
            ),
          for (final c in camps)
            Builder(builder: (_) {
              final when = DateTime.parse(c['scheduledAt'] as String).toLocal();
              final canSend = c['manual'] == true && (c['status'] == 'sending' || (c['status'] == 'scheduled' && !when.isAfter(now)));
              return Card(
                child: ListTile(
                  title: Text(c['title'] as String, style: const TextStyle(fontWeight: FontWeight.bold)),
                  subtitle: Text('${_when.format(when)} • ${c['manual'] == true ? 'Grátis' : 'Automática'} • ${_status(c)}\n'
                      '${c['sent']} mandada(s) • ${c['message']}'),
                  isThreeLine: true,
                  trailing: canSend
                      ? FilledButton(
                          style: FilledButton.styleFrom(backgroundColor: const Color(0xFF25D366)),
                          onPressed: () async {
                            await Navigator.push(context, MaterialPageRoute(builder: (_) => _QueuePage(api: widget.api, id: c['id'] as String, title: c['title'] as String)));
                            _load();
                          },
                          child: const Text('Mandar'),
                        )
                      : null,
                ),
              );
            }),
          if (data != null && camps.isEmpty)
            const Padding(padding: EdgeInsets.all(32), child: Text('Nenhuma promoção ainda. Toque em "Nova promoção".', textAlign: TextAlign.center)),
        ]),
      ),
    );
  }
}

class _NewPromoPage extends StatefulWidget {
  const _NewPromoPage({required this.api});
  final ApiClient api;
  @override
  State<_NewPromoPage> createState() => _NewPromoPageState();
}

class _NewPromoPageState extends State<_NewPromoPage> {
  final title = TextEditingController();
  final message = TextEditingController();
  DateTime when = DateTime.now();
  bool busy = false;
  String? error;

  Future<void> _pick() async {
    final d = await showDatePicker(context: context, initialDate: when, firstDate: DateTime.now().subtract(const Duration(days: 1)), lastDate: DateTime.now().add(const Duration(days: 60)));
    if (d == null || !mounted) return;
    final t = await showTimePicker(context: context, initialTime: TimeOfDay.fromDateTime(when));
    if (t == null) return;
    setState(() => when = DateTime(d.year, d.month, d.day, t.hour, t.minute));
  }

  Future<void> _save() async {
    setState(() {
      busy = true;
      error = null;
    });
    try {
      await widget.api.post('/api/v1/whatsapp/campaigns', {
        'title': title.text.trim(),
        'message': message.text.trim(),
        // "Agora" vai com um minuto de folga para trás (o relógio do celular pode estar adiantado).
        'scheduledAt': (when.isBefore(DateTime.now()) ? DateTime.now().subtract(const Duration(minutes: 1)) : when).toUtc().toIso8601String(),
        'manual': true,
      });
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
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Nova promoção')),
      body: ListView(padding: const EdgeInsets.all(16), children: [
        const Text('Vai assim: "Olá, [nome]! Novidade da [loja]: [sua mensagem]". Seja direto e use no máximo um link. '
            'Na hora marcada o app avisa e você manda pelo WhatsApp da loja, um cliente por vez (grátis).'),
        const SizedBox(height: 16),
        TextField(controller: title, decoration: const InputDecoration(labelText: 'Nome (só para você)', hintText: 'ex.: Promoção de sábado', border: OutlineInputBorder())),
        const SizedBox(height: 12),
        TextField(
          controller: message,
          maxLines: 4,
          maxLength: 500,
          decoration: const InputDecoration(labelText: 'Mensagem', hintText: 'ex.: Tudo com 10% de desconto neste sábado!', border: OutlineInputBorder()),
        ),
        ListTile(
          contentPadding: EdgeInsets.zero,
          leading: const Icon(Icons.schedule),
          title: Text(when.isBefore(DateTime.now().add(const Duration(minutes: 1))) ? 'Agora' : _when.format(when)),
          subtitle: const Text('Toque para escolher outro dia e hora'),
          onTap: _pick,
        ),
        if (error != null) Padding(padding: const EdgeInsets.only(top: 8), child: Text(error!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
        const SizedBox(height: 16),
        FilledButton.icon(onPressed: busy ? null : _save, icon: const Icon(Icons.check), label: Text(busy ? 'Salvando...' : 'Agendar')),
      ]),
    );
  }
}

/// Fila de uma promoção grátis: um botão por cliente, que abre o WhatsApp da loja com a mensagem pronta.
class _QueuePage extends StatefulWidget {
  const _QueuePage({required this.api, required this.id, required this.title});
  final ApiClient api;
  final String id, title;
  @override
  State<_QueuePage> createState() => _QueuePageState();
}

class _QueuePageState extends State<_QueuePage> {
  Map<String, dynamic>? q;
  final Set<String> opened = {};
  String? error;
  bool loading = true;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final d = await widget.api.get('/api/v1/whatsapp/campaigns/${widget.id}/manual-queue');
      setState(() {
        q = d;
        error = null;
      });
    } catch (e) {
      setState(() => error = e is ApiException ? e.message : 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => loading = false);
    }
  }

  Future<void> _send(Map<String, dynamic> c) async {
    if (!await launchUrl(Uri.parse(c['link'] as String), mode: LaunchMode.externalApplication)) {
      setState(() => error = 'Não consegui abrir o WhatsApp neste celular.');
      return;
    }
    try {
      await widget.api.post('/api/v1/whatsapp/campaigns/${widget.id}/manual-sent', {'customerId': c['customerId']});
      setState(() => opened.add(c['customerId'] as String));
    } on ApiException catch (e) {
      setState(() => error = e.message);
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    }
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final queue = (q?['queue'] as List? ?? []).cast<Map<String, dynamic>>();
    final left = queue.where((c) => !opened.contains(c['customerId'])).length;
    return Scaffold(
      appBar: AppBar(title: Text(widget.title)),
      body: loading
          ? const Center(child: CircularProgressIndicator())
          : ListView(padding: const EdgeInsets.fromLTRB(12, 8, 12, 32), children: [
              if (error != null) Card(child: ListTile(leading: Icon(Icons.warning_amber, color: cs.error), title: Text(error!))),
              if (q != null)
                Card(
                  child: ListTile(
                    leading: const Icon(Icons.info_outline),
                    title: Text(queue.isEmpty ? 'Ninguém para mandar agora' : 'Faltam $left de ${queue.length} hoje'),
                    subtitle: Text(queue.isEmpty && (q!['remaining'] as num) > 0
                        ? 'Limite de hoje atingido (${q!['dailyLimit']} por dia, para o número não ser bloqueado). Continue amanhã.'
                        : 'Toque em Mandar: o WhatsApp abre com a mensagem pronta. Aperte Enviar e volte aqui para o próximo.'),
                  ),
                ),
              for (final c in queue)
                Card(
                  child: ListTile(
                    title: Text(c['name'] as String),
                    trailing: opened.contains(c['customerId'])
                        ? const Icon(Icons.check_circle, color: Color(0xFF25D366))
                        : FilledButton(
                            style: FilledButton.styleFrom(backgroundColor: const Color(0xFF25D366)),
                            onPressed: () => _send(c),
                            child: const Text('Mandar'),
                          ),
                  ),
                ),
            ]),
    );
  }
}

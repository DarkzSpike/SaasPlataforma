import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intl/intl.dart';
import 'package:url_launcher/url_launcher.dart';

import '../core/api_client.dart';

final _money = NumberFormat.simpleCurrency(locale: 'pt_BR');
final _date = DateFormat('dd/MM/yyyy');

/// Situação da fatura em palavras (mesma regra do caixa).
String invoiceStatus(Map<String, dynamic> i) {
  final s = i['status'] as String;
  if (s == 'paga') return 'Paga';
  if (s != 'aberta') return 'Cancelada';
  if (i['overdue'] == true) return 'Vencida';
  final d = (i['daysToDue'] as num).toInt();
  return d <= 0 ? 'Vence hoje' : d <= 3 ? 'Vence em $d dia(s)' : 'Em aberto';
}

/// Faturas do sistema (mensalidade): pagar por PIX, cartão ou boleto pelo celular.
class InvoicesPage extends StatefulWidget {
  const InvoicesPage({super.key, required this.api});
  final ApiClient api;
  @override
  State<InvoicesPage> createState() => _InvoicesPageState();
}

class _InvoicesPageState extends State<InvoicesPage> {
  List<Map<String, dynamic>> items = [];
  String? error;
  bool loading = true;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final rows = await widget.api.getList('/api/v1/billing/invoices');
      setState(() { items = rows.cast<Map<String, dynamic>>(); error = null; });
    } on ApiException catch (e) {
      setState(() => error = e.code == 'permission_denied' ? 'Seu usuário não pode ver as faturas do sistema.' : e.message);
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final next = items.where((i) => i['status'] == 'aberta').toList()
      ..sort((a, b) => (a['dueAt'] as String).compareTo(b['dueAt'] as String));
    return Scaffold(
      appBar: AppBar(title: const Text('Faturas'), actions: [IconButton(onPressed: _load, icon: const Icon(Icons.refresh))]),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(padding: const EdgeInsets.fromLTRB(12, 8, 12, 96), children: [
          if (error != null) Card(child: ListTile(leading: const Icon(Icons.warning_amber), title: Text(error!))),
          if (loading) const Padding(padding: EdgeInsets.all(32), child: Center(child: CircularProgressIndicator())),
          if (next.isNotEmpty && (next.first['overdue'] == true || (next.first['daysToDue'] as num) <= 3))
            Card(
              color: next.first['overdue'] == true ? cs.errorContainer : cs.tertiaryContainer,
              child: ListTile(
                leading: const Icon(Icons.priority_high),
                title: Text(next.first['overdue'] == true
                    ? 'Fatura vencida de ${_money.format((next.first['amountCents'] as num) / 100)}. Pague para evitar o bloqueio do sistema.'
                    : 'Sua fatura de ${_money.format((next.first['amountCents'] as num) / 100)} ${invoiceStatus(next.first).toLowerCase()}.'),
              ),
            ),
          if (!loading && items.isEmpty && error == null) const Card(child: ListTile(leading: Icon(Icons.receipt_long), title: Text('Nenhuma fatura.'))),
          for (final i in items)
            Card(
              child: ListTile(
                leading: Icon(i['status'] == 'paga' ? Icons.check_circle : i['status'] == 'aberta' ? Icons.receipt_long : Icons.block,
                    color: i['status'] == 'paga' ? Colors.green : i['overdue'] == true ? cs.error : null),
                title: Text(_money.format((i['amountCents'] as num) / 100), style: const TextStyle(fontWeight: FontWeight.bold)),
                subtitle: Text('Vence ${_date.format(DateTime.parse(i['dueAt'] as String).toLocal())} • ${invoiceStatus(i)}'
                    '${i['paidMethod'] != null ? ' (${i['paidMethod']})' : ''}'),
                trailing: i['status'] == 'aberta'
                    ? FilledButton(onPressed: () async { await showInvoicePay(context, widget.api, i['id'] as String); _load(); }, child: const Text('Pagar'))
                    : null,
              ),
            ),
        ]),
      ),
    );
  }
}

/// Abre o pagamento de uma fatura (PIX na hora, cartão/boleto, "Já paguei"). Usado em Faturas e nos Avisos.
Future<void> showInvoicePay(BuildContext context, ApiClient api, String? invoiceId) => showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => _InvoicePaySheet(api: api, invoiceId: invoiceId),
    );

class _InvoicePaySheet extends StatefulWidget {
  const _InvoicePaySheet({required this.api, required this.invoiceId});
  final ApiClient api;
  final String? invoiceId;
  @override
  State<_InvoicePaySheet> createState() => _InvoicePaySheetState();
}

class _InvoicePaySheetState extends State<_InvoicePaySheet> {
  Map<String, dynamic>? inv;
  String? error;
  String? info;
  bool busy = true;

  @override
  void initState() {
    super.initState();
    _start();
  }

  String _msg(ApiException e) => switch (e.code) {
        'payments_not_configured' => 'O pagamento online ainda não foi configurado pelo fornecedor do sistema. Fale com o suporte.',
        'payment_provider_error' => 'O Mercado Pago não respondeu. Tente de novo em instantes.',
        'invoice_not_open' => 'Esta fatura já foi paga ou cancelada.',
        'permission_denied' => 'Seu usuário não pode pagar as faturas do sistema.',
        _ => e.message,
      };

  Future<void> _start() async {
    try {
      final rows = (await widget.api.getList('/api/v1/billing/invoices')).cast<Map<String, dynamic>>();
      var i = rows.where((x) => x['id'] == widget.invoiceId).firstOrNull ?? rows.where((x) => x['status'] == 'aberta').firstOrNull;
      if (i == null) { setState(() { error = 'Fatura não encontrada.'; busy = false; }); return; }
      // Gera o PIX na hora (ou reaproveita o que ainda vale).
      if (i['status'] == 'aberta' && i['pixCopyPaste'] == null) {
        i = await widget.api.post('/api/v1/billing/invoices/${i['id']}/pix', {}) as Map<String, dynamic>;
      }
      setState(() { inv = i; busy = false; });
    } on ApiException catch (e) {
      setState(() { error = _msg(e); busy = false; });
    } catch (_) {
      setState(() { error = 'Sem conexão com o servidor.'; busy = false; });
    }
  }

  Future<void> _copy() async {
    await Clipboard.setData(ClipboardData(text: inv!['pixCopyPaste'] as String));
    setState(() => info = 'Código PIX copiado. Cole no app do seu banco (PIX copia e cola).');
  }

  Future<void> _checkout() async {
    setState(() { busy = true; error = null; });
    try {
      final r = await widget.api.post('/api/v1/billing/invoices/${inv!['id']}/checkout', {}) as Map<String, dynamic>;
      final url = Uri.tryParse(r['checkoutUrl'] as String? ?? '');
      // Só abre página do próprio Mercado Pago (o servidor também confere).
      if (url == null || url.scheme != 'https' || !(url.host.endsWith('mercadopago.com.br') || url.host.endsWith('mercadopago.com'))) {
        setState(() => error = 'Link de pagamento inválido.');
      } else {
        await launchUrl(url, mode: LaunchMode.externalApplication);
        setState(() => info = 'Abrimos a página segura do Mercado Pago. Depois de pagar, toque em "Já paguei".');
      }
    } on ApiException catch (e) {
      setState(() => error = _msg(e));
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  Future<void> _refresh() async {
    setState(() { busy = true; error = null; });
    try {
      final r = await widget.api.post('/api/v1/billing/invoices/${inv!['id']}/refresh', {}) as Map<String, dynamic>;
      setState(() {
        inv = r;
        info = r['status'] == 'paga' ? 'Pagamento confirmado. Obrigado!' : 'Ainda não identificamos o pagamento. PIX costuma confirmar em segundos; boleto pode levar até 3 dias úteis.';
      });
    } on ApiException catch (e) {
      setState(() => error = _msg(e));
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final i = inv;
    Uint8List? qr;
    if (i?['pixQrBase64'] is String) {
      try { qr = base64Decode(i!['pixQrBase64'] as String); } catch (_) {}
    }
    final open = i?['status'] == 'aberta';
    return Padding(
      padding: EdgeInsets.fromLTRB(20, 8, 20, 20 + MediaQuery.of(context).viewInsets.bottom),
      child: SingleChildScrollView(
        child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Row(children: [
            Expanded(child: Text(i == null ? 'Pagar fatura' : 'Pagar fatura: ${_money.format((i['amountCents'] as num) / 100)}',
                style: Theme.of(context).textTheme.titleLarge)),
            IconButton(onPressed: () => Navigator.pop(context), icon: const Icon(Icons.close)),
          ]),
          if (i != null) Text('Vencimento ${_date.format(DateTime.parse(i['dueAt'] as String).toLocal())} • ${invoiceStatus(i)}'),
          if (busy) const Padding(padding: EdgeInsets.all(24), child: Center(child: CircularProgressIndicator())),
          if (i != null && i['status'] == 'paga')
            const Card(child: ListTile(leading: Icon(Icons.check_circle, color: Colors.green), title: Text('Fatura paga. Obrigado!'))),
          if (open && qr != null) ...[
            const SizedBox(height: 12),
            Center(child: Container(color: Colors.white, padding: const EdgeInsets.all(8), child: Image.memory(qr, width: 220, height: 220, filterQuality: FilterQuality.none))),
            const SizedBox(height: 8),
            const Text('Abra o app do seu banco, escolha PIX e leia o QR Code. Pagando por este celular? Use "Copiar código PIX" e cole no app do banco.', textAlign: TextAlign.center),
            if (i!['pixExpiresAt'] != null)
              Text('Válido até ${DateFormat('dd/MM HH:mm').format(DateTime.parse(i['pixExpiresAt'] as String).toLocal())}',
                  textAlign: TextAlign.center, style: Theme.of(context).textTheme.bodySmall),
            const SizedBox(height: 12),
            FilledButton.icon(onPressed: _copy, icon: const Icon(Icons.copy), label: const Text('Copiar código PIX')),
          ],
          if (open) ...[
            const SizedBox(height: 8),
            OutlinedButton.icon(onPressed: busy ? null : _checkout, icon: const Icon(Icons.credit_card), label: const Text('Cartão ou boleto')),
            const SizedBox(height: 8),
            OutlinedButton.icon(onPressed: busy ? null : _refresh, icon: const Icon(Icons.check), label: const Text('Já paguei')),
          ],
          if (info != null) Padding(padding: const EdgeInsets.only(top: 12), child: Text(info!, style: const TextStyle(color: Colors.green))),
          if (error != null) Padding(padding: const EdgeInsets.only(top: 12), child: Text(error!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
        ]),
      ),
    );
  }
}

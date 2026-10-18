import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../core/api_client.dart';

/// Avisos para o dono (ex.: quebra de caixa). Também chegam por push quando o Firebase está configurado.
class NotificationsPage extends StatefulWidget {
  const NotificationsPage({super.key, required this.api});
  final ApiClient api;
  @override
  State<NotificationsPage> createState() => _NotificationsPageState();
}

class _NotificationsPageState extends State<NotificationsPage> {
  List<dynamic> items = [];
  String? error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final rows = await widget.api.getList('/api/v1/notifications');
      setState(() { items = rows; error = null; });
    } catch (_) {
      setState(() => error = 'Não foi possível carregar os avisos.');
    }
  }

  Future<void> _read(Map<String, dynamic> n) async {
    if (n['readAt'] != null) return;
    try {
      await widget.api.post('/api/v1/notifications/${n['id']}/read', {});
      await _load();
    } catch (_) {}
  }

  @override
  Widget build(BuildContext context) {
    final fmt = DateFormat('dd/MM HH:mm');
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView(padding: const EdgeInsets.all(12), children: [
        if (error != null) Card(child: ListTile(leading: const Icon(Icons.warning_amber), title: Text(error!))),
        if (items.isEmpty && error == null) const Card(child: ListTile(leading: Icon(Icons.notifications_none), title: Text('Nenhum aviso.'))),
        for (final n in items.cast<Map<String, dynamic>>())
          Card(
            child: ListTile(
              onTap: () => _read(n),
              leading: Icon(n['type'] == 'cash.shortage' ? Icons.point_of_sale : Icons.notifications,
                  color: n['readAt'] == null ? Theme.of(context).colorScheme.error : null),
              title: Text(n['title'] as String, style: TextStyle(fontWeight: n['readAt'] == null ? FontWeight.bold : FontWeight.normal)),
              subtitle: Text('${n['body']}\n${fmt.format(DateTime.parse(n['createdAt'] as String).toLocal())}'),
              isThreeLine: true,
            ),
          ),
      ]),
    );
  }
}

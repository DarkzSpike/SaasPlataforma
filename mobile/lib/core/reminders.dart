import 'package:flutter/foundation.dart';
import 'package:flutter_local_notifications/flutter_local_notifications.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:timezone/data/latest.dart' as tzdata;
import 'package:timezone/timezone.dart' as tz;

import 'api_client.dart';
import 'session.dart';

/// Avisos no próprio celular (sem Firebase): às 9h de cada dia de cobrança do fiado e na hora de cada promoção
/// grátis, o app lembra o dono de mandar as mensagens pelo WhatsApp da loja. A agenda vem do servidor e é
/// refeita sempre que o app abre ou volta para a tela.
class Reminders {
  static final _plugin = FlutterLocalNotificationsPlugin();
  static const _store = FlutterSecureStorage();
  static bool _ready = false;

  /// Toque num aviso: 'charges' (cobranças) ou 'promo:<id>' (promoção). A tela principal abre a aba Clientes.
  static final tapped = ValueNotifier<String?>(null);

  static const _details = NotificationDetails(
    android: AndroidNotificationDetails('lembretes', 'Lembretes da loja',
        channelDescription: 'Cobranças do fiado e promoções para mandar pelo WhatsApp', importance: Importance.high, priority: Priority.high),
  );

  static Future<void> _init() async {
    if (_ready) return;
    tzdata.initializeTimeZones();
    tz.setLocalLocation(tz.getLocation('America/Sao_Paulo'));
    await _plugin.initialize(
      settings: const InitializationSettings(android: AndroidInitializationSettings('@mipmap/ic_launcher')),
      onDidReceiveNotificationResponse: (r) => tapped.value = r.payload,
    );
    final launch = await _plugin.getNotificationAppLaunchDetails();
    if (launch?.didNotificationLaunchApp == true) tapped.value = launch!.notificationResponse?.payload;
    _ready = true;
  }

  static tz.TZDateTime _at9(DateTime day) => tz.TZDateTime(tz.local, day.year, day.month, day.day, 9);

  static String _names(List<String> names) =>
      names.length <= 3 ? names.join(', ') : '${names.take(3).join(', ')} e mais ${names.length - 3}';

  /// Mostra agora no máximo uma vez por dia (por assunto).
  static Future<void> _showOnce(String key, int id, String title, String body, String payload) async {
    final now = tz.TZDateTime.now(tz.local);
    final today = '${now.year}-${now.month}-${now.day}';
    if (await _store.read(key: 'reminder.$key') == today) return;
    await _plugin.show(id: id, title: title, body: body, notificationDetails: _details, payload: payload);
    await _store.write(key: 'reminder.$key', value: today);
  }

  static Future<void> refresh(ApiClient api, Me me) async {
    if (!me.chargesByWhatsApp && !me.sendsPromos) return;
    try {
      await _init();
      await _plugin.resolvePlatformSpecificImplementation<AndroidFlutterLocalNotificationsPlugin>()?.requestNotificationsPermission();
      await _plugin.cancelAllPendingNotifications();
      final now = tz.TZDateTime.now(tz.local);
      var id = 100;
      if (me.chargesByWhatsApp) {
        final d = await api.get('/api/v1/customers/charges-due');
        if (d['automatic'] != true) {
          final due = (d['due'] as List).cast<Map<String, dynamic>>();
          if (due.isNotEmpty) {
            final body = '${_names([for (final c in due) c['name'] as String])}. Toque para cobrar: a mensagem já vai pronta no WhatsApp da loja.';
            if (now.hour < 9) {
              await _plugin.zonedSchedule(id: id++, scheduledDate: _at9(now), notificationDetails: _details,
                  androidScheduleMode: AndroidScheduleMode.inexactAllowWhileIdle, title: 'Cobranças do fiado para hoje', body: body, payload: 'charges');
            } else if (now.hour < 20) {
              await _showOnce('charges', 1, 'Cobranças do fiado para hoje', body, 'charges');
            }
          }
          for (final u in (d['upcoming'] as List).cast<Map<String, dynamic>>()) {
            final day = DateTime.parse(u['date'] as String);
            final names = (u['names'] as List).cast<String>();
            await _plugin.zonedSchedule(id: id++, scheduledDate: _at9(day), notificationDetails: _details,
                androidScheduleMode: AndroidScheduleMode.inexactAllowWhileIdle, title: 'Cobranças do fiado para hoje',
                body: '${_names(names)}. Toque para cobrar: a mensagem já vai pronta no WhatsApp da loja.', payload: 'charges');
          }
        }
      }
      if (me.sendsPromos) {
        final c = await api.get('/api/v1/whatsapp/campaigns');
        for (final camp in (c['campaigns'] as List).cast<Map<String, dynamic>>()) {
          if (camp['manual'] != true) continue;
          final when = tz.TZDateTime.from(DateTime.parse(camp['scheduledAt'] as String), tz.local);
          const title = 'Hora de mandar a promoção';
          final body = '"${camp['title']}": toque para mandar pelo WhatsApp da loja, com a mensagem pronta.';
          if (camp['status'] == 'scheduled' && when.isAfter(now)) {
            await _plugin.zonedSchedule(id: id++, scheduledDate: when, notificationDetails: _details,
                androidScheduleMode: AndroidScheduleMode.inexactAllowWhileIdle, title: title, body: body, payload: 'promo:${camp['id']}');
          } else if ((camp['status'] == 'sending' || camp['status'] == 'scheduled') && now.hour >= 9 && now.hour < 20) {
            await _showOnce('promo.${camp['id']}', 2, title, body, 'promo:${camp['id']}');
          }
        }
      }
    } catch (_) {
      // Sem permissão de aviso, sem conexão ou módulo desligado: o app segue normal e a lista aparece na aba Clientes.
    }
  }
}

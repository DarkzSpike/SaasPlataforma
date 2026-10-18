import 'dart:io' show Platform;

import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';

import 'api_client.dart';

/// Registra este aparelho para receber push (ex.: aviso de quebra de caixa).
/// Requer o projeto Firebase configurado no app (google-services.json no Android,
/// GoogleService-Info.plist + chave APNs no iOS). Sem isso, o app segue funcionando
/// e os avisos aparecem na aba "Avisos".
class PushRegistration {
  static Future<void> register(ApiClient api) async {
    try {
      await Firebase.initializeApp();
      final messaging = FirebaseMessaging.instance;
      final perm = await messaging.requestPermission(alert: true, badge: true, sound: true);
      if (perm.authorizationStatus == AuthorizationStatus.denied) return;
      final token = await messaging.getToken();
      if (token == null) return;
      await api.post('/api/v1/push/tokens', {'token': token, 'platform': Platform.isIOS ? 'ios' : 'android'});
      messaging.onTokenRefresh.listen((t) => api.post('/api/v1/push/tokens', {'token': t, 'platform': Platform.isIOS ? 'ios' : 'android'}));
    } catch (_) {
      // Firebase não configurado neste build: push desativado, avisos continuam disponíveis no app.
    }
  }
}

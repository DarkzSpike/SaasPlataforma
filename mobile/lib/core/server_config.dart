import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Endereço da API definido no build (padrão do aplicativo).
/// Ex.: flutter build apk --dart-define=API_BASE_URL=https://api.seudominio.com.br
const defaultApiBaseUrl = String.fromEnvironment('API_BASE_URL', defaultValue: 'http://10.0.2.2:5080');

/// Endereço do servidor escolhido no aparelho (tela de login → "Servidor"). Assim, se o IP do computador
/// mudar na rede local, basta corrigir no app — sem gerar outro APK.
class ServerConfig {
  ServerConfig([FlutterSecureStorage? storage])
      : _s = storage ?? const FlutterSecureStorage(aOptions: AndroidOptions(encryptedSharedPreferences: true));
  final FlutterSecureStorage _s;
  static const _key = 'server_url';

  Future<String> read() async => (await _s.read(key: _key)) ?? defaultApiBaseUrl;
  Future<void> save(String url) => _s.write(key: _key, value: normalize(url));

  /// "192.168.0.10:5080" vira "http://192.168.0.10:5080"; remove a barra final.
  static String normalize(String url) {
    var u = url.trim();
    if (u.isEmpty) return defaultApiBaseUrl;
    if (!u.startsWith('http://') && !u.startsWith('https://')) u = 'http://$u';
    while (u.endsWith('/')) {
      u = u.substring(0, u.length - 1);
    }
    return u;
  }

  /// HTTP sem criptografia só é aceito na rede local (localhost, emulador e IPs privados). Na internet, só HTTPS.
  static bool isAllowed(Uri uri) {
    if (uri.scheme == 'https') return true;
    if (uri.scheme != 'http') return false;
    final h = uri.host;
    if (h == 'localhost' || h == '10.0.2.2' || h == '127.0.0.1') return true;
    final p = h.split('.').map(int.tryParse).toList();
    if (p.length != 4 || p.any((x) => x == null || x < 0 || x > 255)) return false;
    final a = p[0]!, b = p[1]!;
    return a == 10 || (a == 192 && b == 168) || (a == 172 && b >= 16 && b <= 31);
  }
}

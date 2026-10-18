import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Credenciais no armazenamento seguro do sistema (Keychain no iOS, Keystore no Android).
class SessionStore {
  SessionStore([FlutterSecureStorage? storage])
      : _s = storage ?? const FlutterSecureStorage(aOptions: AndroidOptions(encryptedSharedPreferences: true));

  final FlutterSecureStorage _s;
  static const _refresh = 'refresh_token';
  static const _tenant = 'tenant_code';

  Future<void> saveRefreshToken(String v) => _s.write(key: _refresh, value: v);
  Future<String?> readRefreshToken() => _s.read(key: _refresh);
  Future<void> saveTenantCode(String v) => _s.write(key: _tenant, value: v);
  Future<String?> readTenantCode() => _s.read(key: _tenant);

  /// Logout/troca de conta: remove credenciais e qualquer cache de identidade visual.
  Future<void> clear() => _s.deleteAll();
}

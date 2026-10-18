import 'dart:convert';

import 'package:http/http.dart' as http;

import 'session_store.dart';

/// Erro padronizado da API (`code` vem do servidor, sem detalhes internos).
class ApiException implements Exception {
  ApiException(this.status, this.code, this.message);
  final int status;
  final String code;
  final String message;
  @override
  String toString() => message;
}

/// Cliente HTTPS da API de loja. Nunca envia tenant_id: o servidor deriva do token.
/// Nenhum segredo do backend é embarcado no aplicativo.
class ApiClient {
  ApiClient(this.baseUrl, this.store, {http.Client? client}) : _http = client ?? http.Client();

  final String baseUrl;
  final SessionStore store;
  final http.Client _http;
  String? _access;

  Future<void> login(String tenantCode, String email, String password) async {
    final r = await _send('POST', '/api/v1/auth/login',
        body: {'tenantCode': tenantCode, 'email': email, 'password': password}, auth: false);
    _access = r['accessToken'] as String;
    await store.saveRefreshToken(r['refreshToken'] as String);
    await store.saveTenantCode(tenantCode);
  }

  Future<bool> tryRestore() async {
    final refresh = await store.readRefreshToken();
    if (refresh == null) return false;
    try {
      await _refresh(refresh);
      return true;
    } on ApiException {
      await store.clear();
      return false;
    }
  }

  Future<void> logout() async {
    try {
      await _send('POST', '/api/v1/auth/logout', body: {});
    } catch (_) {/* sessão já pode estar expirada */}
    _access = null;
    await store.clear();
  }

  Future<Map<String, dynamic>> get(String path) async => await _send('GET', path) as Map<String, dynamic>;

  Future<void> _refresh(String refresh) async {
    final r = await _send('POST', '/api/v1/auth/refresh', body: {'refreshToken': refresh}, auth: false);
    _access = r['accessToken'] as String;
    await store.saveRefreshToken(r['refreshToken'] as String);
  }

  Future<dynamic> _send(String method, String path, {Object? body, bool auth = true, bool retried = false}) async {
    final uri = Uri.parse(baseUrl + path);
    if (uri.scheme != 'https' && !uri.host.contains('localhost') && uri.host != '10.0.2.2') {
      throw ApiException(0, 'insecure_transport', 'Conexão sem HTTPS não é permitida.');
    }
    final headers = <String, String>{'Content-Type': 'application/json'};
    if (auth && _access != null) headers['Authorization'] = 'Bearer $_access';
    final req = http.Request(method, uri)..headers.addAll(headers);
    if (body != null) req.body = jsonEncode(body);
    final res = await http.Response.fromStream(await _http.send(req).timeout(const Duration(seconds: 20)));

    if (res.statusCode == 401 && auth && !retried) {
      final refresh = await store.readRefreshToken();
      if (refresh != null) {
        await _refresh(refresh);
        return _send(method, path, body: body, auth: auth, retried: true);
      }
    }
    final data = res.body.isEmpty ? null : jsonDecode(utf8.decode(res.bodyBytes));
    if (res.statusCode >= 400) {
      final map = data is Map ? data : const {};
      throw ApiException(res.statusCode, (map['code'] ?? 'http_error') as String, (map['title'] ?? 'Erro ${res.statusCode}') as String);
    }
    return data;
  }
}

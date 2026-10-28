import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:http_parser/http_parser.dart';

import 'server_config.dart';
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

  /// Pode ser trocado na tela de login (campo "Servidor").
  String baseUrl;
  final SessionStore store;
  final http.Client _http;
  String? _access;

  /// [totpCode]: código do autenticador, quando o usuário ativou a verificação em duas etapas.
  Future<void> login(String tenantCode, String email, String password, {String? totpCode}) async {
    final r = await _send('POST', '/api/v1/auth/login',
        body: {'tenantCode': tenantCode, 'email': email, 'password': password, if (totpCode != null && totpCode.isNotEmpty) 'totpCode': totpCode},
        auth: false);
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

  /// GET sem login (versão do app, WhatsApp de atendimento, teste de conexão). Devolve null em 204.
  Future<Map<String, dynamic>?> getPublic(String path) async => await _send('GET', path, auth: false) as Map<String, dynamic>?;

  Future<Map<String, dynamic>> get(String path) async => await _send('GET', path) as Map<String, dynamic>;
  Future<List<dynamic>> getList(String path) async => await _send('GET', path) as List<dynamic>;
  Future<dynamic> post(String path, Object body) => _send('POST', path, body: body);
  Future<dynamic> put(String path, Object body) => _send('PUT', path, body: body);

  /// Identifica o app do celular: o servidor só aceita usuários com a permissão "Usar o app do celular".
  static const clientHeaders = {'X-Client-App': 'mobile'};

  /// Envia a foto do produto (multipart, campo "file"). O servidor valida o conteúdo real da imagem.
  Future<void> uploadImage(String path, List<int> bytes, String fileName) async {
    final req = http.MultipartRequest('POST', Uri.parse(baseUrl + path))..headers.addAll(clientHeaders);
    if (_access != null) req.headers['Authorization'] = 'Bearer $_access';
    final type = fileName.toLowerCase().endsWith('.png') ? 'png' : 'jpeg';
    req.files.add(http.MultipartFile.fromBytes('file', bytes, filename: fileName, contentType: MediaType('image', type)));
    final res = await http.Response.fromStream(await _http.send(req).timeout(const Duration(seconds: 60)));
    if (res.statusCode >= 400) {
      final map = res.body.isEmpty ? const {} : jsonDecode(res.body) as Map;
      throw ApiException(res.statusCode, (map['code'] ?? 'http_error') as String, (map['title'] ?? 'Erro ${res.statusCode}') as String);
    }
  }

  /// Baixa a foto do produto (autenticada). Retorna null se o produto não tiver foto.
  Future<List<int>?> getBytes(String path) async {
    final res = await _http.get(Uri.parse(baseUrl + path), headers: {...clientHeaders, if (_access != null) 'Authorization': 'Bearer $_access'});
    return res.statusCode == 200 ? res.bodyBytes : null;
  }

  Future<void> _refresh(String refresh) async {
    final r = await _send('POST', '/api/v1/auth/refresh', body: {'refreshToken': refresh}, auth: false);
    _access = r['accessToken'] as String;
    await store.saveRefreshToken(r['refreshToken'] as String);
  }

  Future<dynamic> _send(String method, String path, {Object? body, bool auth = true, bool retried = false}) async {
    final uri = Uri.parse(baseUrl + path);
    if (!ServerConfig.isAllowed(uri)) {
      throw ApiException(0, 'insecure_transport', 'Conexão sem HTTPS só é permitida na rede local.');
    }
    final headers = <String, String>{'Content-Type': 'application/json', ...clientHeaders};
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

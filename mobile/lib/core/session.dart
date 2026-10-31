import 'api_client.dart';

/// Quem está usando o app e o que pode fazer (vem de /api/v1/me). Só controla o que aparece na tela:
/// o servidor confere tudo de novo a cada pedido.
class Me {
  Me({required this.tenantCode, required this.tenantName, required this.roles, required this.permissions, required this.modules});
  final String tenantCode;
  final String tenantName;
  final List<String> roles;
  final Set<String> permissions;
  final Set<String> modules;

  static Future<Me> load(ApiClient api) async {
    final m = await api.get('/api/v1/me');
    final t = m['tenant'] as Map<String, dynamic>;
    return Me(
      tenantCode: t['code'] as String,
      tenantName: (t['tradeName'] ?? t['code']) as String,
      roles: (m['roles'] as List).cast<String>(),
      permissions: (m['permissions'] as List).cast<String>().toSet(),
      modules: (m['modules'] as List).cast<String>().toSet(),
    );
  }

  bool can(String p) => permissions.contains(p);
  bool get isOwner => roles.contains('owner');

  /// Painel do dono: módulo do app do proprietário + permissão de relatórios.
  bool get seesDashboard => modules.contains('app_proprietario') && can('reports.read');
  bool get seesProducts => can('products.read') || can('products.write');
  bool get registersProducts => can('products.write');
  bool get seesStock => can('stock.read');
  bool get entersStock => can('stock.entry');
  bool get managesTeam => can('users.manage');
  /// Financeiro: resumo, contas e lançamento de gastos (módulo financeiro contratado + permissão).
  bool get seesFinance => modules.contains('financeiro') && can('finance.read');
  bool get seesProfit => can('reports.financial');
  /// Ver e pagar as faturas do sistema (mensalidade).
  bool get paysInvoices => can('billing.manage');
}

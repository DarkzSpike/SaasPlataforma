import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'core/api_client.dart';
import 'core/branding.dart';
import 'core/push.dart';
import 'core/session_store.dart';
import 'features/notifications.dart';
import 'features/products.dart';

/// URL da API definida no build: flutter run --dart-define=API_BASE_URL=https://api.exemplo.com.br
const apiBaseUrl = String.fromEnvironment('API_BASE_URL', defaultValue: 'http://10.0.2.2:5080');

void main() => runApp(const ComercialApp());

class ComercialApp extends StatefulWidget {
  const ComercialApp({super.key});
  @override
  State<ComercialApp> createState() => _ComercialAppState();
}

class _ComercialAppState extends State<ComercialApp> {
  final api = ApiClient(apiBaseUrl, SessionStore());
  Branding branding = Branding.fallback();
  bool? loggedIn;

  @override
  void initState() {
    super.initState();
    api.tryRestore().then((ok) async {
      if (ok) {
        await _loadBranding();
        PushRegistration.register(api);
      }
      setState(() => loggedIn = ok);
    });
  }

  Future<void> _loadBranding() async {
    try {
      final b = Branding.fromJson(await api.get('/api/v1/branding'));
      setState(() => branding = b);
    } on ApiException {
      // Mantém a identidade atual; nunca reaproveita a de outra empresa.
    }
  }

  Future<void> _onLoggedIn() async {
    setState(() => branding = Branding.fallback()); // limpa a identidade anterior antes de carregar a nova
    await _loadBranding();
    PushRegistration.register(api); // aviso de quebra de caixa no celular do dono
    setState(() => loggedIn = true);
  }

  Future<void> _onLogout() async {
    await api.logout();
    setState(() {
      branding = Branding.fallback();
      loggedIn = false;
    });
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      key: ValueKey(branding.cacheKey),
      title: branding.systemName,
      theme: branding.theme(Brightness.light),
      darkTheme: branding.theme(Brightness.dark),
      themeMode: branding.themeMode,
      builder: (context, child) => MediaQuery(
        data: MediaQuery.of(context).copyWith(textScaler: TextScaler.linear(branding.fontScale)),
        child: child!,
      ),
      home: switch (loggedIn) {
        null => const Scaffold(body: Center(child: CircularProgressIndicator())),
        false => LoginPage(api: api, branding: branding, onLoggedIn: _onLoggedIn),
        true => HomeShell(api: api, branding: branding, onLogout: _onLogout),
      },
    );
  }
}

/// Navegação principal do app do dono: Painel, Estoque (com cadastro pela câmera) e Avisos.
class HomeShell extends StatefulWidget {
  const HomeShell({super.key, required this.api, required this.branding, required this.onLogout});
  final ApiClient api;
  final Branding branding;
  final Future<void> Function() onLogout;
  @override
  State<HomeShell> createState() => _HomeShellState();
}

class _HomeShellState extends State<HomeShell> {
  int index = 0;
  String? storeId;

  @override
  void initState() {
    super.initState();
    widget.api.getList('/api/v1/stores').then((s) {
      if (s.isNotEmpty) setState(() => storeId = (s.first as Map<String, dynamic>)['id'] as String);
    }).catchError((_) {});
  }

  @override
  Widget build(BuildContext context) {
    final pages = <Widget>[
      DashboardPage(api: widget.api, branding: widget.branding, onLogout: widget.onLogout),
      Scaffold(
        appBar: AppBar(title: const Text('Estoque')),
        body: storeId == null ? const Center(child: CircularProgressIndicator()) : ProductsPage(api: widget.api, storeId: storeId!),
      ),
      Scaffold(appBar: AppBar(title: const Text('Avisos')), body: NotificationsPage(api: widget.api)),
    ];
    return Scaffold(
      body: IndexedStack(index: index, children: pages),
      bottomNavigationBar: NavigationBar(
        selectedIndex: index,
        onDestinationSelected: (i) => setState(() => index = i),
        destinations: const [
          NavigationDestination(icon: Icon(Icons.dashboard_outlined), label: 'Painel'),
          NavigationDestination(icon: Icon(Icons.inventory_2_outlined), label: 'Estoque'),
          NavigationDestination(icon: Icon(Icons.notifications_outlined), label: 'Avisos'),
        ],
      ),
    );
  }
}

class LoginPage extends StatefulWidget {
  const LoginPage({super.key, required this.api, required this.branding, required this.onLoggedIn});
  final ApiClient api;
  final Branding branding;
  final Future<void> Function() onLoggedIn;
  @override
  State<LoginPage> createState() => _LoginPageState();
}

class _LoginPageState extends State<LoginPage> {
  final tenant = TextEditingController();
  final email = TextEditingController();
  final password = TextEditingController();
  String? error;
  bool busy = false;

  @override
  void initState() {
    super.initState();
    widget.api.store.readTenantCode().then((v) => tenant.text = v ?? '');
  }

  Future<void> _submit() async {
    setState(() { busy = true; error = null; });
    try {
      await widget.api.login(tenant.text.trim(), email.text.trim(), password.text);
      password.clear();
      await widget.onLoggedIn();
    } on ApiException catch (e) {
      setState(() => error = e.code == 'account_locked' ? 'Conta bloqueada temporariamente.' : 'Não foi possível entrar: ${e.message}');
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                Text(widget.branding.systemName, style: Theme.of(context).textTheme.headlineSmall),
                const SizedBox(height: 24),
                TextField(controller: tenant, decoration: const InputDecoration(labelText: 'Código da empresa')),
                TextField(controller: email, decoration: const InputDecoration(labelText: 'E-mail'), keyboardType: TextInputType.emailAddress),
                TextField(controller: password, decoration: const InputDecoration(labelText: 'Senha'), obscureText: true),
                const SizedBox(height: 16),
                FilledButton(onPressed: busy ? null : _submit, child: Text(busy ? 'Entrando...' : 'Entrar')),
                if (error != null) Padding(padding: const EdgeInsets.only(top: 12), child: Text(error!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
              ]),
            ),
          ),
        ),
      ),
    );
  }
}

class DashboardPage extends StatefulWidget {
  const DashboardPage({super.key, required this.api, required this.branding, required this.onLogout});
  final ApiClient api;
  final Branding branding;
  final Future<void> Function() onLogout;
  @override
  State<DashboardPage> createState() => _DashboardPageState();
}

class _DashboardPageState extends State<DashboardPage> {
  Map<String, dynamic>? data;
  Map<String, dynamic>? me;
  String? error;
  DateTime? loadedAt;
  final money = NumberFormat.simpleCurrency(locale: 'pt_BR');

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final m = await widget.api.get('/api/v1/me');
      final d = await widget.api.get('/api/v1/mobile/dashboard');
      setState(() { me = m; data = d; error = null; loadedAt = DateTime.now(); });
    } on ApiException catch (e) {
      setState(() => error = switch (e.code) {
        'module_not_enabled' => 'O aplicativo do proprietário não está contratado para esta empresa.',
        'permission_denied' => 'Seu usuário não tem acesso aos relatórios.',
        _ => e.message,
      });
    } catch (_) {
      setState(() => error = 'Sem conexão. Os dados abaixo podem estar desatualizados.');
    }
  }

  String _ago(String? iso) {
    if (iso == null) return 'nunca';
    final d = DateTime.now().difference(DateTime.parse(iso).toLocal());
    if (d.inMinutes < 1) return 'agora';
    if (d.inHours < 1) return 'há ${d.inMinutes} min';
    if (d.inDays < 1) return 'há ${d.inHours} h';
    return 'há ${d.inDays} dias';
  }

  @override
  Widget build(BuildContext context) {
    final d = data;
    final stale = loadedAt != null && DateTime.now().difference(loadedAt!).inMinutes > 15;
    return Scaffold(
      appBar: AppBar(title: Text(widget.branding.systemName), actions: [
        IconButton(onPressed: _load, icon: const Icon(Icons.refresh), tooltip: 'Atualizar'),
        IconButton(onPressed: widget.onLogout, icon: const Icon(Icons.logout), tooltip: 'Sair'),
      ]),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(padding: const EdgeInsets.all(16), children: [
          if (error != null) Card(child: ListTile(leading: const Icon(Icons.warning_amber), title: Text(error!))),
          if (stale) const Card(child: ListTile(leading: Icon(Icons.schedule), title: Text('Dados com mais de 15 minutos. Puxe para atualizar.'))),
          if (d != null) ...[
            _tile('Vendas hoje', money.format((d['todayRevenueCents'] as num) / 100), '${d['todaySalesCount']} vendas'),
            _tile('Vendas no mês', money.format((d['monthRevenueCents'] as num) / 100), null),
            _tile('Caixas abertos', '${d['openCashSessions']}', null),
            _tile('Produtos com estoque baixo', '${d['lowStockProducts']}', null),
            _tile('Licença', '${d['license']}', 'Módulos: ${(me?['modules'] as List?)?.join(', ') ?? '-'}'),
            _tile('Última sincronização dos caixas', _ago(d['lastSyncAt'] as String?), d['note'] as String?),
          ],
        ]),
      ),
    );
  }

  Widget _tile(String title, String value, String? subtitle) => Card(
        child: ListTile(
          title: Text(title),
          subtitle: subtitle == null ? null : Text(subtitle),
          trailing: Text(value, style: Theme.of(context).textTheme.titleLarge?.copyWith(color: Theme.of(context).colorScheme.primary)),
        ),
      );
}

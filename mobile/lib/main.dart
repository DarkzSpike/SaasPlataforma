import 'dart:async';

import 'package:flutter/material.dart';

import 'core/api_client.dart';
import 'core/app_extras.dart';
import 'core/branding.dart';
import 'core/push.dart';
import 'core/reminders.dart';
import 'core/server_config.dart';
import 'core/session.dart';
import 'core/session_store.dart';
import 'features/invoices.dart';
import 'features/notifications.dart';
import 'features/customers.dart';
import 'features/finance.dart';
import 'features/owner.dart';
import 'features/products.dart';
import 'features/team.dart';

void main() => runApp(const ComercialApp());

class ComercialApp extends StatefulWidget {
  const ComercialApp({super.key});
  @override
  State<ComercialApp> createState() => _ComercialAppState();
}

class _ComercialAppState extends State<ComercialApp> with WidgetsBindingObserver {
  // Uma por identidade visual: o MaterialApp é recriado quando a marca muda (ValueKey abaixo).
  var navigatorKey = GlobalKey<NavigatorState>();
  String _navigatorFor = '';
  final server = ServerConfig();
  final api = ApiClient(defaultApiBaseUrl, SessionStore());
  Branding branding = Branding.fallback();
  bool? loggedIn;
  AppUpdate? update;
  SupportContact? support;
  String? tenantCode;
  Timer? _updateTimer;
  int _offeredVersion = 0; // pergunta uma vez por versão; a faixa no topo continua visível

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _start();
    _updateTimer = Timer.periodic(const Duration(minutes: 30), (_) => _checkUpdate());
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _updateTimer?.cancel();
    super.dispose();
  }

  /// Ao voltar para o app, confere de novo se saiu versão nova.
  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) _checkUpdate();
  }

  Future<void> _checkUpdate() async {
    final u = await AppUpdate.check(api);
    if (!mounted) return;
    setState(() => update = u);
    if (u != null && u.versionCode != _offeredVersion) {
      _offeredVersion = u.versionCode;
      // Espera a primeira tela aparecer para poder mostrar a pergunta.
      WidgetsBinding.instance.addPostFrameCallback((_) {
        final ctx = navigatorKey.currentContext;
        if (ctx != null) UpdateFlow.offer(ctx, u);
      });
    }
  }

  void _runUpdate() {
    final ctx = navigatorKey.currentContext;
    if (ctx != null && update != null) UpdateFlow.run(ctx, update!);
  }

  Future<void> _start() async {
    api.baseUrl = await server.read();
    tenantCode = await api.store.readTenantCode();
    final ok = await api.tryRestore().catchError((_) => false);
    if (ok) {
      await _loadBranding();
      PushRegistration.register(api);
    }
    setState(() => loggedIn = ok);
    _loadExtras(); // depois da marca: a pergunta de atualização não some quando a tela é recriada
  }

  /// Aviso de versão nova e WhatsApp de atendimento (funcionam sem login).
  Future<void> _loadExtras() async {
    final s = await SupportContact.load(api);
    if (mounted) setState(() => support = s);
    await _checkUpdate();
  }

  Future<void> _onServerChanged(String url) async {
    await server.save(url);
    api.baseUrl = await server.read();
    await _loadExtras();
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
    tenantCode = await api.store.readTenantCode();
    setState(() => branding = Branding.fallback()); // limpa a identidade anterior antes de carregar a nova
    await _loadBranding();
    PushRegistration.register(api); // aviso de quebra de caixa no celular do dono
    setState(() => loggedIn = true);
    _offeredVersion = 0; // a tela foi recriada com a marca da empresa: pergunta de novo se havia versão nova
    _checkUpdate();
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
    if (_navigatorFor != branding.cacheKey) {
      _navigatorFor = branding.cacheKey;
      navigatorKey = GlobalKey<NavigatorState>();
    }
    return MaterialApp(
      key: ValueKey(branding.cacheKey),
      navigatorKey: navigatorKey,
      title: branding.systemName,
      theme: branding.theme(Brightness.light),
      darkTheme: branding.theme(Brightness.dark),
      themeMode: branding.themeMode,
      builder: (context, child) => MediaQuery(
        data: MediaQuery.of(context).copyWith(textScaler: TextScaler.linear(branding.fontScale)),
        child: Column(children: [
          if (update != null) UpdateBanner(update: update!, onUpdate: _runUpdate),
          Expanded(child: child!),
        ]),
      ),
      home: switch (loggedIn) {
        null => const Scaffold(body: Center(child: CircularProgressIndicator())),
        false => LoginPage(api: api, branding: branding, onLoggedIn: _onLoggedIn, onServerChanged: _onServerChanged, support: support),
        true => HomeShell(api: api, branding: branding, onLogout: _onLogout, support: support, tenantCode: tenantCode),
      },
    );
  }
}

/// Navegação principal. As abas dependem do usuário: o dono vê tudo; um funcionário liberado vê só o que as
/// permissões dele deixam (ex.: estoquista → Produtos). O servidor confere de novo cada pedido.
class HomeShell extends StatefulWidget {
  const HomeShell({super.key, required this.api, required this.branding, required this.onLogout, this.support, this.tenantCode});
  final ApiClient api;
  final Branding branding;
  final Future<void> Function() onLogout;
  final SupportContact? support;
  final String? tenantCode;
  @override
  State<HomeShell> createState() => _HomeShellState();
}

class _HomeShellState extends State<HomeShell> with WidgetsBindingObserver {
  int index = 0;
  Me? me;
  String? storeId;
  String? error;
  bool _openCustomers = false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    Reminders.tapped.addListener(_onReminder);
    _load();
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    Reminders.tapped.removeListener(_onReminder);
    super.dispose();
  }

  // Volta para o app: refaz a agenda de avisos (cobranças e promoções podem ter mudado no caixa).
  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed && me != null) Reminders.refresh(widget.api, me!);
  }

  // Tocou no aviso de cobrança/promoção: abre a aba Clientes.
  void _onReminder() {
    if (Reminders.tapped.value == null) return;
    Reminders.tapped.value = null;
    setState(() => _openCustomers = true);
  }

  Future<void> _load() async {
    try {
      final m = await Me.load(widget.api);
      final stores = await widget.api.getList('/api/v1/stores').catchError((_) => <dynamic>[]);
      setState(() {
        me = m;
        storeId = stores.isEmpty ? null : (stores.first as Map<String, dynamic>)['id'] as String;
        error = null;
      });
      Reminders.refresh(widget.api, m);
    } on ApiException catch (e) {
      if (e.code == 'mobile_not_allowed') {
        await widget.onLogout(); // o dono desligou o acesso deste usuário ao app
        return;
      }
      setState(() => error = e.message);
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    }
  }

  @override
  Widget build(BuildContext context) {
    final m = me;
    if (m == null) {
      return Scaffold(body: Center(child: error == null
          ? const CircularProgressIndicator()
          : Column(mainAxisSize: MainAxisSize.min, children: [
              Text(error!), const SizedBox(height: 12),
              FilledButton(onPressed: _load, child: const Text('Tentar de novo')),
              TextButton(onPressed: widget.onLogout, child: const Text('Sair')),
            ])));
    }
    final tabs = <({Widget page, NavigationDestination dest, bool fab})>[
      if (m.seesDashboard)
        (page: OwnerDashboardPage(api: widget.api, me: m, onLogout: widget.onLogout), dest: const NavigationDestination(icon: Icon(Icons.dashboard_outlined), label: 'Painel'), fab: true),
      if (m.seesProducts && storeId != null)
        (page: ProductsPage(api: widget.api, me: m, storeId: storeId!), dest: const NavigationDestination(icon: Icon(Icons.inventory_2_outlined), label: 'Produtos'), fab: false),
      if (m.managesTeam)
        (page: TeamPage(api: widget.api), dest: const NavigationDestination(icon: Icon(Icons.group_outlined), label: 'Equipe'), fab: true),
      if (m.seesCustomers)
        (page: CustomersPage(api: widget.api, me: m), dest: const NavigationDestination(icon: Icon(Icons.people_alt_outlined), label: 'Clientes'), fab: true),
      if (m.seesFinance)
        (page: FinancePage(api: widget.api, me: m), dest: const NavigationDestination(icon: Icon(Icons.account_balance_wallet_outlined), label: 'Financeiro'), fab: true),
      if (m.seesDashboard)
        (page: Scaffold(appBar: AppBar(title: const Text('Avisos')), body: NotificationsPage(api: widget.api, canPay: m.paysInvoices)),
          dest: const NavigationDestination(icon: Icon(Icons.notifications_outlined), label: 'Avisos'), fab: true),
      if (m.paysInvoices)
        (page: InvoicesPage(api: widget.api), dest: const NavigationDestination(icon: Icon(Icons.receipt_long_outlined), label: 'Faturas'), fab: true),
    ];
    if (tabs.isEmpty) {
      return Scaffold(
        appBar: AppBar(title: Text(m.tenantName), actions: [IconButton(onPressed: widget.onLogout, icon: const Icon(Icons.logout))]),
        body: const Center(child: Padding(padding: EdgeInsets.all(24),
            child: Text('Seu usuário entrou, mas não tem nenhuma função liberada no app. Peça ao dono para liberar "Cadastrar e editar produtos".', textAlign: TextAlign.center))),
      );
    }
    if (_openCustomers) {
      _openCustomers = false;
      final c = tabs.indexWhere((t) => t.page is CustomersPage);
      if (c >= 0) index = c;
    }
    final i = index.clamp(0, tabs.length - 1);
    return Scaffold(
      body: IndexedStack(index: i, children: [for (final t in tabs) t.page]),
      // Na aba Produtos o botão "Cadastrar produto" ocupa o canto; o WhatsApp aparece nas demais.
      floatingActionButton: widget.support == null || !tabs[i].fab ? null : WhatsappFab(contact: widget.support!, tenantCode: widget.tenantCode),
      bottomNavigationBar: tabs.length < 2 ? null : NavigationBar(
        selectedIndex: i,
        onDestinationSelected: (v) => setState(() => index = v),
        // Com muitas abas, só a escolhida mostra o nome (os ícones continuam todos visíveis).
        labelBehavior: tabs.length > 5 ? NavigationDestinationLabelBehavior.onlyShowSelected : null,
        destinations: [for (final t in tabs) t.dest],
      ),
    );
  }
}

class LoginPage extends StatefulWidget {
  const LoginPage({super.key, required this.api, required this.branding, required this.onLoggedIn, required this.onServerChanged, this.support});
  final ApiClient api;
  final Branding branding;
  final Future<void> Function() onLoggedIn;
  final Future<void> Function(String url) onServerChanged;
  final SupportContact? support;
  @override
  State<LoginPage> createState() => _LoginPageState();
}

class _LoginPageState extends State<LoginPage> {
  final tenant = TextEditingController();
  final email = TextEditingController();
  final password = TextEditingController();
  final totp = TextEditingController();
  final serverUrl = TextEditingController();
  String? error;
  String? serverInfo;
  bool busy = false;
  bool needTotp = false;
  bool showServer = false;

  @override
  void initState() {
    super.initState();
    widget.api.store.readTenantCode().then((v) => tenant.text = v ?? '');
    serverUrl.text = widget.api.baseUrl;
  }

  Future<void> _submit() async {
    setState(() { busy = true; error = null; });
    try {
      await widget.api.login(tenant.text.trim(), email.text.trim(), password.text, totpCode: needTotp ? totp.text.trim() : null);
      password.clear();
      totp.clear();
      await widget.onLoggedIn();
    } on ApiException catch (e) {
      setState(() {
        if (e.code == 'totp_required' || e.code == 'invalid_totp') { needTotp = true; totp.clear(); }
        error = switch (e.code) {
          'account_locked' => 'Conta bloqueada temporariamente.',
          'mobile_not_allowed' => 'Seu usuário não tem acesso ao app. Peça ao dono da loja para liberar em Equipe.',
          'invalid_credentials' => 'Código da empresa, e-mail ou senha incorretos.',
          'totp_required' => 'Digite o código de 6 dígitos do aplicativo autenticador.',
          'invalid_totp' => 'Código do autenticador incorreto (ou já usado). Espere o próximo.',
          'insecure_transport' => e.message,
          _ => 'Não foi possível entrar: ${e.message}',
        };
      });
    } catch (_) {
      setState(() { error = 'Sem conexão com o servidor (${widget.api.baseUrl}). Confira o endereço em "Servidor".'; showServer = true; });
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  /// Salva o endereço do servidor e testa a conexão (GET /health).
  Future<void> _saveServer() async {
    setState(() => serverInfo = 'Testando...');
    await widget.onServerChanged(serverUrl.text);
    serverUrl.text = widget.api.baseUrl;
    try {
      await widget.api.getPublic('/health');
      setState(() => serverInfo = 'Conectado a ${widget.api.baseUrl}');
    } on ApiException catch (e) {
      setState(() => serverInfo = e.message);
    } catch (_) {
      setState(() => serverInfo = 'Sem resposta de ${widget.api.baseUrl}. O computador e o celular estão no mesmo Wi-Fi? A API está aberta?');
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      floatingActionButton: widget.support == null ? null : WhatsappFab(contact: widget.support!),
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
                if (needTotp)
                  TextField(controller: totp, decoration: const InputDecoration(labelText: 'Código do autenticador (6 dígitos)'),
                      keyboardType: TextInputType.number, maxLength: 6),
                const SizedBox(height: 16),
                FilledButton(onPressed: busy ? null : _submit, child: Text(busy ? 'Entrando...' : 'Entrar')),
                if (error != null) Padding(padding: const EdgeInsets.only(top: 12), child: Text(error!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
                const SizedBox(height: 12),
                TextButton.icon(
                  onPressed: () => setState(() => showServer = !showServer),
                  icon: const Icon(Icons.dns_outlined),
                  label: Text('Servidor: ${widget.api.baseUrl}'),
                ),
                if (showServer) ...[
                  TextField(controller: serverUrl, keyboardType: TextInputType.url,
                      decoration: const InputDecoration(labelText: 'Endereço do servidor', helperText: 'Rede local: http://IP-DO-COMPUTADOR:5080')),
                  const SizedBox(height: 8),
                  OutlinedButton(onPressed: _saveServer, child: const Text('Salvar e testar conexão')),
                  if (serverInfo != null) Padding(padding: const EdgeInsets.only(top: 8), child: Text(serverInfo!)),
                ],
              ]),
            ),
          ),
        ),
      ),
    );
  }
}

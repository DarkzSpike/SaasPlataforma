import 'package:flutter/material.dart';

import '../core/api_client.dart';

/// Equipe: cadastrar funcionário, criar nova senha, mudar a função, escolher o que ele pode ver e fazer,
/// bloquear e liberar o app do celular.
/// Vale na hora no caixa também (é o mesmo cadastro no servidor, que confere cada permissão de novo).
class TeamPage extends StatefulWidget {
  const TeamPage({super.key, required this.api});
  final ApiClient api;
  @override
  State<TeamPage> createState() => _TeamPageState();
}

const _roleNames = {'owner': 'Proprietário', 'manager': 'Gerente', 'operator': 'Operador de caixa', 'stockist': 'Estoquista', 'finance': 'Financeiro'};
String _roleName(String r) => _roleNames[r] ?? r;

/// Regra de senha igual à do servidor: 10+ caracteres com letras e números.
String? _passwordProblem(String p) => p.length < 10
    ? 'A senha deve ter pelo menos 10 caracteres.'
    : !RegExp(r'[A-Za-z]').hasMatch(p) || !RegExp(r'\d').hasMatch(p)
        ? 'A senha deve ter letras e números.'
        : null;

class _TeamPageState extends State<TeamPage> {
  List<Map<String, dynamic>> users = [];
  List<String> roles = [];
  Map<String, dynamic> catalog = {};
  String? error;
  bool loading = true;
  String? myId;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final me = await widget.api.get('/api/v1/me');
      final rows = await widget.api.getList('/api/v1/users');
      final cat = await widget.api.get('/api/v1/users/permission-catalog');
      setState(() {
        catalog = cat;
        myId = me['user'] as String?;
        users = rows.cast<Map<String, dynamic>>();
        // Só as funções que quem está usando pode dar; "proprietário" não se cria por aqui.
        roles = [for (final r in (cat['roles'] as List)) (r as Map)['key'] as String].where((r) => r != 'owner').toList();
        error = null;
      });
    } catch (e) {
      setState(() => error = e is ApiException ? e.message : 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => loading = false);
    }
  }

  void _snack(String m) {
    if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(m)));
  }

  /// Executa uma ação no servidor, recarrega a lista e mostra o resultado.
  Future<void> _run(Future<void> Function() action, String ok) async {
    try {
      await action();
      await _load();
      _snack(ok);
    } on ApiException catch (e) {
      _snack(e.message);
    } catch (_) {
      _snack('Sem conexão com o servidor.');
    }
  }

  Future<void> _toggle(Map<String, dynamic> u, bool allowed) => _run(
      () => widget.api.put('/api/v1/users/${u['id']}/mobile-access', {'allowed': allowed}),
      allowed ? '${u['displayName']} já pode entrar no app (código da empresa, e-mail e senha dele).' : 'Acesso de ${u['displayName']} ao app desligado.');

  Future<void> _create() async {
    final created = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => _EmployeeForm(api: widget.api, roles: roles)));
    if (created == true) {
      await _load();
      _snack('Funcionário cadastrado. Ele já pode entrar no caixa com o e-mail e a senha.');
    }
  }

  Future<void> _password(Map<String, dynamic> u) async {
    final pass = await showDialog<String>(context: context, builder: (_) => _PasswordDialog(name: u['displayName'] as String));
    if (pass != null) {
      await _run(() => widget.api.post('/api/v1/users/${u['id']}/password', {'newPassword': pass}),
          'Senha de ${u['displayName']} trocada. Ele precisa entrar de novo com a nova senha.');
    }
  }

  Future<void> _role(Map<String, dynamic> u) async {
    final current = (u['roles'] as List).cast<String>();
    final r = await showDialog<String>(
        context: context,
        builder: (c) => SimpleDialog(title: Text('Função de ${u['displayName']}'), children: [
              for (final r in roles)
                ListTile(
                  leading: Icon(current.contains(r) ? Icons.radio_button_checked : Icons.radio_button_unchecked),
                  title: Text(_roleName(r)),
                  onTap: () => Navigator.pop(c, r),
                ),
            ]));
    if (r != null && !(current.length == 1 && current.first == r)) {
      await _run(() => widget.api.put('/api/v1/users/${u['id']}/roles', {'roles': [r]}), '${u['displayName']} agora é ${_roleName(r)}.');
    }
  }

  Future<void> _permissions(Map<String, dynamic> u) async {
    final saved = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => _PermissionsPage(api: widget.api, user: u, catalog: catalog)));
    if (saved == true) {
      await _load();
      _snack('Permissões de ${u['displayName']} salvas. Valem na próxima vez que ele entrar.');
    }
  }

  Future<void> _block(Map<String, dynamic> u) async {
    final active = u['isActive'] == true;
    final ok = await showDialog<bool>(
        context: context,
        builder: (c) => AlertDialog(
              title: Text(active ? 'Bloquear ${u['displayName']}?' : 'Desbloquear ${u['displayName']}?'),
              content: Text(active
                  ? 'Ele sai na hora do caixa e do app e não consegue mais entrar até ser desbloqueado.'
                  : 'Ele volta a poder entrar com a senha atual.'),
              actions: [
                TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Voltar')),
                FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(active ? 'Bloquear' : 'Desbloquear')),
              ],
            ));
    if (ok != true) return;
    await _run(() async {
      await widget.api.post('/api/v1/users/${u['id']}/${active ? 'block' : 'unblock'}', {});
      if (active) await widget.api.post('/api/v1/users/${u['id']}/revoke-sessions', {});
    }, active ? '${u['displayName']} bloqueado.' : '${u['displayName']} desbloqueado.');
  }

  void _actions(Map<String, dynamic> u) {
    if (u['id'] == myId) return _snack('Para trocar a sua própria senha use o caixa (Minha conta).');
    if ((u['roles'] as List).contains('owner')) return _snack('O proprietário não pode ser alterado por aqui.');
    final active = u['isActive'] == true;
    showModalBottomSheet<void>(
        context: context,
        showDragHandle: true,
        builder: (c) => SafeArea(
                child: Column(mainAxisSize: MainAxisSize.min, children: [
              ListTile(title: Text(u['displayName'] as String, style: const TextStyle(fontWeight: FontWeight.bold)), subtitle: Text(u['email'] as String)),
              ListTile(leading: const Icon(Icons.key_outlined), title: const Text('Criar nova senha'), onTap: () {
                Navigator.pop(c);
                _password(u);
              }),
              ListTile(
                  leading: const Icon(Icons.badge_outlined),
                  title: const Text('Mudar função'),
                  subtitle: Text((u['roles'] as List).map((r) => _roleName(r as String)).join(', ')),
                  onTap: () {
                    Navigator.pop(c);
                    _role(u);
                  }),
              ListTile(
                  leading: const Icon(Icons.visibility_outlined),
                  title: const Text('O que ele pode ver e fazer'),
                  subtitle: const Text('Telas e ações liberadas para este funcionário'),
                  onTap: () {
                    Navigator.pop(c);
                    _permissions(u);
                  }),
              ListTile(
                  leading: Icon(active ? Icons.block : Icons.lock_open_outlined, color: active ? Theme.of(context).colorScheme.error : null),
                  title: Text(active ? 'Bloquear acesso' : 'Desbloquear'),
                  onTap: () {
                    Navigator.pop(c);
                    _block(u);
                  }),
            ])));
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Equipe'), actions: [IconButton(onPressed: _load, icon: const Icon(Icons.refresh))]),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(padding: const EdgeInsets.fromLTRB(12, 8, 12, 160), children: [
          const Card(
              child: ListTile(
            leading: Icon(Icons.info_outline),
            title: Text('Funcionários'),
            subtitle: Text('Toque num funcionário para criar uma nova senha, mudar a função, escolher o que ele pode ver ou bloquear. '
                'A chave "App" libera o celular para ele (ele vê só o que a função dele permite).'),
          )),
          if (error != null) Card(child: ListTile(leading: const Icon(Icons.warning_amber), title: Text(error!))),
          if (loading) const Padding(padding: EdgeInsets.all(32), child: Center(child: CircularProgressIndicator())),
          for (final u in users)
            Card(
              child: ListTile(
                onTap: () => _actions(u),
                leading: CircleAvatar(child: Text(((u['displayName'] as String?) ?? '?').characters.first.toUpperCase())),
                title: Text('${u['displayName']}${u['isActive'] == true ? '' : ' (bloqueado)'}'),
                subtitle: Text('${(u['roles'] as List).map((r) => _roleName(r as String)).join(', ')}\n${u['email']}'),
                isThreeLine: true,
                trailing: Column(mainAxisSize: MainAxisSize.min, children: [
                  const Text('App', style: TextStyle(fontSize: 11)),
                  SizedBox(
                    height: 32,
                    child: Switch(
                      value: u['mobileAccess'] == true,
                      onChanged: u['id'] == myId || (u['roles'] as List).contains('owner') ? null : (v) => _toggle(u, v),
                    ),
                  ),
                ]),
              ),
            ),
        ]),
      ),
      // À esquerda: o canto direito é do botão do WhatsApp.
      floatingActionButtonLocation: FloatingActionButtonLocation.startFloat,
      floatingActionButton: roles.isEmpty
          ? null
          : FloatingActionButton.extended(heroTag: 'novo-funcionario', onPressed: _create, icon: const Icon(Icons.person_add_alt_1), label: const Text('Novo funcionário')),
    );
  }
}

class _EmployeeForm extends StatefulWidget {
  const _EmployeeForm({required this.api, required this.roles});
  final ApiClient api;
  final List<String> roles;
  @override
  State<_EmployeeForm> createState() => _EmployeeFormState();
}

class _EmployeeFormState extends State<_EmployeeForm> {
  final name = TextEditingController();
  final email = TextEditingController();
  final pass = TextEditingController();
  late String role = widget.roles.contains('operator') ? 'operator' : widget.roles.first;
  bool show = false;
  bool busy = false;
  String? error;

  Future<void> _save() async {
    final e = email.text.trim();
    if (name.text.trim().isEmpty) return setState(() => error = 'Informe o nome.');
    if (!RegExp(r'^[^@\s]+@[^@\s]+\.[^@\s]+$').hasMatch(e)) return setState(() => error = 'E-mail inválido.');
    final problem = _passwordProblem(pass.text);
    if (problem != null) return setState(() => error = problem);
    setState(() {
      busy = true;
      error = null;
    });
    try {
      await widget.api.post('/api/v1/users', {'email': e, 'displayName': name.text.trim(), 'password': pass.text, 'roles': [role]});
      if (mounted) Navigator.pop(context, true);
    } on ApiException catch (x) {
      setState(() => error = x.message);
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: const Text('Novo funcionário')),
        body: ListView(padding: const EdgeInsets.all(16), children: [
          TextField(controller: name, textCapitalization: TextCapitalization.words, decoration: const InputDecoration(labelText: 'Nome')),
          const SizedBox(height: 12),
          TextField(controller: email, keyboardType: TextInputType.emailAddress, decoration: const InputDecoration(labelText: 'E-mail (usado para entrar)')),
          const SizedBox(height: 12),
          TextField(
              controller: pass,
              obscureText: !show,
              decoration: InputDecoration(
                  labelText: 'Senha',
                  helperText: 'Mínimo 10 caracteres, com letras e números',
                  suffixIcon: IconButton(icon: Icon(show ? Icons.visibility_off : Icons.visibility), onPressed: () => setState(() => show = !show)))),
          const SizedBox(height: 12),
          DropdownButtonFormField<String>(
            initialValue: role,
            decoration: const InputDecoration(labelText: 'Função'),
            items: [for (final r in widget.roles) DropdownMenuItem(value: r, child: Text(_roleName(r)))],
            onChanged: (v) => setState(() => role = v ?? role),
          ),
          if (error != null) Padding(padding: const EdgeInsets.only(top: 12), child: Text(error!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
          const SizedBox(height: 20),
          FilledButton.icon(onPressed: busy ? null : _save, icon: const Icon(Icons.check), label: Text(busy ? 'Salvando...' : 'Cadastrar funcionário')),
        ]),
      );
}

class _PasswordDialog extends StatefulWidget {
  const _PasswordDialog({required this.name});
  final String name;
  @override
  State<_PasswordDialog> createState() => _PasswordDialogState();
}

class _PasswordDialogState extends State<_PasswordDialog> {
  final pass = TextEditingController();
  final confirm = TextEditingController();
  bool show = false;
  String? error;

  void _ok() {
    final problem = _passwordProblem(pass.text);
    if (problem != null) return setState(() => error = problem);
    if (pass.text != confirm.text) return setState(() => error = 'As senhas não conferem.');
    Navigator.pop(context, pass.text);
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
        title: Text('Nova senha de ${widget.name}'),
        content: Column(mainAxisSize: MainAxisSize.min, children: [
          TextField(
              controller: pass,
              obscureText: !show,
              decoration: InputDecoration(
                  labelText: 'Nova senha',
                  helperText: '10+ caracteres, letras e números',
                  suffixIcon: IconButton(icon: Icon(show ? Icons.visibility_off : Icons.visibility), onPressed: () => setState(() => show = !show)))),
          const SizedBox(height: 8),
          TextField(controller: confirm, obscureText: !show, decoration: const InputDecoration(labelText: 'Repita a senha')),
          if (error != null) Padding(padding: const EdgeInsets.only(top: 8), child: Text(error!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
        ]),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: const Text('Voltar')),
          FilledButton(onPressed: _ok, child: const Text('Salvar senha')),
        ],
      );
}

/// Áreas para agrupar as permissões na tela (mesma lista do caixa; o servidor confere cada uma de novo).
const _areas = [
  ('Vendas e caixa', ['sales.', 'cash.', 'operating.']),
  ('Produtos e estoque', ['products.', 'stock.']),
  ('Compras', ['purchases.', 'suppliers.']),
  ('Clientes', ['customers.', 'loyalty.', 'messaging.']),
  ('Financeiro', ['finance.', 'billing.', 'payments.']),
  ('Relatórios', ['reports.']),
  ('Fiscal', ['fiscal.']),
  ('Equipe e sistema', ['users.', 'devices.', 'support.', 'sync.', 'mobile.']),
];

String _areaOf(String key) {
  for (final a in _areas) {
    if (a.$2.any(key.startsWith)) return a.$1;
  }
  return 'Outros';
}

/// O que o funcionário pode ver e fazer: começa no padrão da função e o dono marca ou desmarca o que quiser.
/// Só é guardado o que difere da função (igual ao caixa).
class _PermissionsPage extends StatefulWidget {
  const _PermissionsPage({required this.api, required this.user, required this.catalog});
  final ApiClient api;
  final Map<String, dynamic> user;
  final Map<String, dynamic> catalog;
  @override
  State<_PermissionsPage> createState() => _PermissionsPageState();
}

class _PermissionsPageState extends State<_PermissionsPage> {
  late final List<Map<String, dynamic>> perms = (widget.catalog['permissions'] as List? ?? []).cast<Map<String, dynamic>>();
  final Set<String> fromRole = {};
  Map<String, bool> checked = {};
  bool loading = true, busy = false, hadGrants = false;
  String? error;

  String get _name => widget.user['displayName'] as String;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final roles = (widget.user['roles'] as List).cast<String>();
    for (final r in (widget.catalog['roles'] as List? ?? []).cast<Map<String, dynamic>>()) {
      if (roles.contains(r['key'])) fromRole.addAll((r['permissions'] as List).cast<String>());
    }
    try {
      final grants = (await widget.api.getList('/api/v1/users/grants')).cast<Map<String, dynamic>>().where((g) => g['userId'] == widget.user['id']).toList();
      hadGrants = grants.isNotEmpty;
      setState(() {
        checked = {for (final p in perms) p['key'] as String: fromRole.contains(p['key'])};
        for (final g in grants) {
          checked[g['permission'] as String] = g['granted'] == true;
        }
      });
    } on ApiException catch (e) {
      setState(() => error = e.message);
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => loading = false);
    }
  }

  Future<void> _save() async {
    final grants = [
      for (final p in perms)
        if ((checked[p['key']] ?? false) != fromRole.contains(p['key'])) {'permission': p['key'], 'granted': checked[p['key']] ?? false},
    ];
    if (grants.isEmpty && !hadGrants) return Navigator.pop(context, false);
    setState(() {
      busy = true;
      error = null;
    });
    try {
      await widget.api.put('/api/v1/users/${widget.user['id']}/permissions', grants);
      if (mounted) Navigator.pop(context, true);
    } on ApiException catch (e) {
      setState(() => error = e.status == 403
          ? 'Ajustar permissões uma a uma faz parte do módulo "Usuários avançado". Peça a liberação ao suporte ou mude a função do funcionário.'
          : e.message);
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  void _resetToRole() => setState(() => checked = {for (final p in perms) p['key'] as String: fromRole.contains(p['key'])});

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final groups = <String, List<Map<String, dynamic>>>{};
    for (final p in perms) {
      groups.putIfAbsent(_areaOf(p['key'] as String), () => []).add(p);
    }
    final order = [..._areas.map((a) => a.$1), 'Outros'].where(groups.containsKey);
    return Scaffold(
      appBar: AppBar(title: Text('O que $_name pode fazer'), actions: [
        TextButton(onPressed: loading || busy ? null : _resetToRole, child: const Text('Padrão')),
      ]),
      body: loading
          ? const Center(child: CircularProgressIndicator())
          : ListView(padding: const EdgeInsets.fromLTRB(12, 8, 12, 120), children: [
              Card(
                  child: ListTile(
                leading: const Icon(Icons.info_outline),
                title: Text('Função: ${(widget.user['roles'] as List).map((r) => _roleName(r as String)).join(', ')}'),
                subtitle: const Text('Marque o que ele pode ver e fazer no caixa e no app. "Padrão da função" mostra o que a função já libera. '
                    'Itens apagados só o dono pode liberar.'),
              )),
              if (error != null) Card(child: ListTile(leading: Icon(Icons.warning_amber, color: cs.error), title: Text(error!))),
              for (final area in order) ...[
                Padding(padding: const EdgeInsets.fromLTRB(8, 16, 8, 4), child: Text(area, style: TextStyle(color: cs.primary, fontWeight: FontWeight.bold))),
                Card(
                  child: Column(children: [
                    for (final p in groups[area]!)
                      CheckboxListTile(
                        value: checked[p['key']] ?? false,
                        onChanged: p['canGrant'] == true && !busy ? (v) => setState(() => checked[p['key'] as String] = v ?? false) : null,
                        title: Text(p['name'] as String),
                        subtitle: fromRole.contains(p['key']) ? const Text('Padrão da função') : null,
                        dense: true,
                      ),
                  ]),
                ),
              ],
            ]),
      floatingActionButtonLocation: FloatingActionButtonLocation.startFloat,
      floatingActionButton: loading
          ? null
          : FloatingActionButton.extended(
              heroTag: 'salvar-permissoes', onPressed: busy ? null : _save, icon: const Icon(Icons.check), label: Text(busy ? 'Salvando...' : 'Salvar')),
    );
  }
}

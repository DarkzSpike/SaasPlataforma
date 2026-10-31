import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:http/http.dart' as http;
import 'package:package_info_plus/package_info_plus.dart';
import 'package:url_launcher/url_launcher.dart';

import 'api_client.dart';
import 'server_config.dart';

/// Versão nova do app publicada no servidor (painel admin → Atualizações, ou o APK mais novo gerado pelo
/// dev/build-apk.ps1). O app baixa o arquivo e abre o instalador do Android, que pede a confirmação do usuário.
class AppUpdate {
  AppUpdate(this.versionCode, this.versionName, this.url, this.notes, this.mandatory);
  final int versionCode;
  final String versionName;
  final String url;
  final String? notes;
  final bool mandatory;

  static const _channel = MethodChannel('comercial/updater');

  /// Compara o versionCode publicado com o deste APK. Sem internet ou sem versão publicada: null.
  static Future<AppUpdate?> check(ApiClient api) async {
    try {
      final r = await api.getPublic('/api/v1/public/mobile/latest');
      if (r == null) return null;
      final current = int.tryParse((await PackageInfo.fromPlatform()).buildNumber) ?? 0;
      final code = (r['versionCode'] as num).toInt();
      if (code <= current) return null;
      // Caminho relativo ("/downloads/x.apk"): baixa do mesmo servidor em que o app está conectado.
      final path = r['path'];
      var url = path is String && path.startsWith('/') ? api.baseUrl + path : r['url'] as String;
      if (!ServerConfig.isAllowed(Uri.parse(url))) return null;
      return AppUpdate(code, r['versionName'] as String, url, r['notes'] as String?, r['mandatory'] == true);
    } catch (_) {
      return null;
    }
  }

  /// Baixa o APK para a pasta de atualizações do app, avisando o progresso (0 a 1; null = tamanho desconhecido).
  Future<String> download(void Function(double?) onProgress) async {
    final dir = await _channel.invokeMethod<String>('updatesDir');
    final file = File('$dir/comercial-$versionCode.apk');
    final client = http.Client();
    try {
      final res = await client.send(http.Request('GET', Uri.parse(url)));
      if (res.statusCode != 200) throw Exception('o servidor respondeu ${res.statusCode}');
      final total = res.contentLength;
      var received = 0;
      final sink = file.openWrite();
      try {
        await for (final chunk in res.stream) {
          sink.add(chunk);
          received += chunk.length;
          onProgress(total == null || total == 0 ? null : received / total);
        }
      } finally {
        await sink.close();
      }
      return file.path;
    } finally {
      client.close();
    }
  }

  /// O Android pede, uma única vez, a permissão "instalar apps desta fonte".
  static Future<bool> canInstall() async => await _channel.invokeMethod<bool>('canInstall') ?? false;
  static Future<void> openInstallSettings() => _channel.invokeMethod('openInstallSettings');

  /// Abre o instalador do Android (ele mostra "Atualizar este app?" e o usuário confirma). O login é mantido.
  static Future<void> install(String path) => _channel.invokeMethod('install', {'path': path});

  /// Alternativa se o download falhar: abre o link no navegador.
  Future<void> openInBrowser() => launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication);
}

/// Pergunta se pode atualizar e conduz o download e a instalação.
class UpdateFlow {
  /// Mostra "Nova versão — atualizar agora?". Versão obrigatória não tem o botão "Depois".
  static Future<void> offer(BuildContext context, AppUpdate update) async {
    final ok = await showDialog<bool>(
      context: context,
      barrierDismissible: !update.mandatory,
      builder: (c) => AlertDialog(
        icon: const Icon(Icons.system_update, size: 36),
        title: Text('Nova versão ${update.versionName}'),
        content: Text([
          if (update.notes != null && update.notes!.isNotEmpty) update.notes!,
          update.mandatory
              ? 'Esta atualização é obrigatória para continuar usando o app.'
              : 'Quer atualizar agora? Leva cerca de um minuto e você continua conectado.',
        ].join('\n\n')),
        actions: [
          if (!update.mandatory) TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Depois')),
          FilledButton.icon(onPressed: () => Navigator.pop(c, true), icon: const Icon(Icons.download), label: const Text('Atualizar agora')),
        ],
      ),
    );
    if (ok == true && context.mounted) await run(context, update);
  }

  /// Baixa (com barra de progresso) e abre o instalador do Android.
  static Future<void> run(BuildContext context, AppUpdate update) async {
    if (!await AppUpdate.canInstall()) {
      if (!context.mounted) return;
      final go = await showDialog<bool>(
        context: context,
        builder: (c) => AlertDialog(
          title: const Text('Permitir a atualização'),
          content: const Text('Só na primeira vez: o Android vai abrir uma tela. Ative "Permitir desta fonte", '
              'volte para o app e toque em "Atualizar" de novo.'),
          actions: [
            TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Cancelar')),
            FilledButton(onPressed: () => Navigator.pop(c, true), child: const Text('Abrir configuração')),
          ],
        ),
      );
      if (go == true) await AppUpdate.openInstallSettings();
      return;
    }
    if (!context.mounted) return;
    final nav = Navigator.of(context, rootNavigator: true);
    final progress = ValueNotifier<double?>(null);
    showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (c) => PopScope(
        canPop: false,
        child: AlertDialog(
          title: Text('Baixando a versão ${update.versionName}'),
          content: ValueListenableBuilder<double?>(
            valueListenable: progress,
            builder: (_, v, __) => Column(mainAxisSize: MainAxisSize.min, children: [
              LinearProgressIndicator(value: v),
              const SizedBox(height: 12),
              Text(v == null ? 'Aguarde...' : '${(v * 100).round()}%'),
            ]),
          ),
        ),
      ),
    );
    try {
      final path = await update.download((v) => progress.value = v);
      nav.pop();
      await AppUpdate.install(path);
    } catch (e) {
      nav.pop();
      if (!context.mounted) return;
      final browser = await showDialog<bool>(
        context: context,
        builder: (c) => AlertDialog(
          title: const Text('Não foi possível baixar'),
          content: Text('Confira a internet e tente de novo.\n\nDetalhe: $e'),
          actions: [
            TextButton(onPressed: () => Navigator.pop(c, true), child: const Text('Baixar pelo navegador')),
            FilledButton(onPressed: () => Navigator.pop(c, false), child: const Text('OK')),
          ],
        ),
      );
      if (browser == true) await update.openInBrowser();
    }
  }
}

/// Faixa "Nova versão disponível" no topo das telas.
class UpdateBanner extends StatelessWidget {
  const UpdateBanner({super.key, required this.update, required this.onUpdate});
  final AppUpdate update;
  final VoidCallback onUpdate;
  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Material(
      color: cs.primaryContainer,
      child: SafeArea(
        bottom: false,
        child: ListTile(
          leading: Icon(Icons.system_update, color: cs.onPrimaryContainer),
          title: Text('Nova versão ${update.versionName}${update.mandatory ? ' (obrigatória)' : ''}', style: TextStyle(color: cs.onPrimaryContainer)),
          subtitle: update.notes == null ? null : Text(update.notes!, style: TextStyle(color: cs.onPrimaryContainer)),
          trailing: FilledButton(onPressed: onUpdate, child: const Text('Atualizar')),
        ),
      ),
    );
  }
}

/// WhatsApp de atendimento (número definido no painel admin, aba WhatsApp).
class SupportContact {
  SupportContact(this.number, this.message);
  final String number;
  final String message;

  static Future<SupportContact?> load(ApiClient api) async {
    try {
      final r = await api.getPublic('/api/v1/public/support');
      if (r == null || r['enabled'] != true) return null;
      return SupportContact(r['whatsapp'] as String, r['message'] as String);
    } catch (_) {
      return SupportContact('5547992841812', 'Olá! Preciso de ajuda com o sistema.'); // sem internet: número padrão
    }
  }

  Future<void> open({String? tenantCode}) {
    var text = message;
    if (tenantCode != null && tenantCode.isNotEmpty) text += '\n\nLoja: $tenantCode (app do celular)';
    return launchUrl(Uri.parse('https://wa.me/$number?text=${Uri.encodeComponent(text)}'), mode: LaunchMode.externalApplication);
  }
}

/// Botão flutuante verde do WhatsApp.
class WhatsappFab extends StatelessWidget {
  const WhatsappFab({super.key, required this.contact, this.tenantCode});
  final SupportContact contact;
  final String? tenantCode;
  @override
  Widget build(BuildContext context) => FloatingActionButton(
        heroTag: 'whatsapp',
        backgroundColor: const Color(0xFF25D366),
        foregroundColor: Colors.white,
        tooltip: 'Dúvidas? Fale com o suporte no WhatsApp',
        onPressed: () => contact.open(tenantCode: tenantCode),
        child: const Icon(Icons.chat),
      );
}

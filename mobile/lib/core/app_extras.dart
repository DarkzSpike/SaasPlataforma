import 'package:flutter/material.dart';
import 'package:package_info_plus/package_info_plus.dart';
import 'package:url_launcher/url_launcher.dart';

import 'api_client.dart';

/// Versão nova do app publicada no painel admin (aba Atualizações).
class AppUpdate {
  AppUpdate(this.versionName, this.url, this.notes, this.mandatory);
  final String versionName;
  final String url;
  final String? notes;
  final bool mandatory;

  /// Compara o versionCode publicado com o deste APK. Sem internet ou sem versão publicada: null.
  static Future<AppUpdate?> check(ApiClient api) async {
    try {
      final r = await api.getPublic('/api/v1/public/mobile/latest');
      if (r == null) return null;
      final current = int.tryParse((await PackageInfo.fromPlatform()).buildNumber) ?? 0;
      if ((r['versionCode'] as num).toInt() <= current) return null;
      return AppUpdate(r['versionName'] as String, r['url'] as String, r['notes'] as String?, r['mandatory'] == true);
    } catch (_) {
      return null;
    }
  }

  /// Abre o download no navegador; o Android instala por cima (mesma assinatura) sem perder o login.
  Future<void> open() => launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication);
}

/// Faixa "Nova versão disponível" no topo das telas.
class UpdateBanner extends StatelessWidget {
  const UpdateBanner({super.key, required this.update});
  final AppUpdate update;
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
          trailing: FilledButton(onPressed: update.open, child: const Text('Atualizar')),
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

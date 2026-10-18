import 'package:flutter/material.dart';

/// Identidade visual publicada para a empresa autenticada (mesmo schema de BrandingConfig no servidor).
/// O cache é sempre por tenant + versão, para que a identidade de uma empresa jamais apareça em outra.
class Branding {
  Branding({
    required this.tenantId,
    required this.version,
    required this.systemName,
    required this.welcomeMessage,
    required this.primary,
    required this.secondary,
    required this.accent,
    required this.background,
    required this.surface,
    required this.text,
    required this.darkBackground,
    required this.darkSurface,
    required this.darkText,
    required this.fontScale,
    required this.buttonStyle,
    required this.defaultMode,
  });

  final String tenantId;
  final int version;
  final String systemName;
  final String welcomeMessage;
  final Color primary, secondary, accent, background, surface, text, darkBackground, darkSurface, darkText;
  final double fontScale;
  final String buttonStyle;
  final String defaultMode;

  String get cacheKey => '$tenantId:$version';

  static Branding fallback() => Branding.fromJson({'tenantId': '', 'version': 0, 'config': <String, dynamic>{}});

  static Color _c(dynamic v, String def) {
    final s = (v is String && RegExp(r'^#[0-9A-Fa-f]{6}$').hasMatch(v)) ? v : def;
    return Color(int.parse('FF${s.substring(1)}', radix: 16));
  }

  factory Branding.fromJson(Map<String, dynamic> j) {
    final c = (j['config'] as Map?)?.cast<String, dynamic>() ?? {};
    final scale = (c['fontScale'] as num?)?.toDouble() ?? 1.0;
    return Branding(
      tenantId: (j['tenantId'] ?? '').toString(),
      version: (j['version'] as num?)?.toInt() ?? 0,
      systemName: (c['systemName'] as String?) ?? 'Gestão Comercial',
      welcomeMessage: (c['welcomeMessage'] as String?) ?? 'Bem-vindo',
      primary: _c(c['primaryColor'], '#1F5EFF'),
      secondary: _c(c['secondaryColor'], '#0F2A5C'),
      accent: _c(c['accentColor'], '#FFB020'),
      background: _c(c['backgroundColor'], '#F6F8FB'),
      surface: _c(c['surfaceColor'], '#FFFFFF'),
      text: _c(c['textColor'], '#14181F'),
      darkBackground: _c(c['darkBackgroundColor'], '#0E1116'),
      darkSurface: _c(c['darkSurfaceColor'], '#171B22'),
      darkText: _c(c['darkTextColor'], '#E8ECF2'),
      fontScale: scale.clamp(0.85, 1.3),
      buttonStyle: (c['buttonStyle'] as String?) ?? 'rounded',
      defaultMode: (c['defaultMode'] as String?) ?? 'system',
    );
  }

  ThemeMode get themeMode => switch (defaultMode) { 'light' => ThemeMode.light, 'dark' => ThemeMode.dark, _ => ThemeMode.system };

  double get _radius => switch (buttonStyle) { 'square' => 0, 'pill' => 999, _ => 8 };

  ThemeData theme(Brightness b) {
    final dark = b == Brightness.dark;
    final scheme = ColorScheme(
      brightness: b,
      primary: primary,
      onPrimary: Colors.white,
      secondary: secondary,
      onSecondary: Colors.white,
      tertiary: accent,
      error: const Color(0xFFC62F2F),
      onError: Colors.white,
      surface: dark ? darkSurface : surface,
      onSurface: dark ? darkText : text,
    );
    return ThemeData(
      colorScheme: scheme,
      scaffoldBackgroundColor: dark ? darkBackground : background,
      useMaterial3: true,
      appBarTheme: AppBarTheme(backgroundColor: secondary, foregroundColor: Colors.white),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(_radius))),
      ),
    );
  }
}

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

  /// O app segue o caixa (desktop): operação sempre no tema escuro com as cores da empresa.
  /// Só fica claro se a empresa escolheu "claro" no editor de identidade visual.
  ThemeMode get themeMode => defaultMode == 'light' ? ThemeMode.light : ThemeMode.dark;

  double get _radius => switch (buttonStyle) { 'square' => 0, 'pill' => 999, _ => 10 };

  static Color _mix(Color a, Color b, double t) => Color.lerp(a, b, t)!;

  ThemeData theme(Brightness b) {
    final dark = b == Brightness.dark;
    // Mesmos tons derivados do caixa (AppServices.ApplyBranding): superfícies 2/3 e borda a partir da superfície.
    final bg = dark ? darkBackground : background;
    final surf = dark ? darkSurface : surface;
    final fg = dark ? darkText : text;
    final surf2 = dark ? _mix(darkSurface, Colors.white, 0.06) : _mix(surface, background, 0.5);
    final surf3 = dark ? _mix(darkSurface, Colors.white, 0.12) : _mix(surface, Colors.black, 0.06);
    final border = dark ? _mix(darkSurface, Colors.white, 0.10) : _mix(surface, Colors.black, 0.12);
    final muted = _mix(fg, surf, 0.4);
    const danger = Color(0xFFF0525F);
    final scheme = ColorScheme(
      brightness: b,
      primary: primary,
      onPrimary: Colors.white,
      primaryContainer: _mix(primary, bg, 0.6),
      onPrimaryContainer: fg,
      secondary: accent,
      onSecondary: Colors.black,
      secondaryContainer: _mix(primary, surf, 0.7),
      onSecondaryContainer: fg,
      tertiary: accent,
      onTertiary: Colors.black,
      error: danger,
      onError: Colors.white,
      surface: surf,
      onSurface: fg,
      onSurfaceVariant: muted,
      outline: border,
      outlineVariant: border,
      surfaceContainerLowest: bg,
      surfaceContainerLow: surf,
      surfaceContainer: surf,
      surfaceContainerHigh: surf2,
      surfaceContainerHighest: surf3,
    );
    final shape = RoundedRectangleBorder(borderRadius: BorderRadius.circular(_radius));
    final inputRadius = BorderRadius.circular(10);
    return ThemeData(
      colorScheme: scheme,
      scaffoldBackgroundColor: bg,
      useMaterial3: true,
      appBarTheme: AppBarTheme(
        backgroundColor: surf, foregroundColor: fg, surfaceTintColor: Colors.transparent, elevation: 0,
        shape: Border(bottom: BorderSide(color: border)),
      ),
      cardTheme: CardThemeData(color: surf, surfaceTintColor: Colors.transparent, elevation: 0,
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12), side: BorderSide(color: border))),
      navigationBarTheme: NavigationBarThemeData(backgroundColor: surf, indicatorColor: _mix(primary, surf, 0.55), surfaceTintColor: Colors.transparent),
      dialogTheme: DialogThemeData(backgroundColor: surf, surfaceTintColor: Colors.transparent),
      bottomSheetTheme: BottomSheetThemeData(backgroundColor: surf, surfaceTintColor: Colors.transparent),
      dividerTheme: DividerThemeData(color: border),
      listTileTheme: ListTileThemeData(iconColor: muted),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: surf2,
        labelStyle: TextStyle(color: muted),
        border: OutlineInputBorder(borderRadius: inputRadius, borderSide: BorderSide(color: border)),
        enabledBorder: OutlineInputBorder(borderRadius: inputRadius, borderSide: BorderSide(color: border)),
        focusedBorder: OutlineInputBorder(borderRadius: inputRadius, borderSide: BorderSide(color: primary, width: 1.6)),
      ),
      filledButtonTheme: FilledButtonThemeData(style: FilledButton.styleFrom(backgroundColor: primary, foregroundColor: Colors.white, shape: shape, minimumSize: const Size(0, 48))),
      outlinedButtonTheme: OutlinedButtonThemeData(style: OutlinedButton.styleFrom(foregroundColor: fg, side: BorderSide(color: border), shape: shape, minimumSize: const Size(0, 46))),
      textButtonTheme: TextButtonThemeData(style: TextButton.styleFrom(foregroundColor: dark ? _mix(primary, Colors.white, 0.35) : primary)),
      floatingActionButtonTheme: FloatingActionButtonThemeData(backgroundColor: primary, foregroundColor: Colors.white),
      snackBarTheme: SnackBarThemeData(backgroundColor: surf3, contentTextStyle: TextStyle(color: fg), behavior: SnackBarBehavior.floating),
    );
  }
}

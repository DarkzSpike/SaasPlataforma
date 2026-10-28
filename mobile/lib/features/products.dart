import 'dart:typed_data';

import 'package:barcode_widget/barcode_widget.dart';
import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';
import 'package:intl/intl.dart';
import 'package:mobile_scanner/mobile_scanner.dart' hide Barcode;

import '../core/api_client.dart';
import '../core/pricing.dart';
import '../core/session.dart';

final _money = NumberFormat.simpleCurrency(locale: 'pt_BR');

int? parseCents(String s) {
  final t = s.replaceAll('R\$', '').replaceAll(' ', '').trim();
  if (t.isEmpty) return null;
  final v = double.tryParse(t.contains(',') ? t.replaceAll('.', '').replaceAll(',', '.') : t);
  return v == null || v < 0 ? null : (v * 100).round();
}

/// Produtos pelo celular: lista com busca, cadastro rápido (foto + nome + código de barras) e entrada de estoque.
class ProductsPage extends StatefulWidget {
  const ProductsPage({super.key, required this.api, required this.me, required this.storeId});
  final ApiClient api;
  final Me me;
  final String storeId;
  @override
  State<ProductsPage> createState() => _ProductsPageState();
}

class _ProductsPageState extends State<ProductsPage> {
  List<Map<String, dynamic>> items = [];
  String? error;
  bool loading = true;
  String search = '';
  bool onlyNoPrice = false;
  bool onlyLow = false;
  final searchCtrl = TextEditingController();

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    searchCtrl.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    setState(() => loading = true);
    try {
      // Com permissão de estoque: lista com quantidades. Sem ela: só o catálogo.
      final rows = widget.me.seesStock
          ? await widget.api.getList('/api/v1/stock/balances?storeId=${widget.storeId}')
          : (await widget.api.getList('/api/v1/products')).map((p) => {...(p as Map<String, dynamic>), 'productId': p['id'], 'hasImage': p['imageContentType'] != null}).toList();
      setState(() { items = rows.cast<Map<String, dynamic>>(); error = null; });
    } on ApiException catch (e) {
      setState(() => error = e.code == 'module_not_enabled' ? 'Módulo de estoque não contratado.' : e.message);
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => loading = false);
    }
  }

  Future<void> _scanSearch() async {
    final code = await Navigator.push<String>(context, MaterialPageRoute(builder: (_) => const BarcodeScanPage()));
    if (code == null) return;
    setState(() { search = code; searchCtrl.text = code; });
    final hit = items.where((p) => p['barcode'] == code || p['sku'] == code).toList();
    if (hit.isEmpty && mounted) {
      final create = await showDialog<bool>(context: context, builder: (c) => AlertDialog(
            title: const Text('Produto não cadastrado'),
            content: Text('Nenhum produto com o código $code.'),
            actions: [
              TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Fechar')),
              if (widget.me.registersProducts) FilledButton(onPressed: () => Navigator.pop(c, true), child: const Text('Cadastrar agora')),
            ],
          ));
      if (create == true) _new(barcode: code);
    }
  }

  Future<void> _new({String? barcode}) async {
    final created = await Navigator.push<bool>(context, MaterialPageRoute(
        builder: (_) => QuickProductPage(api: widget.api, me: widget.me, storeId: widget.storeId, initialBarcode: barcode)));
    if (created == true) _load();
  }

  @override
  Widget build(BuildContext context) {
    final q = search.toLowerCase();
    final shown = items.where((p) {
      if (onlyNoPrice && (p['priceCents'] as num? ?? 0) > 0) return false;
      if (onlyLow && p['low'] != true) return false;
      if (q.isEmpty) return true;
      return '${p['name']} ${p['sku']} ${p['barcode'] ?? ''}'.toLowerCase().contains(q);
    }).toList();
    return Scaffold(
      appBar: AppBar(title: const Text('Produtos'), actions: [
        IconButton(onPressed: _scanSearch, icon: const Icon(Icons.qr_code_scanner), tooltip: 'Buscar pelo código de barras'),
        IconButton(onPressed: _load, icon: const Icon(Icons.refresh), tooltip: 'Atualizar'),
      ]),
      floatingActionButton: widget.me.registersProducts
          ? FloatingActionButton.extended(onPressed: () => _new(), icon: const Icon(Icons.add_a_photo), label: const Text('Cadastrar produto'))
          : null,
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(padding: const EdgeInsets.fromLTRB(12, 8, 12, 96), children: [
          TextField(
            decoration: const InputDecoration(prefixIcon: Icon(Icons.search), hintText: 'Buscar por nome ou código'),
            controller: searchCtrl,
            onChanged: (v) => setState(() => search = v),
          ),
          Wrap(spacing: 8, children: [
            FilterChip(label: const Text('Sem preço'), selected: onlyNoPrice, onSelected: (v) => setState(() => onlyNoPrice = v)),
            if (widget.me.seesStock) FilterChip(label: const Text('Estoque baixo'), selected: onlyLow, onSelected: (v) => setState(() => onlyLow = v)),
          ]),
          if (error != null) Card(child: ListTile(leading: const Icon(Icons.warning_amber), title: Text(error!))),
          if (loading && items.isEmpty) const Padding(padding: EdgeInsets.all(32), child: Center(child: CircularProgressIndicator())),
          if (!loading && shown.isEmpty && error == null) const Padding(padding: EdgeInsets.all(24), child: Center(child: Text('Nenhum produto encontrado.'))),
          for (final p in shown)
            Card(
              child: ListTile(
                leading: ProductThumb(api: widget.api, productId: p['productId'] as String, hasImage: p['hasImage'] == true),
                title: Text(p['name'] as String),
                subtitle: Text([
                  (p['priceCents'] as num? ?? 0) == 0 ? 'SEM PREÇO' : _money.format((p['priceCents'] as num) / 100),
                  if (p['quantity'] != null) 'estoque ${p['quantity']}',
                  if (p['low'] == true) 'REPOR',
                ].join(' • ')),
                onTap: () async {
                  final changed = await Navigator.push<bool>(context, MaterialPageRoute(
                      builder: (_) => ProductDetailPage(api: widget.api, me: widget.me, storeId: widget.storeId, productId: p['productId'] as String)));
                  if (changed == true) _load();
                },
              ),
            ),
        ]),
      ),
    );
  }
}

class ProductThumb extends StatelessWidget {
  const ProductThumb({super.key, required this.api, required this.productId, required this.hasImage, this.size = 48});
  final ApiClient api;
  final String productId;
  final bool hasImage;
  final double size;

  @override
  Widget build(BuildContext context) {
    if (!hasImage) return CircleAvatar(radius: size / 2, child: const Icon(Icons.inventory_2_outlined));
    return FutureBuilder<List<int>?>(
      future: api.getBytes('/api/v1/products/$productId/image'),
      builder: (c, s) => s.data == null
          ? CircleAvatar(radius: size / 2, child: const Icon(Icons.image_outlined))
          : ClipRRect(borderRadius: BorderRadius.circular(8), child: Image.memory(Uint8List.fromList(s.data!), width: size, height: size, fit: BoxFit.cover)),
    );
  }
}

/// Desenho do código de barras (EAN-13 quando for válido; senão Code 128).
class BarcodeView extends StatelessWidget {
  const BarcodeView({super.key, required this.code});
  final String code;
  @override
  Widget build(BuildContext context) {
    final ean = RegExp(r'^\d{13}$').hasMatch(code) && Barcode.ean13().isValid(code);
    return Container(
      color: Colors.white,
      padding: const EdgeInsets.all(12),
      child: BarcodeWidget(
        barcode: ean ? Barcode.ean13(drawEndChar: true) : Barcode.code128(),
        data: code,
        height: 90,
        style: const TextStyle(color: Colors.black, fontSize: 16),
        errorBuilder: (c, e) => Text(code, style: const TextStyle(color: Colors.black)),
      ),
    );
  }
}

/// Leitor de código de barras pela câmera. Devolve o código lido.
class BarcodeScanPage extends StatefulWidget {
  const BarcodeScanPage({super.key});
  @override
  State<BarcodeScanPage> createState() => _BarcodeScanPageState();
}

class _BarcodeScanPageState extends State<BarcodeScanPage> {
  final controller = MobileScannerController(formats: const [
    BarcodeFormat.ean13, BarcodeFormat.ean8, BarcodeFormat.upcA, BarcodeFormat.upcE, BarcodeFormat.code128, BarcodeFormat.code39, BarcodeFormat.itf2of5,
  ]);
  bool done = false;

  @override
  void dispose() {
    controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Aponte para o código de barras'), actions: [
        IconButton(onPressed: () => controller.toggleTorch(), icon: const Icon(Icons.flashlight_on_outlined), tooltip: 'Lanterna'),
      ]),
      body: Stack(children: [
        MobileScanner(
          controller: controller,
          onDetect: (capture) {
            final v = capture.barcodes.map((b) => b.rawValue).whereType<String>().where((s) => s.trim().isNotEmpty).firstOrNull;
            if (v == null || done) return;
            done = true;
            Navigator.pop(context, v.trim());
          },
          errorBuilder: (c, e) => Center(child: Padding(
            padding: const EdgeInsets.all(24),
            child: Text('Não foi possível abrir a câmera. Libere a permissão de câmera para o app nas configurações do celular.\n(${e.errorCode.name})',
                textAlign: TextAlign.center),
          )),
        ),
        Center(child: Container(width: 280, height: 160, decoration: BoxDecoration(border: Border.all(color: Colors.greenAccent, width: 3), borderRadius: BorderRadius.circular(12)))),
      ]),
    );
  }
}

/// Valores digitados no bloco de preço.
class PriceInput {
  const PriceInput(this.costCents, this.markup, this.priceCents, this.error);
  final int costCents;
  final double? markup;
  final int priceCents;
  final String? error;
}

/// "Quanto você pagou" + "% de lucro" = preço de venda calculado na hora (ex.: R\$ 10,00 + 100% = R\$ 20,00).
/// Sem % o preço pode ser digitado direto (e o app mostra a % equivalente).
class PriceFields extends StatefulWidget {
  const PriceFields({super.key, required this.controller, this.initialCostCents = 0, this.initialMarkup, this.initialPriceCents = 0});
  final PriceFieldsController controller;
  final int initialCostCents;
  final double? initialMarkup;
  final int initialPriceCents;
  @override
  State<PriceFields> createState() => _PriceFieldsState();
}

class PriceFieldsController {
  _PriceFieldsState? _s;
  PriceInput read() => _s?._read() ?? const PriceInput(0, null, 0, null);
}

class _PriceFieldsState extends State<PriceFields> {
  final cost = TextEditingController();
  final markup = TextEditingController();
  final price = TextEditingController();

  String _fmt(int cents) => (cents / 100).toStringAsFixed(2).replaceAll('.', ',');

  @override
  void initState() {
    super.initState();
    widget.controller._s = this;
    if (widget.initialCostCents > 0) cost.text = _fmt(widget.initialCostCents);
    if (widget.initialMarkup != null) markup.text = Pricing.formatPercent(widget.initialMarkup!);
    if (widget.initialPriceCents > 0) price.text = _fmt(widget.initialPriceCents);
  }

  PriceInput _read() {
    final c = cost.text.trim().isEmpty ? 0 : parseCents(cost.text);
    if (c == null) return const PriceInput(0, null, 0, 'Valor pago (custo) inválido.');
    if (markup.text.trim().isNotEmpty) {
      final m = Pricing.parsePercent(markup.text);
      if (m == null) return const PriceInput(0, null, 0, '% de lucro inválida (ex.: 100).');
      if (c == 0) return const PriceInput(0, null, 0, 'Informe quanto pagou para calcular o preço pela %.');
      return PriceInput(c, m, Pricing.priceFromMarkup(c, m), null);
    }
    final p = price.text.trim().isEmpty ? 0 : parseCents(price.text);
    if (p == null) return const PriceInput(0, null, 0, 'Preço de venda inválido.');
    return PriceInput(c, null, p, null);
  }

  @override
  Widget build(BuildContext context) {
    final r = _read();
    final usesMarkup = markup.text.trim().isNotEmpty && r.error == null;
    final profit = r.priceCents - r.costCents;
    final implied = !usesMarkup && r.costCents > 0 && r.priceCents > 0 ? Pricing.markupFromPrice(r.costCents, r.priceCents) : null;
    final cs = Theme.of(context).colorScheme;
    return Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      TextField(controller: cost, onChanged: (_) => setState(() {}), keyboardType: const TextInputType.numberWithOptions(decimal: true),
          decoration: const InputDecoration(labelText: 'Quanto você pagou (custo)', prefixText: 'R\$ ', hintText: '10,00')),
      TextField(controller: markup, onChanged: (_) => setState(() {}), keyboardType: const TextInputType.numberWithOptions(decimal: true),
          decoration: const InputDecoration(labelText: '% de lucro em cima do custo', suffixText: '%', hintText: '100',
              helperText: 'Ex.: pagou 10,00 e colocou 100% → vende por 20,00')),
      const SizedBox(height: 8),
      if (usesMarkup)
        Card(
          color: cs.primaryContainer,
          child: ListTile(
            title: Text('Preço de venda: ${_money.format(r.priceCents / 100)}', style: TextStyle(fontWeight: FontWeight.bold, color: cs.onPrimaryContainer)),
            subtitle: Text('Lucro de ${_money.format(profit / 100)} por unidade', style: TextStyle(color: cs.onPrimaryContainer)),
          ),
        )
      else
        TextField(controller: price, onChanged: (_) => setState(() {}), keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: InputDecoration(labelText: 'Preço de venda (sem %: digite direto)', prefixText: 'R\$ ',
                helperText: implied != null ? 'Equivale a ${Pricing.formatPercent(implied)}% de lucro (${_money.format(profit / 100)} por unidade)'
                    : 'Se não souber, deixe em branco: o dono define depois.')),
      if (r.error != null) Padding(padding: const EdgeInsets.only(top: 6), child: Text(r.error!, style: TextStyle(color: cs.error))),
    ]);
  }
}

/// Cadastro rápido (dono ou funcionário liberado): foto, nome e código de barras — lido pela câmera,
/// digitado ou gerado pelo sistema (código único). Preço e quantidade são opcionais.
class QuickProductPage extends StatefulWidget {
  const QuickProductPage({super.key, required this.api, required this.me, required this.storeId, this.initialBarcode});
  final ApiClient api;
  final Me me;
  final String storeId;
  final String? initialBarcode;
  @override
  State<QuickProductPage> createState() => _QuickProductPageState();
}

class _QuickProductPageState extends State<QuickProductPage> {
  final name = TextEditingController();
  final barcode = TextEditingController();
  final prices = PriceFieldsController();
  final qty = TextEditingController();
  XFile? photo;
  Uint8List? preview;
  String? error;
  bool busy = false;
  bool generated = false;

  @override
  void initState() {
    super.initState();
    barcode.text = widget.initialBarcode ?? '';
    barcode.addListener(() => setState(() {}));
  }

  Future<void> _pick(ImageSource source) async {
    // Reduz a foto no próprio aparelho (o servidor aceita até 5 MB).
    final f = await ImagePicker().pickImage(source: source, maxWidth: 1600, imageQuality: 82);
    if (f == null) return;
    final bytes = await f.readAsBytes();
    setState(() { photo = f; preview = bytes; });
  }

  Future<void> _scan() async {
    final code = await Navigator.push<String>(context, MaterialPageRoute(builder: (_) => const BarcodeScanPage()));
    if (code != null) setState(() { barcode.text = code; generated = false; });
  }

  Future<void> _generate() async {
    try {
      final r = await widget.api.post('/api/v1/products/barcode/generate', {}) as Map<String, dynamic>;
      setState(() { barcode.text = r['barcode'] as String; generated = true; error = null; });
    } on ApiException catch (e) {
      setState(() => error = e.message);
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    }
  }

  Future<void> _save() async {
    final pr = prices.read();
    final p = pr.priceCents;
    if (name.text.trim().isEmpty) { setState(() => error = 'Digite o nome do produto.'); return; }
    if (pr.error != null) { setState(() => error = pr.error); return; }
    if (barcode.text.trim().isEmpty) { setState(() => error = 'Leia o código de barras do produto ou toque em "Gerar código".'); return; }
    setState(() { busy = true; error = null; });
    try {
      final created = await widget.api.post('/api/v1/products', {
        'sku': '', 'barcode': barcode.text.trim(), 'name': name.text.trim(),
        'unit': 'UN', 'priceCents': p, 'costCents': pr.costCents, 'markupPercent': pr.markup, 'minStock': 0,
      }) as Map<String, dynamic>;
      final id = created['id'] as String;
      if (photo != null) await widget.api.uploadImage('/api/v1/products/$id/image', preview!, photo!.name);
      final q = double.tryParse(qty.text.replaceAll(',', '.'));
      if (q != null && q > 0 && widget.me.entersStock) {
        await widget.api.post('/api/v1/stock/entries', {'storeId': widget.storeId, 'productId': id, 'quantity': q, 'unitCostCents': pr.costCents, 'reason': 'cadastro pelo celular'});
      }
      if (!mounted) return;
      await showDialog<void>(context: context, builder: (c) => AlertDialog(
            title: const Text('Produto cadastrado'),
            content: Column(mainAxisSize: MainAxisSize.min, children: [
              Text(name.text.trim()),
              const SizedBox(height: 12),
              BarcodeView(code: barcode.text.trim()),
              if (p == 0) const Padding(padding: EdgeInsets.only(top: 12), child: Text('Sem preço: o dono define o preço depois (filtro "Sem preço").')),
            ]),
            actions: [FilledButton(onPressed: () => Navigator.pop(c), child: const Text('OK'))],
          ));
      if (mounted) Navigator.pop(context, true);
    } on ApiException catch (e) {
      setState(() => error = switch (e.code) {
        'barcode_taken' => 'Este código de barras já está em outro produto.',
        'mobile_not_allowed' => 'Seu acesso ao app foi desligado pelo dono.',
        'permission_denied' => 'Seu usuário não pode cadastrar produtos.',
        _ => e.message,
      });
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final code = barcode.text.trim();
    return Scaffold(
      appBar: AppBar(title: const Text('Cadastrar produto')),
      body: ListView(padding: const EdgeInsets.all(16), children: [
        AspectRatio(
          aspectRatio: 4 / 3,
          child: Card(
            clipBehavior: Clip.antiAlias,
            child: InkWell(
              onTap: () => _pick(ImageSource.camera),
              child: preview == null
                  ? const Center(child: Column(mainAxisSize: MainAxisSize.min, children: [Icon(Icons.photo_camera_outlined, size: 56), Text('Toque para tirar a foto')]))
                  : Image.memory(preview!, fit: BoxFit.cover),
            ),
          ),
        ),
        Row(children: [
          Expanded(child: OutlinedButton.icon(onPressed: () => _pick(ImageSource.camera), icon: const Icon(Icons.photo_camera), label: const Text('Tirar foto'))),
          const SizedBox(width: 12),
          Expanded(child: OutlinedButton.icon(onPressed: () => _pick(ImageSource.gallery), icon: const Icon(Icons.photo_library), label: const Text('Galeria'))),
        ]),
        const SizedBox(height: 16),
        TextField(controller: name, textCapitalization: TextCapitalization.sentences, decoration: const InputDecoration(labelText: 'Nome do produto *')),
        const SizedBox(height: 12),
        TextField(
          controller: barcode,
          keyboardType: TextInputType.number,
          decoration: InputDecoration(
            labelText: 'Código de barras *',
            helperText: generated ? 'Código único gerado pelo sistema — imprima a etiqueta e cole no produto.' : 'Leia o código da embalagem. Se não tiver, toque em "Gerar código".',
            suffixIcon: IconButton(onPressed: _scan, icon: const Icon(Icons.qr_code_scanner), tooltip: 'Ler com a câmera'),
          ),
          onChanged: (_) => generated = false,
        ),
        const SizedBox(height: 8),
        Row(children: [
          Expanded(child: OutlinedButton.icon(onPressed: _scan, icon: const Icon(Icons.qr_code_scanner), label: const Text('Ler código'))),
          const SizedBox(width: 12),
          Expanded(child: OutlinedButton.icon(onPressed: _generate, icon: const Icon(Icons.auto_awesome), label: const Text('Gerar código'))),
        ]),
        if (code.isNotEmpty) Padding(padding: const EdgeInsets.only(top: 12), child: BarcodeView(code: code)),
        const SizedBox(height: 12),
        PriceFields(controller: prices),
        if (widget.me.entersStock)
          TextField(controller: qty, keyboardType: const TextInputType.numberWithOptions(decimal: true), decoration: const InputDecoration(labelText: 'Quantidade em estoque (opcional)')),
        if (error != null) Padding(padding: const EdgeInsets.only(top: 12), child: Text(error!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
        const SizedBox(height: 20),
        FilledButton.icon(onPressed: busy ? null : _save, icon: const Icon(Icons.check), label: Text(busy ? 'Salvando...' : 'Salvar produto')),
      ]),
    );
  }
}

/// Detalhe do produto: foto, código de barras desenhado, preço (quem pode editar) e entrada de estoque.
class ProductDetailPage extends StatefulWidget {
  const ProductDetailPage({super.key, required this.api, required this.me, required this.storeId, required this.productId});
  final ApiClient api;
  final Me me;
  final String storeId;
  final String productId;
  @override
  State<ProductDetailPage> createState() => _ProductDetailPageState();
}

class _ProductDetailPageState extends State<ProductDetailPage> {
  Map<String, dynamic>? p;
  String? error;
  bool changed = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final r = await widget.api.get('/api/v1/products/${widget.productId}');
      setState(() { p = r; error = null; });
    } catch (e) {
      setState(() => error = e is ApiException ? e.message : 'Sem conexão com o servidor.');
    }
  }

  Future<String?> _ask(String title, String label, {String initial = ''}) {
    final c = TextEditingController(text: initial);
    return showDialog<String>(context: context, builder: (d) => AlertDialog(
          title: Text(title),
          content: TextField(controller: c, autofocus: true, keyboardType: const TextInputType.numberWithOptions(decimal: true), decoration: InputDecoration(labelText: label)),
          actions: [
            TextButton(onPressed: () => Navigator.pop(d), child: const Text('Cancelar')),
            FilledButton(onPressed: () => Navigator.pop(d, c.text), child: const Text('Salvar')),
          ],
        ));
  }

  Future<void> _editPrice() async {
    final x = p!;
    final ctrl = PriceFieldsController();
    final ok = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      builder: (c) => Padding(
        padding: EdgeInsets.fromLTRB(16, 16, 16, 16 + MediaQuery.of(c).viewInsets.bottom),
        child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Text('Custo e preço de venda', style: Theme.of(c).textTheme.titleLarge),
          const SizedBox(height: 8),
          PriceFields(controller: ctrl, initialCostCents: (x['costCents'] as num).toInt(),
              initialMarkup: (x['markupPercent'] as num?)?.toDouble(), initialPriceCents: (x['priceCents'] as num).toInt()),
          const SizedBox(height: 12),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: const Text('Salvar')),
        ]),
      ),
    );
    if (ok != true) return;
    final r = ctrl.read();
    if (r.error != null) { if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(r.error!))); return; }
    try {
      await widget.api.put('/api/v1/products/${widget.productId}', {
        'sku': x['sku'], 'barcode': x['barcode'], 'name': x['name'], 'description': x['description'], 'unit': x['unit'],
        'priceCents': r.priceCents, 'costCents': r.costCents, 'markupPercent': r.markup, 'minStock': x['minStock'], 'categoryId': x['categoryId'], 'expectedVersion': x['version'],
        'ncm': x['ncm'], 'cest': x['cest'], 'cfop': x['cfop'], 'fiscalOrigin': x['fiscalOrigin'] ?? 0, 'icmsCst': x['icmsCst'],
        'ibsCbsCst': x['ibsCbsCst'], 'ibsCbsClass': x['ibsCbsClass'],
      });
      changed = true;
      await _load();
    } on ApiException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  Future<void> _entry() async {
    final v = await _ask('Entrada de mercadoria', 'Quantidade recebida');
    final q = v == null ? null : double.tryParse(v.replaceAll(',', '.'));
    if (q == null || q <= 0) return;
    try {
      await widget.api.post('/api/v1/stock/entries', {
        'storeId': widget.storeId, 'productId': widget.productId, 'quantity': q, 'unitCostCents': p!['costCents'], 'reason': 'entrada pelo celular',
      });
      changed = true;
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Entrada registrada.')));
    } on ApiException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  @override
  Widget build(BuildContext context) {
    final x = p;
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) { if (!didPop) Navigator.pop(context, changed); },
      child: Scaffold(
        appBar: AppBar(title: Text(x?['name'] as String? ?? 'Produto')),
        body: x == null
            ? Center(child: error == null ? const CircularProgressIndicator() : Text(error!))
            : ListView(padding: const EdgeInsets.all(16), children: [
                Center(child: ProductThumb(api: widget.api, productId: widget.productId, hasImage: x['imageContentType'] != null, size: 160)),
                const SizedBox(height: 16),
                Text(x['name'] as String, style: Theme.of(context).textTheme.headlineSmall),
                Text('Código interno: ${x['sku']}'),
                const SizedBox(height: 12),
                if ((x['barcode'] as String?)?.isNotEmpty == true) BarcodeView(code: x['barcode'] as String),
                const SizedBox(height: 12),
                Card(child: ListTile(
                  title: const Text('Custo e preço de venda'),
                  subtitle: Text((x['priceCents'] as num) == 0 ? 'SEM PREÇO — defina antes de vender' : [
                    'Venda ${_money.format((x['priceCents'] as num) / 100)}',
                    if ((x['costCents'] as num) > 0) 'pagou ${_money.format((x['costCents'] as num) / 100)}',
                    if (x['markupPercent'] != null) '${Pricing.formatPercent((x['markupPercent'] as num).toDouble())}% de lucro',
                    if ((x['costCents'] as num) > 0) 'lucro ${_money.format(((x['priceCents'] as num) - (x['costCents'] as num)) / 100)}/un.',
                  ].join(' • ')),
                  trailing: widget.me.registersProducts ? const Icon(Icons.edit) : null,
                  onTap: widget.me.registersProducts ? _editPrice : null,
                )),
                if (widget.me.entersStock)
                  Card(child: ListTile(leading: const Icon(Icons.add_box_outlined), title: const Text('Registrar entrada de mercadoria'), onTap: _entry)),
              ]),
      ),
    );
  }
}

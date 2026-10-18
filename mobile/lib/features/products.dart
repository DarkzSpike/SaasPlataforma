import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';
import 'package:intl/intl.dart';

import '../core/api_client.dart';

/// Estoque pelo celular: lista, entrada de mercadoria e cadastro de produto com foto da câmera.
class ProductsPage extends StatefulWidget {
  const ProductsPage({super.key, required this.api, required this.storeId});
  final ApiClient api;
  final String storeId;
  @override
  State<ProductsPage> createState() => _ProductsPageState();
}

class _ProductsPageState extends State<ProductsPage> {
  List<dynamic> items = [];
  String? error;
  bool loading = true;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() => loading = true);
    try {
      final rows = await widget.api.getList('/api/v1/stock/balances?storeId=${widget.storeId}');
      setState(() { items = rows; error = null; });
    } on ApiException catch (e) {
      setState(() => error = e.code == 'module_not_enabled' ? 'Módulo de estoque não contratado.' : e.message);
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => loading = false);
    }
  }

  Future<void> _entry(Map<String, dynamic> p) async {
    final qty = TextEditingController();
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text('Entrada: ${p['name']}'),
        content: TextField(controller: qty, keyboardType: const TextInputType.numberWithOptions(decimal: true), decoration: const InputDecoration(labelText: 'Quantidade recebida')),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Cancelar')),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: const Text('Registrar')),
        ],
      ),
    );
    final q = double.tryParse(qty.text.replaceAll(',', '.'));
    if (ok != true || q == null || q <= 0) return;
    try {
      await widget.api.post('/api/v1/stock/entries', {
        'storeId': widget.storeId, 'productId': p['productId'], 'quantity': q, 'unitCostCents': p['costCents'], 'reason': 'entrada pelo celular',
      });
      await _load();
    } on ApiException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      floatingActionButton: FloatingActionButton.extended(
        onPressed: () async {
          final created = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => NewProductPage(api: widget.api, storeId: widget.storeId)));
          if (created == true) _load();
        },
        icon: const Icon(Icons.add_a_photo),
        label: const Text('Novo produto'),
      ),
      body: RefreshIndicator(
        onRefresh: _load,
        child: loading && items.isEmpty
            ? const Center(child: CircularProgressIndicator())
            : ListView(padding: const EdgeInsets.fromLTRB(12, 12, 12, 90), children: [
                if (error != null) Card(child: ListTile(leading: const Icon(Icons.warning_amber), title: Text(error!))),
                for (final p in items.cast<Map<String, dynamic>>())
                  Card(
                    child: ListTile(
                      leading: _Thumb(api: widget.api, productId: p['productId'] as String, hasImage: p['hasImage'] == true),
                      title: Text(p['name'] as String),
                      subtitle: Text('${p['sku']} • estoque ${p['quantity']}${p['low'] == true ? ' • REPOR' : ''}'),
                      trailing: IconButton(icon: const Icon(Icons.add_box_outlined), tooltip: 'Entrada', onPressed: () => _entry(p)),
                    ),
                  ),
              ]),
      ),
    );
  }
}

class _Thumb extends StatelessWidget {
  const _Thumb({required this.api, required this.productId, required this.hasImage});
  final ApiClient api;
  final String productId;
  final bool hasImage;

  @override
  Widget build(BuildContext context) {
    if (!hasImage) return const CircleAvatar(child: Icon(Icons.inventory_2_outlined));
    return FutureBuilder<List<int>?>(
      future: api.getBytes('/api/v1/products/$productId/image'),
      builder: (c, s) => s.data == null
          ? const CircleAvatar(child: Icon(Icons.image_outlined))
          : ClipRRect(borderRadius: BorderRadius.circular(8), child: Image.memory(Uint8List.fromList(s.data!), width: 48, height: 48, fit: BoxFit.cover)),
    );
  }
}

class NewProductPage extends StatefulWidget {
  const NewProductPage({super.key, required this.api, required this.storeId});
  final ApiClient api;
  final String storeId;
  @override
  State<NewProductPage> createState() => _NewProductPageState();
}

class _NewProductPageState extends State<NewProductPage> {
  final name = TextEditingController();
  final sku = TextEditingController();
  final barcode = TextEditingController();
  final price = TextEditingController();
  final cost = TextEditingController();
  final qty = TextEditingController();
  XFile? photo;
  Uint8List? preview;
  String? error;
  bool busy = false;

  int? _cents(String s) {
    final v = double.tryParse(s.replaceAll('R\$', '').replaceAll('.', '').replaceAll(',', '.').trim());
    return v == null || v < 0 ? null : (v * 100).round();
  }

  Future<void> _pick(ImageSource source) async {
    // Reduz a foto no próprio aparelho (o servidor aceita até 5 MB).
    final f = await ImagePicker().pickImage(source: source, maxWidth: 1600, imageQuality: 82);
    if (f == null) return;
    final bytes = await f.readAsBytes();
    setState(() { photo = f; preview = bytes; });
  }

  Future<void> _save() async {
    final p = _cents(price.text), c = _cents(cost.text);
    if (name.text.trim().isEmpty || sku.text.trim().isEmpty || p == null || c == null) {
      setState(() => error = 'Preencha nome, SKU, preço e custo.');
      return;
    }
    setState(() { busy = true; error = null; });
    try {
      final created = await widget.api.post('/api/v1/products', {
        'sku': sku.text.trim(), 'barcode': barcode.text.trim().isEmpty ? null : barcode.text.trim(), 'name': name.text.trim(),
        'unit': 'UN', 'priceCents': p, 'costCents': c, 'minStock': 5,
      }) as Map<String, dynamic>;
      final id = created['id'] as String;
      final q = double.tryParse(qty.text.replaceAll(',', '.'));
      if (q != null && q > 0) {
        await widget.api.post('/api/v1/stock/entries', {'storeId': widget.storeId, 'productId': id, 'quantity': q, 'unitCostCents': c, 'reason': 'estoque inicial (celular)'});
      }
      if (photo != null) await widget.api.uploadImage('/api/v1/products/$id/image', preview!, photo!.name);
      if (mounted) Navigator.pop(context, true);
    } on ApiException catch (e) {
      setState(() => error = e.code == 'sku_taken' ? 'Esse SKU já existe.' : e.message);
    } catch (_) {
      setState(() => error = 'Sem conexão com o servidor.');
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final money = NumberFormat.simpleCurrency(locale: 'pt_BR');
    return Scaffold(
      appBar: AppBar(title: const Text('Novo produto')),
      body: ListView(padding: const EdgeInsets.all(16), children: [
        AspectRatio(
          aspectRatio: 4 / 3,
          child: Card(
            clipBehavior: Clip.antiAlias,
            child: preview == null
                ? const Center(child: Icon(Icons.photo_camera_outlined, size: 56))
                : Image.memory(preview!, fit: BoxFit.cover),
          ),
        ),
        Row(children: [
          Expanded(child: OutlinedButton.icon(onPressed: () => _pick(ImageSource.camera), icon: const Icon(Icons.photo_camera), label: const Text('Tirar foto'))),
          const SizedBox(width: 12),
          Expanded(child: OutlinedButton.icon(onPressed: () => _pick(ImageSource.gallery), icon: const Icon(Icons.photo_library), label: const Text('Galeria'))),
        ]),
        const SizedBox(height: 12),
        TextField(controller: name, decoration: const InputDecoration(labelText: 'Nome do produto')),
        TextField(controller: sku, decoration: const InputDecoration(labelText: 'Código interno (SKU)')),
        TextField(controller: barcode, decoration: const InputDecoration(labelText: 'Código de barras (opcional)'), keyboardType: TextInputType.number),
        TextField(controller: price, decoration: InputDecoration(labelText: 'Preço de venda', hintText: money.format(0)), keyboardType: const TextInputType.numberWithOptions(decimal: true)),
        TextField(controller: cost, decoration: const InputDecoration(labelText: 'Custo'), keyboardType: const TextInputType.numberWithOptions(decimal: true)),
        TextField(controller: qty, decoration: const InputDecoration(labelText: 'Quantidade inicial (opcional)'), keyboardType: const TextInputType.numberWithOptions(decimal: true)),
        if (error != null) Padding(padding: const EdgeInsets.only(top: 12), child: Text(error!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
        const SizedBox(height: 16),
        FilledButton.icon(onPressed: busy ? null : _save, icon: const Icon(Icons.check), label: Text(busy ? 'Salvando...' : 'Cadastrar produto')),
      ]),
    );
  }
}

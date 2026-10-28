import 'package:comercial_app/core/pricing.dart';
import 'package:comercial_app/core/session.dart';
import 'package:comercial_app/features/invoices.dart';
import 'package:comercial_app/features/products.dart';
import 'package:flutter_test/flutter_test.dart';

Me _me(List<String> roles, List<String> perms, {List<String> modules = const ['app_proprietario']}) =>
    Me(tenantCode: 'x', tenantName: 'X', roles: roles, permissions: perms.toSet(), modules: modules.toSet());

void main() {
  test('preço digitado vira centavos', () {
    expect(parseCents('12,90'), 1290);
    expect(parseCents('R\$ 1.234,50'), 123450);
    expect(parseCents('7.5'), 750);
    expect(parseCents(''), isNull);
    expect(parseCents('abc'), isNull);
  });

  test('estoquista liberado vê só Produtos e pode cadastrar', () {
    final m = _me(['stockist'], ['products.read', 'products.write', 'stock.read', 'stock.entry', 'mobile.access']);
    expect(m.seesDashboard, isFalse);
    expect(m.seesProducts, isTrue);
    expect(m.registersProducts, isTrue);
    expect(m.managesTeam, isFalse);
  });

  test('dono vê painel, equipe e lucro', () {
    final m = _me(['owner'], ['reports.read', 'reports.financial', 'users.manage', 'products.read', 'products.write']);
    expect(m.isOwner, isTrue);
    expect(m.seesDashboard, isTrue);
    expect(m.managesTeam, isTrue);
    expect(m.seesProfit, isTrue);
  });

  test('sem o módulo do app do proprietário o painel não aparece', () {
    expect(_me(['owner'], ['reports.read'], modules: []).seesDashboard, isFalse);
  });

  test('paguei 10 + 100% = vende por 20 (mesma regra do servidor)', () {
    expect(Pricing.priceFromMarkup(1000, 100), 2000);
    expect(Pricing.priceFromMarkup(1000, 35.5), 1355);
    expect(Pricing.priceFromMarkup(799, 50), 1199);
    expect(Pricing.markupFromPrice(1000, 1990), 99);
    expect(Pricing.markupFromPrice(0, 1990), isNull);
    expect(Pricing.parsePercent('35,5%'), 35.5);
    expect(Pricing.parsePercent('-1'), isNull);
  });

  test('situação da fatura igual à do caixa', () {
    expect(invoiceStatus({'status': 'paga'}), 'Paga');
    expect(invoiceStatus({'status': 'cancelada'}), 'Cancelada');
    expect(invoiceStatus({'status': 'aberta', 'overdue': true, 'daysToDue': -2}), 'Vencida');
    expect(invoiceStatus({'status': 'aberta', 'overdue': false, 'daysToDue': 0}), 'Vence hoje');
    expect(invoiceStatus({'status': 'aberta', 'overdue': false, 'daysToDue': 2}), 'Vence em 2 dia(s)');
    expect(invoiceStatus({'status': 'aberta', 'overdue': false, 'daysToDue': 20}), 'Em aberto');
  });

  test('dono paga faturas; estoquista não', () {
    expect(_me(['owner'], ['billing.manage']).paysInvoices, isTrue);
    expect(_me(['stockist'], ['products.write']).paysInvoices, isFalse);
  });
}

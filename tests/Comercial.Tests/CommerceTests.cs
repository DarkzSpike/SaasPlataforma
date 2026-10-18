using System.Net;
using Comercial.Domain;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Tests;

/// <summary>Integridade financeira e de estoque: idempotência, preço, desconto, concorrência, caixa, fiscal.</summary>
public class CommerceTests(ApiFactory f) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly Api api = new(f);
    private TenantInfo T = null!;
    private Guid product, cash;

    public async Task InitializeAsync()
    {
        T = await api.CreateTenant("com-" + Guid.NewGuid().ToString("N")[..6]);
        product = await api.CreateProduct(T.OwnerToken, "C1", price: 1000, cost: 600);
        await api.StockEntry(T.OwnerToken, T.StoreId, product, 100);
        cash = await api.OpenCash(T.OwnerToken, T.StoreId, 5000);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Venda_repetida_com_mesma_chave_nao_duplica()
    {
        var before = await api.Balance(T.OwnerToken, T.StoreId, product);
        var req = Api.Sale(T.StoreId, cash, product, key: "dup-" + Guid.NewGuid().ToString("N"));
        var first = await Api.Expect(await api.Post("/api/v1/sales", T.OwnerToken, req), HttpStatusCode.Created);
        var second = await Api.Expect(await api.Post("/api/v1/sales", T.OwnerToken, req), HttpStatusCode.OK);
        Assert.True(second.GetProperty("duplicate").GetBoolean());
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal(1, await f.WithDb(db => db.Sales.IgnoreQueryFilters().CountAsync(s => s.IdempotencyKey == req.IdempotencyKey)));
        Assert.Equal(before - 1, await api.Balance(T.OwnerToken, T.StoreId, product));
    }

    [Fact]
    public async Task Requisicoes_simultaneas_com_mesma_chave_geram_uma_venda()
    {
        var req = Api.Sale(T.StoreId, cash, product, key: "race-" + Guid.NewGuid().ToString("N"));
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => api.Post("/api/v1/sales", T.OwnerToken, req)));
        Assert.All(results, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));
        Assert.Equal(1, await f.WithDb(db => db.Sales.IgnoreQueryFilters().CountAsync(s => s.IdempotencyKey == req.IdempotencyKey)));
    }

    [Fact]
    public async Task Preco_enviado_pelo_cliente_nunca_prevalece()
    {
        var manipulated = await api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, product, pay: 1, clientPrice: 1));
        Assert.Equal(HttpStatusCode.Conflict, manipulated.StatusCode);
        Assert.Equal("price_mismatch", await Api.Code(manipulated));

        var underpaid = await api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, product, pay: 500));
        Assert.Equal("insufficient_payment", await Api.Code(underpaid));

        var ok = await Api.Expect(await api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, product, qty: 2, pay: 2000)), HttpStatusCode.Created);
        Assert.Equal(2000, ok.GetProperty("totalCents").GetInt64());
    }

    [Fact]
    public async Task Desconto_acima_do_limite_exige_permissao_de_gerente()
    {
        var (_, op, _) = await api.CreateUser(T, "operator");
        var opCash = await api.OpenCash(op, T.StoreId);
        var denied = await api.Post("/api/v1/sales", op, Api.Sale(T.StoreId, opCash, product, pay: 800, discount: 200)); // 20%
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("discount_not_authorized", await Api.Code(denied));
        await Api.Expect(await api.Post("/api/v1/sales", op, Api.Sale(T.StoreId, opCash, product, pay: 950, discount: 50)), HttpStatusCode.Created); // 5%
        await Api.Expect(await api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, product, pay: 800, discount: 200)), HttpStatusCode.Created);
        var negative = await api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, product, pay: 1000, discount: -100));
        Assert.Equal("invalid_discount", await Api.Code(negative));
    }

    [Fact]
    public async Task Pagamento_em_cartao_nao_gera_troco_e_quantidade_invalida_e_recusada()
    {
        Assert.Equal("overpayment", await Api.Code(await api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, product, pay: 5000, method: 3))));
        Assert.Equal("invalid_quantity", await Api.Code(await api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, product, qty: -1))));
        Assert.Equal("invalid_quantity", await Api.Code(await api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, product, qty: 0.0001m))));
    }

    [Fact]
    public async Task Vendas_concorrentes_nao_corrompem_saldo_de_estoque()
    {
        var p = await api.CreateProduct(T.OwnerToken, "CONC", price: 100, cost: 50);
        await api.StockEntry(T.OwnerToken, T.StoreId, p, 50);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, p, pay: 100))));
        var ok = results.Count(r => r.IsSuccessStatusCode);
        Assert.Equal(20, ok);
        Assert.Equal(30m, await api.Balance(T.OwnerToken, T.StoreId, p));
        var movementsSum = await f.WithDb(async db => (await db.StockMovements.IgnoreQueryFilters().Where(m => m.ProductId == p).Select(m => m.Quantity).ToListAsync()).Sum());
        Assert.Equal(30m, movementsSum);
    }

    [Fact]
    public async Task Cancelamento_exige_permissao_motivo_e_estorna_estoque_e_caixa()
    {
        var sale = await Api.Expect(await api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, product, qty: 3, pay: 5000)), HttpStatusCode.Created);
        var id = sale.GetProperty("id").GetGuid();
        var balance = await api.Balance(T.OwnerToken, T.StoreId, product);
        var (_, op, _) = await api.CreateUser(T, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post($"/api/v1/sales/{id}/cancel", op, new { reason = "cliente desistiu" })).StatusCode);
        Assert.Equal("reason_required", await Api.Code(await api.Post($"/api/v1/sales/{id}/cancel", T.OwnerToken, new { reason = "" })));

        var current = await Api.Expect(await api.Get("/api/v1/cash/sessions/current", T.OwnerToken), HttpStatusCode.OK);
        var cashBefore = current.GetProperty("expectedCashCents").GetInt64();
        await Api.Expect(await api.Post($"/api/v1/sales/{id}/cancel", T.OwnerToken, new { reason = "cliente desistiu" }), HttpStatusCode.OK);
        Assert.Equal(balance + 3, await api.Balance(T.OwnerToken, T.StoreId, product));
        var after = await Api.Expect(await api.Get("/api/v1/cash/sessions/current", T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(cashBefore - 3000, after.GetProperty("expectedCashCents").GetInt64());
        Assert.Equal("sale_not_completed", await Api.Code(await api.Post($"/api/v1/sales/{id}/cancel", T.OwnerToken, new { reason = "de novo pls" })));
        // A venda continua existindo (cancelada), nunca é apagada.
        Assert.Equal(SaleStatus.Cancelled, await f.WithDb(db => db.Sales.IgnoreQueryFilters().Where(s => s.Id == id).Select(s => s.Status).FirstAsync()));
    }

    [Fact]
    public async Task Caixa_calcula_diferenca_e_exige_justificativa()
    {
        var (_, op, _) = await api.CreateUser(T, "operator");
        var c = await api.OpenCash(op, T.StoreId, 10000);
        Assert.Equal("cash_already_open", await Api.Code(await api.Post("/api/v1/cash/sessions", op, new { storeId = T.StoreId, openingCents = 0 })));
        await Api.Expect(await api.Post($"/api/v1/cash/sessions/{c}/supply", op, new { amountCents = 2000, reason = "troco" }), HttpStatusCode.OK);
        await Api.Expect(await api.Post($"/api/v1/cash/sessions/{c}/withdrawal", op, new { amountCents = 5000, reason = "sangria" }), HttpStatusCode.OK);
        Assert.Equal("insufficient_cash", await Api.Code(await api.Post($"/api/v1/cash/sessions/{c}/withdrawal", op, new { amountCents = 999999, reason = "sangria" })));
        await Api.Expect(await api.Post("/api/v1/sales", op, Api.Sale(T.StoreId, c, product, pay: 1500)), HttpStatusCode.Created); // troco 500

        // esperado = 10000 + 2000 - 5000 + 1000 = 8000
        Assert.Equal("justification_required", await Api.Code(await api.Post($"/api/v1/cash/sessions/{c}/close", op, new { declaredCashCents = 7900 })));
        var closed = await Api.Expect(await api.Post($"/api/v1/cash/sessions/{c}/close", op, new { declaredCashCents = 7900, justification = "faltou moeda" }), HttpStatusCode.OK);
        Assert.Equal(8000, closed.GetProperty("expectedCashCents").GetInt64());
        Assert.Equal(-100, closed.GetProperty("differenceCents").GetInt64());
        Assert.Equal("cash_not_open", await Api.Code(await api.Post("/api/v1/sales", op, Api.Sale(T.StoreId, c, product))));
    }

    [Fact]
    public async Task Operador_nao_usa_caixa_de_outro_operador()
    {
        var (_, op, _) = await api.CreateUser(T, "operator");
        var res = await api.Post("/api/v1/sales", op, Api.Sale(T.StoreId, cash, product));
        Assert.Equal("not_owner", await Api.Code(res));
    }

    [Fact]
    public async Task Ajuste_de_estoque_exige_motivo_e_movimentacoes_sao_imutaveis()
    {
        Assert.Equal("reason_required", await Api.Code(await api.Post("/api/v1/stock/adjustments", T.OwnerToken, new { storeId = T.StoreId, productId = product, quantity = -2, reason = "" })));
        await Api.Expect(await api.Post("/api/v1/stock/adjustments", T.OwnerToken, new { storeId = T.StoreId, productId = product, quantity = -2, reason = "avaria na prateleira" }), HttpStatusCode.NoContent);
        await f.WithDb(async db =>
        {
            var m = await db.StockMovements.IgnoreQueryFilters().FirstAsync(x => x.ProductId == product);
            m.Quantity = 999;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        });
        await f.WithDb(async db =>
        {
            var s = await db.Sales.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TenantId == T.Id);
            if (s is null) return;
            db.Sales.Remove(s);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Documento_fiscal_nunca_aparece_autorizado_sem_provedor_real()
    {
        var sale = await Api.Expect(await api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, product)), HttpStatusCode.Created);
        var docs = await Api.Expect(await api.Get("/api/v1/fiscal/documents", T.OwnerToken), HttpStatusCode.OK);
        var doc = docs.EnumerateArray().First(d => d.GetProperty("saleId").GetGuid() == sale.GetProperty("id").GetGuid());
        Assert.Equal((int)FiscalStatus.PendingTransmission, doc.GetProperty("status").GetInt32());
        var transmitted = await Api.Expect(await api.Post($"/api/v1/fiscal/documents/{doc.GetProperty("id").GetGuid()}/transmit", T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal((int)FiscalStatus.PendingTransmission, transmitted.GetProperty("status").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, transmitted.GetProperty("protocol").ValueKind);
    }

    [Fact]
    public async Task Edicao_concorrente_de_produto_e_detectada()
    {
        var p = await api.CreateProduct(T.OwnerToken, "VER");
        var body = new { sku = "VER", name = "v2", unit = "UN", priceCents = 2000, costCents = 600, minStock = 0, expectedVersion = 1 };
        await Api.Expect(await api.Put($"/api/v1/products/{p}", T.OwnerToken, body), HttpStatusCode.OK);
        var stale = await api.Put($"/api/v1/products/{p}", T.OwnerToken, body);
        Assert.Equal("version_conflict", await Api.Code(stale));
    }

    [Fact]
    public async Task Lucratividade_calcula_lucro_margem_por_produto_e_exige_modulo_e_permissao()
    {
        var t = await api.CreateTenant("luc-" + Guid.NewGuid().ToString("N")[..6]);
        var p = await api.CreateProduct(t.OwnerToken, "L1", price: 1000, cost: 400);
        var c = await api.OpenCash(t.OwnerToken, t.StoreId);
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, c, p, qty: 2, pay: 2000)), HttpStatusCode.Created);
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, c, p, pay: 900, discount: 100)), HttpStatusCode.Created);
        var url = $"/api/v1/reports/profitability?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}";

        // Plano profissional não inclui Lucratividade: bloqueado no servidor.
        Assert.Equal("module_not_enabled", await Api.Code(await api.Get(url, t.OwnerToken)));
        var admin = await api.AdminToken();
        await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/modules/lucratividade/enable", admin, new { confirmTenantCode = t.Code, reason = "teste lucratividade" }, api.Reauth()), HttpStatusCode.OK);

        var r = await Api.Expect(await api.Get(url, t.OwnerToken), HttpStatusCode.OK);
        var totals = r.GetProperty("totals");
        Assert.Equal(2900, totals.GetProperty("revenueCents").GetInt64());
        Assert.Equal(1200, totals.GetProperty("costCents").GetInt64());
        Assert.Equal(1700, totals.GetProperty("grossProfitCents").GetInt64());
        Assert.Equal(100, totals.GetProperty("saleLevelDiscountCents").GetInt64());
        Assert.Equal(58.6m, totals.GetProperty("marginPercent").GetDecimal());
        var prod = r.GetProperty("products")[0];
        Assert.Equal(3m, prod.GetProperty("quantity").GetDecimal());
        Assert.Equal(1800, prod.GetProperty("grossProfitCents").GetInt64());
        Assert.Equal(1, r.GetProperty("daily").GetArrayLength());

        // Gerente não tem reports.financial.
        var (_, manager, _) = await api.CreateUser(t, "manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Get(url, manager)).StatusCode);
    }

    [Fact]
    public async Task Historico_de_vendas_traz_itens_pagamentos_e_operador()
    {
        var sale = await Api.Expect(await api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, product, qty: 2, pay: 2500)), HttpStatusCode.Created);
        var list = await Api.Expect(await api.Get("/api/v1/sales", T.OwnerToken), HttpStatusCode.OK);
        var row = list.EnumerateArray().First(s => s.GetProperty("id").GetGuid() == sale.GetProperty("id").GetGuid());
        Assert.Equal(1, row.GetProperty("itemsCount").GetInt32());
        Assert.Equal(2m, row.GetProperty("quantity").GetDecimal());
        Assert.Equal("Dono " + T.Code, row.GetProperty("operatorName").GetString());
        Assert.Equal(2500, row.GetProperty("payments")[0].GetProperty("amountCents").GetInt64());
        Assert.Equal(500, row.GetProperty("changeCents").GetInt64());
        var (_, op, _) = await api.CreateUser(T, "stockist");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Get("/api/v1/sales", op)).StatusCode);
    }

    [Fact]
    public void Dia_e_mes_seguem_o_fuso_da_loja()
    {
        var tz = Comercial.Api.Services.StoreClock.Find("America/Sao_Paulo");
        // 01:30 UTC de 18/10 ainda é 17/10 em São Paulo (22:30).
        var start = Comercial.Api.Services.StoreClock.StartOfDay(new DateTimeOffset(2026, 10, 18, 1, 30, 0, TimeSpan.Zero), tz);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 3, 0, 0, TimeSpan.Zero), start);
        var month = Comercial.Api.Services.StoreClock.StartOfMonth(new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.Zero), tz);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.Zero), month);
    }

    [Fact]
    public async Task Relatorio_diferencia_faturamento_cancelamentos_e_lucro_bruto()
    {
        var t = await api.CreateTenant("rep-" + Guid.NewGuid().ToString("N")[..6]);
        var p = await api.CreateProduct(t.OwnerToken, "R1", price: 1000, cost: 400);
        var c = await api.OpenCash(t.OwnerToken, t.StoreId);
        await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, c, p, qty: 2, pay: 2000));
        var s2 = await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, c, p, pay: 1000)), HttpStatusCode.Created);
        await api.Post($"/api/v1/sales/{s2.GetProperty("id").GetGuid()}/cancel", t.OwnerToken, new { reason = "teste relatorio" });
        var url = $"/api/v1/reports/sales-summary?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}";
        var r = await Api.Expect(await api.Get(url, t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(2000, r.GetProperty("revenueCents").GetInt64());
        Assert.Equal(1000, r.GetProperty("cancelledCents").GetInt64());
        Assert.Equal(1200, r.GetProperty("grossProfitCents").GetInt64());
    }
}

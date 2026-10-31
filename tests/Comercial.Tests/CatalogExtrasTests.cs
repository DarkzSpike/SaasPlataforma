using System.Net;
using Comercial.Domain;
using Comercial.Shared.Contracts;
using Comercial.Shared.Promotions;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Tests;

/// <summary>Grade, kits, códigos adicionais, promoções/cupom, fiado e orçamentos.</summary>
public class CatalogExtrasTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private readonly Api api = new(f);

    private async Task Enable(TenantInfo t, params string[] keys)
    {
        var admin = await api.AdminToken();
        foreach (var k in keys)
            await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/modules/{k}/enable", admin, new { confirmTenantCode = t.Code, reason = "teste do fiado" }, api.Reauth()), HttpStatusCode.OK);
    }

    private async Task<(TenantInfo T, Guid Cash)> Store()
    {
        var t = await api.CreateTenant("cx-" + Guid.NewGuid().ToString("N")[..6]);
        return (t, await api.OpenCash(t.OwnerToken, t.StoreId, 0));
    }

    [Fact]
    public void Motor_de_promocoes()
    {
        var p = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        PromoRule R(PromoType ty, int buy = 0, int pay = 0, decimal min = 0, long unit = 0, decimal pct = 0, string? cpn = null, Guid? prod = null) =>
            new(Guid.NewGuid(), ty.ToString(), ty, prod ?? p, buy, pay, min, unit, pct, cpn, null, null);
        Assert.Equal(1000, PromotionEngine.Apply([new(p, 3, 1000)], [R(PromoType.BuyXPayY, 3, 2)], now, null)[0].DiscountCents);       // leve 3 pague 2
        Assert.Equal(2000, PromotionEngine.Apply([new(p, 7, 1000)], [R(PromoType.BuyXPayY, 3, 2)], now, null)[0].DiscountCents);       // 7 un = 2 grátis
        Assert.Equal(1000, PromotionEngine.Apply([new(p, 10, 1000)], [R(PromoType.QuantityPrice, min: 10, unit: 900)], now, null)[0].DiscountCents); // atacarejo
        Assert.Equal(0, PromotionEngine.Apply([new(p, 9, 1000)], [R(PromoType.QuantityPrice, min: 10, unit: 900)], now, null)[0].DiscountCents);
        var best = PromotionEngine.Apply([new(p, 3, 1000)], [R(PromoType.BuyXPayY, 3, 2), R(PromoType.PercentOff, pct: 10)], now, null)[0];
        Assert.Equal(1000, best.DiscountCents);                                                                                           // não acumulam: vale a melhor
        var cupom = PromotionEngine.Apply([new(p, 1, 1000)], [R(PromoType.PercentOff, pct: 10), R(PromoType.Coupon, pct: 10, cpn: "VOLTE10", prod: Guid.Empty) with { ProductId = null }], now, "volte10")[0];
        Assert.Equal(100 + 90, cupom.DiscountCents);                                                                                     // cupom sobre o que sobrou
        var expirada = R(PromoType.PercentOff, pct: 50) with { EndsAt = now.AddMinutes(-1) };
        Assert.Equal(0, PromotionEngine.Apply([new(p, 1, 1000)], [expirada], now, null)[0].DiscountCents);
        Assert.NotNull(PromotionEngine.Validate(R(PromoType.BuyXPayY, 2, 2)));
    }

    [Fact]
    public async Task Grade_cria_variacoes_e_pai_nao_e_vendido_direto()
    {
        var (t, cash) = await Store();
        var shirt = await api.CreateProduct(t.OwnerToken, "CAM", price: 5000);
        var vars = await Api.Expect(await api.Post($"/api/v1/products/{shirt}/variants", t.OwnerToken, new { attributes = new Dictionary<string, string[]> { ["Tamanho"] = ["P", "M"], ["Cor"] = ["Azul", "Preto"] } }), HttpStatusCode.OK);
        Assert.Equal(4, vars.GetArrayLength());
        Assert.Contains(vars.EnumerateArray(), v => v.GetProperty("sku").GetString() == "CAM-M-AZUL" && v.GetProperty("name").GetString() == "Camiseta CAM - M / Azul" || v.GetProperty("variantLabel").GetString() == "M / Azul");
        Assert.Equal(0, (await Api.Expect(await api.Post($"/api/v1/products/{shirt}/variants", t.OwnerToken, new { attributes = new Dictionary<string, string[]> { ["Tamanho"] = ["P", "M"], ["Cor"] = ["Azul", "Preto"] } }), HttpStatusCode.OK)).GetArrayLength()); // não duplica
        Assert.Equal("variant_required", await Api.Code(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, shirt, pay: 5000))));
        var m = vars.EnumerateArray().First().GetProperty("id").GetGuid();
        await api.StockEntry(t.OwnerToken, t.StoreId, m, 5);
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, m, pay: 5000)), HttpStatusCode.Created);
        Assert.Equal(4m, await api.Balance(t.OwnerToken, t.StoreId, m)); // estoque por variação
    }

    [Fact]
    public async Task Kit_baixa_componentes_e_cancelamento_devolve()
    {
        var (t, cash) = await Store();
        var shampoo = await api.CreateProduct(t.OwnerToken, "SHA", price: 2000, cost: 800);
        var cond = await api.CreateProduct(t.OwnerToken, "CON", price: 2500, cost: 900);
        await api.StockEntry(t.OwnerToken, t.StoreId, shampoo, 10);
        await api.StockEntry(t.OwnerToken, t.StoreId, cond, 10);
        var kit = await api.CreateProduct(t.OwnerToken, "KIT", price: 4000);
        var k = await Api.Expect(await api.Put($"/api/v1/products/{kit}/components", t.OwnerToken, new[] { new { productId = shampoo, quantity = 1m }, new { productId = cond, quantity = 2m } }), HttpStatusCode.OK);
        Assert.True(k.GetProperty("isKit").GetBoolean());
        Assert.Equal(800 + 1800, k.GetProperty("costCents").GetInt64());
        Assert.Equal("invalid_kit", await Api.Code(await api.Put($"/api/v1/products/{kit}/components", t.OwnerToken, new[] { new { productId = kit, quantity = 1m } })));

        var sale = await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, kit, qty: 2, pay: 8000)), HttpStatusCode.Created);
        Assert.Equal(8m, await api.Balance(t.OwnerToken, t.StoreId, shampoo));
        Assert.Equal(6m, await api.Balance(t.OwnerToken, t.StoreId, cond));
        await Api.Expect(await api.Post($"/api/v1/sales/{sale.GetProperty("id").GetGuid()}/cancel", t.OwnerToken, new { reason = "cliente desistiu" }), HttpStatusCode.OK);
        Assert.Equal(10m, await api.Balance(t.OwnerToken, t.StoreId, shampoo));
        Assert.Equal(10m, await api.Balance(t.OwnerToken, t.StoreId, cond));
    }

    [Fact]
    public async Task Codigos_de_barras_adicionais_sao_unicos()
    {
        var (t, _) = await Store();
        var a = await api.CreateProduct(t.OwnerToken, "AB12");
        var b = await api.CreateProduct(t.OwnerToken, "BC34");
        await Api.Expect(await api.Post($"/api/v1/products/{a}/barcodes", t.OwnerToken, new { code = "7891234500011" }), HttpStatusCode.OK);
        Assert.Equal("barcode_taken", await Api.Code(await api.Post($"/api/v1/products/{b}/barcodes", t.OwnerToken, new { code = "7891234500011" })));
        Assert.Equal("barcode_taken", await Api.Code(await api.Post($"/api/v1/products/{b}/barcodes", t.OwnerToken, new { code = "AB12" })));
        var all = await Api.Expect(await api.Get("/api/v1/catalog/barcodes", t.OwnerToken), HttpStatusCode.OK);
        Assert.Single(all.EnumerateArray());
        await Api.Expect(await api.Delete($"/api/v1/products/{a}/barcodes/7891234500011", t.OwnerToken), HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Editar_produto_nao_repete_codigo_de_barras_nem_sku()
    {
        var (t, _) = await Store();
        var a = await api.CreateProduct(t.OwnerToken, "ED12");
        var b = await api.CreateProduct(t.OwnerToken, "ED34");
        object Body(string sku, string? barcode) => new { sku, barcode, name = "Produto " + sku, unit = "UN", priceCents = 1000, costCents = 600, minStock = 0m };
        Assert.Equal("barcode_taken", await Api.Code(await api.Put($"/api/v1/products/{b}", t.OwnerToken, Body("ED34", "789ED12"))));
        Assert.Equal("sku_taken", await Api.Code(await api.Put($"/api/v1/products/{b}", t.OwnerToken, Body("ED12", "789ED34"))));
        // O próprio código continua valendo ao salvar; e um código gerado pelo sistema nunca colide.
        await Api.Expect(await api.Put($"/api/v1/products/{a}", t.OwnerToken, Body("ED12", "789ED12")), HttpStatusCode.OK);
        var gen = (await Api.Expect(await api.Post("/api/v1/products/barcode/generate", t.OwnerToken, new { }), HttpStatusCode.OK)).GetProperty("barcode").GetString();
        await Api.Expect(await api.Put($"/api/v1/products/{b}", t.OwnerToken, Body("ED34", gen)), HttpStatusCode.OK);
        Assert.Equal("barcode_taken", await Api.Code(await api.Put($"/api/v1/products/{a}", t.OwnerToken, Body("ED12", gen))));
    }

    [Fact]
    public async Task Promocao_e_cupom_aplicados_pelo_servidor()
    {
        var (t, cash) = await Store();
        var p = await api.CreateProduct(t.OwnerToken, "PR", price: 1000);
        await api.StockEntry(t.OwnerToken, t.StoreId, p, 20);
        await Api.Expect(await api.Post("/api/v1/promotions", t.OwnerToken, new { name = "Leve 3 pague 2", type = 1, productId = p, buyQty = 3, payQty = 2 }), HttpStatusCode.OK);
        await Api.Expect(await api.Post("/api/v1/promotions", t.OwnerToken, new { name = "Cupom VOLTE10", type = 4, percent = 10m, couponCode = "volte10" }), HttpStatusCode.OK);
        var (_, op, _) = await api.CreateUser(t, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post("/api/v1/promotions", op, new { name = "x", type = 3, percent = 50m })).StatusCode); // operador não cria promoção

        var r = await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, p, qty: 3, pay: 2000)), HttpStatusCode.Created);
        Assert.Equal(2000, r.GetProperty("totalCents").GetInt64());
        var c = await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, p, qty: 3, pay: 1800) with { CouponCode = "VOLTE10" }), HttpStatusCode.Created);
        Assert.Equal(1800, c.GetProperty("totalCents").GetInt64());
        Assert.Equal("invalid_coupon", await Api.Code(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, p, pay: 1000) with { CouponCode = "NAOEXISTE" })));
        var active = await Api.Expect(await api.Get("/api/v1/promotions/active", op), HttpStatusCode.OK);
        Assert.Equal(2, active.GetArrayLength());
    }

    [Fact]
    public async Task Fiado_respeita_limite_gera_conta_e_recebe_no_caixa()
    {
        var (t, cash) = await Store();
        await Enable(t, "crm", "financeiro", "contas");
        var p = await api.CreateProduct(t.OwnerToken, "FI", price: 3000);
        await api.StockEntry(t.OwnerToken, t.StoreId, p, 20);
        var cust = (await Api.Expect(await api.Post("/api/v1/customers", t.OwnerToken, new { name = "Maria Fiado", document = "52998224725", marketingConsent = false }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        SalePaymentRequest[] fiado = [new(5, 3000)];
        Assert.Equal("credit_not_allowed", await Api.Code(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, p) with { Payments = [.. fiado], CustomerId = cust })));
        Assert.Equal("customer_required", await Api.Code(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, p) with { Payments = [.. fiado] })));
        var (_, op, _) = await api.CreateUser(t, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Put($"/api/v1/customers/{cust}/credit-limit", op, new { limitCents = 999999 })).StatusCode);
        // O caixa confere o limite antes de finalizar (o operador consegue, sem ver o extrato do cliente).
        var semLimite = await Api.Expect(await api.Get($"/api/v1/pos/customers/{cust}/credit-check?amountCents=3000", op), HttpStatusCode.OK);
        Assert.False(semLimite.GetProperty("allowed").GetBoolean());
        Assert.False(semLimite.GetProperty("hasLimit").GetBoolean());
        await Api.Expect(await api.Put($"/api/v1/customers/{cust}/credit-limit", t.OwnerToken, new { limitCents = 5000 }), HttpStatusCode.OK);

        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, p) with { Payments = [.. fiado], CustomerId = cust }), HttpStatusCode.Created);
        Assert.Equal("credit_limit", await Api.Code(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, p) with { Payments = [.. fiado], CustomerId = cust })));
        var st = await Api.Expect(await api.Get($"/api/v1/customers/{cust}/credit", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(3000, st.GetProperty("openCents").GetInt64());
        Assert.Equal(2000, st.GetProperty("availableCents").GetInt64());
        Assert.True((await Api.Expect(await api.Get($"/api/v1/pos/customers/{cust}/credit-check?amountCents=2000", op), HttpStatusCode.OK)).GetProperty("allowed").GetBoolean());
        var passa = await Api.Expect(await api.Get($"/api/v1/pos/customers/{cust}/credit-check?amountCents=2500", op), HttpStatusCode.OK);
        Assert.False(passa.GetProperty("allowed").GetBoolean());
        Assert.Equal(2000, passa.GetProperty("availableCents").GetInt64());

        var before = (await Api.Expect(await api.Get("/api/v1/cash/sessions/current", t.OwnerToken), HttpStatusCode.OK)).GetProperty("expectedCashCents").GetInt64();
        var after = await Api.Expect(await api.Post($"/api/v1/customers/{cust}/credit/receive", t.OwnerToken, new { amountCents = 1000, method = 1 }), HttpStatusCode.OK);
        Assert.Equal(2000, after.GetProperty("openCents").GetInt64());     // pagamento parcial
        Assert.Equal(before + 1000, (await Api.Expect(await api.Get("/api/v1/cash/sessions/current", t.OwnerToken), HttpStatusCode.OK)).GetProperty("expectedCashCents").GetInt64());
        Assert.Equal("overpayment", await Api.Code(await api.Post($"/api/v1/customers/{cust}/credit/receive", t.OwnerToken, new { amountCents = 99999, method = 4 })));
    }

    [Fact]
    public async Task Orcamento_vira_venda_uma_vez()
    {
        var (t, cash) = await Store();
        var p = await api.CreateProduct(t.OwnerToken, "OR", price: 1500);
        await api.StockEntry(t.OwnerToken, t.StoreId, p, 10);
        var q = await Api.Expect(await api.Post("/api/v1/quotes", t.OwnerToken, new { customerName = "Cliente Balcão", validDays = 7, items = new[] { new { productId = p, quantity = 2m } } }), HttpStatusCode.OK);
        Assert.Equal(3000, q.GetProperty("totalCents").GetInt64());
        Assert.Equal(1, q.GetProperty("number").GetInt32());
        var qid = q.GetProperty("id").GetGuid();
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, p, qty: 2, pay: 3000) with { QuoteId = qid }), HttpStatusCode.Created);
        Assert.Equal("quote_not_open", await Api.Code(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, p, qty: 2, pay: 3000) with { QuoteId = qid })));
        var list = await Api.Expect(await api.Get("/api/v1/quotes", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal((int)QuoteStatus.Converted, list[0].GetProperty("status").GetInt32());
    }
}

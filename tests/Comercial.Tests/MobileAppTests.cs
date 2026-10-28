using System.Net;
using Comercial.Domain;
using Comercial.Shared.Contracts;

namespace Comercial.Tests;

/// <summary>App do celular: acesso liberado pelo dono, cadastro rápido com código de barras único e resumo do dono.</summary>
public class MobileAppTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private readonly Api api = new(f);
    private static readonly Dictionary<string, string> Mobile = new() { ["X-Client-App"] = "mobile" };

    private async Task<HttpResponseMessage> MobileLogin(TenantInfo t, string email, string pwd) =>
        await api.Post("/api/v1/auth/login", null, new LoginRequest(t.Code, email, pwd, null), Mobile);

    [Fact]
    public async Task Funcionario_so_entra_no_app_se_o_dono_liberar_e_perde_o_acesso_na_hora()
    {
        var t = await api.CreateTenant("app-" + Guid.NewGuid().ToString("N")[..6]);
        var (id, pdvToken, email) = await api.CreateUser(t, "stockist");

        Assert.Equal("mobile_not_allowed", await Api.Code(await MobileLogin(t, email, "Senha-Func-123")));
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/products", pdvToken)).StatusCode); // no caixa (sem o cabeçalho do app) continua igual

        var users = await Api.Expect(await api.Get("/api/v1/users", t.OwnerToken), HttpStatusCode.OK);
        Assert.False(users.EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == id).GetProperty("mobileAccess").GetBoolean());
        await Api.Expect(await api.Put($"/api/v1/users/{id}/mobile-access", t.OwnerToken, new { allowed = true }), HttpStatusCode.NoContent);
        users = await Api.Expect(await api.Get("/api/v1/users", t.OwnerToken), HttpStatusCode.OK);
        Assert.True(users.EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == id).GetProperty("mobileAccess").GetBoolean());

        var login = await Api.Expect(await MobileLogin(t, email, "Senha-Func-123"), HttpStatusCode.OK);
        var token = login.GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/products", token, Mobile)).StatusCode);

        await Api.Expect(await api.Put($"/api/v1/users/{id}/mobile-access", t.OwnerToken, new { allowed = false }), HttpStatusCode.NoContent);
        Assert.Equal("mobile_not_allowed", await Api.Code(await api.Get("/api/v1/products", token, Mobile)));     // sessão aberta perde o acesso

        // Funcionário não libera o app para outro funcionário; dono não altera o próprio acesso.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Put($"/api/v1/users/{id}/mobile-access", pdvToken, new { allowed = true })).StatusCode);
        var me = await Api.Expect(await api.Get("/api/v1/me", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await MobileLogin(t, t.OwnerEmail, t.OwnerPassword)).StatusCode);     // dono sempre entra
    }

    [Fact]
    public async Task Cadastro_rapido_gera_codigo_de_barras_unico_e_sku_automatico()
    {
        var t = await api.CreateTenant("cb-" + Guid.NewGuid().ToString("N")[..6]);
        var g1 = (await Api.Expect(await api.Post("/api/v1/products/barcode/generate", t.OwnerToken), HttpStatusCode.OK)).GetProperty("barcode").GetString()!;
        Assert.True(ProductCodes.IsInternal(g1));

        // Sem SKU, sem preço (o funcionário só fotografa, dá o nome e o código): o sistema cria o código interno.
        var p = await Api.Expect(await api.Post("/api/v1/products", t.OwnerToken, new { sku = "", barcode = g1, name = "Garrafa térmica", unit = "UN", priceCents = 0, costCents = 0, minStock = 0 }), HttpStatusCode.Created);
        Assert.StartsWith("P", p.GetProperty("sku").GetString());
        Assert.Equal(g1, p.GetProperty("barcode").GetString());

        // Mesmo código em outro produto é recusado (inclusive código do fabricante repetido).
        Assert.Equal("barcode_taken", await Api.Code(await api.Post("/api/v1/products", t.OwnerToken, new { sku = "", barcode = g1, name = "Outro", unit = "UN", priceCents = 100, costCents = 0, minStock = 0 })));
        var g2 = (await Api.Expect(await api.Post("/api/v1/products/barcode/generate", t.OwnerToken), HttpStatusCode.OK)).GetProperty("barcode").GetString()!;
        Assert.NotEqual(g1, g2);

        // Quem não cadastra produtos não gera código.
        var (_, opToken, _) = await api.CreateUser(t, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post("/api/v1/products/barcode/generate", opToken)).StatusCode);
    }

    [Fact]
    public async Task Preco_pela_porcentagem_de_lucro_e_mesmo_cadastro_no_app_e_no_caixa()
    {
        var t = await api.CreateTenant("mk-" + Guid.NewGuid().ToString("N")[..6]);
        // App do celular: pagou R$ 10,00 + 100% → vende por R$ 20,00 (o servidor calcula, mesmo se o preço enviado vier errado).
        var tok = (await Api.Expect(await api.Post("/api/v1/auth/login", null, new LoginRequest(t.Code, t.OwnerEmail, t.OwnerPassword, null), Mobile), HttpStatusCode.OK))
            .GetProperty("accessToken").GetString()!;
        var p = await Api.Expect(await api.Post("/api/v1/products", tok, new { sku = "", barcode = "7890000000017", name = "Caderno", unit = "UN", priceCents = 1, costCents = 1000, markupPercent = 100, minStock = 0 }, Mobile), HttpStatusCode.Created);
        Assert.Equal(2000, p.GetProperty("priceCents").GetInt64());
        Assert.Equal(100m, p.GetProperty("markupPercent").GetDecimal());
        var id = p.GetProperty("id").GetGuid();

        // Caixa (computador, sem o cabeçalho do app): o produto aparece no catálogo que o caixa baixa para vender.
        var catalog = await Api.Expect(await api.Get("/api/v1/catalog/products", t.OwnerToken), HttpStatusCode.OK);
        Assert.Contains(catalog.EnumerateArray(), x => x.GetProperty("id").GetGuid() == id && x.GetProperty("priceCents").GetInt64() == 2000);

        // Caixa altera a %: 35,5% de R$ 10,00 = R$ 13,55 — e o app enxerga o mesmo valor.
        var full = await Api.Expect(await api.Get($"/api/v1/products/{id}", t.OwnerToken), HttpStatusCode.OK);
        await Api.Expect(await api.Put($"/api/v1/products/{id}", t.OwnerToken, new { sku = full.GetProperty("sku").GetString(), barcode = "7890000000017", name = "Caderno", unit = "UN",
            priceCents = 0, costCents = 1000, markupPercent = 35.5m, minStock = 0, expectedVersion = full.GetProperty("version").GetInt32() }), HttpStatusCode.OK);
        var bal = await Api.Expect(await api.Get($"/api/v1/stock/balances?storeId={t.StoreId}", tok, Mobile), HttpStatusCode.OK);
        var row = bal.EnumerateArray().Single(x => x.GetProperty("productId").GetGuid() == id);
        Assert.Equal(1355, row.GetProperty("priceCents").GetInt64());
        Assert.Equal(35.5m, row.GetProperty("markupPercent").GetDecimal());

        // Sem % o preço digitado vale (e a % fica vazia); % fora do limite é recusada.
        await Api.Expect(await api.Put($"/api/v1/products/{id}", t.OwnerToken, new { sku = full.GetProperty("sku").GetString(), barcode = "7890000000017", name = "Caderno", unit = "UN",
            priceCents = 1990, costCents = 1000, minStock = 0 }), HttpStatusCode.OK);
        Assert.Equal(1990, (await Api.Expect(await api.Get($"/api/v1/products/{id}", t.OwnerToken), HttpStatusCode.OK)).GetProperty("priceCents").GetInt64());
        Assert.Equal("invalid_markup", await Api.Code(await api.Post("/api/v1/products", t.OwnerToken, new { sku = "X1", name = "X", unit = "UN", priceCents = 0, costCents = 100, markupPercent = -5, minStock = 0 })));
    }

    [Theory]
    [InlineData(1000, 100, 2000)]
    [InlineData(1000, 35.5, 1355)]
    [InlineData(799, 50, 1199)]   // 11,985 arredonda para 11,99
    [InlineData(1000, 0, 1000)]
    public void Regra_do_preco(long cost, decimal markup, long price) => Assert.Equal(price, Comercial.Shared.Pricing.PriceFromMarkup(cost, markup));

    [Fact]
    public void Codigo_interno_e_ean13_valido()
    {
        for (var i = 0; i < 200; i++)
        {
            var c = ProductCodes.NewInternalEan13();
            Assert.True(ProductCodes.IsValidEan13(c));
            Assert.StartsWith("200", c);
        }
        Assert.True(ProductCodes.IsValidEan13("7891000100011"));
        Assert.False(ProductCodes.IsValidEan13("7891000100012"));
    }

    [Fact]
    public async Task Resumo_do_dono_mostra_vendas_por_dia_mais_vendidos_e_estoque_baixo()
    {
        var t = await api.CreateTenant("ov-" + Guid.NewGuid().ToString("N")[..6]);
        var prod = await api.CreateProduct(t.OwnerToken, "OV1", price: 1500, minStock: 10);
        await api.StockEntry(t.OwnerToken, t.StoreId, prod, 5);
        var cash = await api.OpenCash(t.OwnerToken, t.StoreId);
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, prod, qty: 2, pay: 5000)), HttpStatusCode.Created);

        var o = await Api.Expect(await api.Get("/api/v1/mobile/overview?days=7", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(7, o.GetProperty("byDay").GetArrayLength());
        Assert.Equal(3000, o.GetProperty("revenueCents").GetInt64());
        Assert.Equal(3000, o.GetProperty("byDay")[6].GetProperty("revenueCents").GetInt64());                // hoje
        Assert.Equal("Produto OV1", o.GetProperty("topProducts")[0].GetProperty("name").GetString());
        Assert.Equal(3000, o.GetProperty("paymentMix")[0].GetProperty("amountCents").GetInt64());            // pagou 50, troco 20
        Assert.Equal(1, o.GetProperty("recentSales").GetArrayLength());
        Assert.Contains(o.GetProperty("lowStock").EnumerateArray(), x => x.GetProperty("name").GetString() == "Produto OV1"); // 3 <= mínimo 10

        var (_, opToken, _) = await api.CreateUser(t, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Get("/api/v1/mobile/overview", opToken)).StatusCode);
    }
}

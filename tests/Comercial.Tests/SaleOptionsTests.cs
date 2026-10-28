using System.Net;
using Comercial.Desktop.Core;
using Comercial.Domain;
using Comercial.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Tests;

/// <summary>Código=preço (produto de preço livre) e modo de emissão da NFC-e escolhido pela loja.</summary>
public class SaleOptionsTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private readonly Api api = new(f);

    [Theory]
    [InlineData("100=9,90", "100", 990)]
    [InlineData("100 = 9.90", "100", 990)]
    [InlineData("DIV=15", "DIV", 1500)]
    [InlineData("7891234567895=0,5", "7891234567895", 50)]
    public void Codigo_igual_preco_e_entendido(string input, string code, long cents)
    {
        Assert.True(PriceInput.TrySplit(input, out var c, out var p, out var error));
        Assert.Null(error);
        Assert.Equal(code, c);
        Assert.Equal(cents, p);
    }

    [Theory]
    [InlineData("100=")]
    [InlineData("100=abc")]
    [InlineData("100=0")]
    [InlineData("=9,90")]
    public void Preco_invalido_tem_mensagem(string input)
    {
        Assert.True(PriceInput.TrySplit(input, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Quantidade_com_preco_livre()
    {
        Assert.True(QuantityInput.TryParse("3*100=9,90", out var qty, out var term, out _));
        Assert.Equal(3, qty);
        Assert.True(PriceInput.TrySplit(term, out var code, out var price, out _));
        Assert.Equal(("100", 990L), (code, price));
        Assert.False(PriceInput.TrySplit("caderno", out _, out _, out _)); // sem "=": busca normal
    }

    private async Task<(TenantInfo T, Guid Cash)> Shop(string? emission = null)
    {
        var t = await api.CreateTenant("op-" + Guid.NewGuid().ToString("N")[..6]);
        if (emission is not null)
            await Api.Expect(await api.Put("/api/v1/fiscal/profile", t.OwnerToken, new
            {
                cnpj = "11222333000181", regime = 1, uf = "SP", environment = 2, provider = "focus", providerToken = "t", nfceSeries = 1, autoEmit = false, emissionMode = emission,
            }), HttpStatusCode.OK);
        return (t, await api.OpenCash(t.OwnerToken, t.StoreId, 0));
    }

    private static CreateSaleRequest Sale(TenantInfo t, Guid cash, Guid product, decimal qty, long? price, long pay, bool? nfce = null) =>
        new(Guid.NewGuid().ToString("N"), t.StoreId, cash, DateTimeOffset.UtcNow, [new SaleItemRequest(product, qty, 0, price)], [new SalePaymentRequest(1, pay)], IssueNfce: nfce);

    [Fact]
    public async Task Produto_de_preco_livre_vende_pelo_valor_digitado_e_os_demais_nao()
    {
        var (t, cash) = await Shop();
        var div = (await Api.Expect(await api.Post("/api/v1/products", t.OwnerToken, new
        {
            sku = "100", name = "Diversos", unit = "UN", priceCents = 0, costCents = 0, minStock = 0m, openPrice = true,
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await api.StockEntry(t.OwnerToken, t.StoreId, div, 50);
        var catalog = await Api.Expect(await api.Get("/api/v1/catalog/products", t.OwnerToken), HttpStatusCode.OK);
        Assert.True(catalog.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == div).GetProperty("openPrice").GetBoolean());

        var sale = await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Sale(t, cash, div, 2, 990, 1980)), HttpStatusCode.Created);
        Assert.Equal(1980, sale.GetProperty("totalCents").GetInt64());
        var item = await f.WithDb(db => db.SaleItems.IgnoreQueryFilters().FirstAsync(i => i.SaleId == sale.GetProperty("id").GetGuid()));
        Assert.Equal(990, item.UnitPriceCents);
        Assert.Equal("open_price_required", await Api.Code(await api.Post("/api/v1/sales", t.OwnerToken, Sale(t, cash, div, 1, null, 1000))));

        // Produto comum continua com o preço do cadastro: preço diferente vindo do caixa é recusado.
        var normal = await api.CreateProduct(t.OwnerToken, "N1", 1000);
        await api.StockEntry(t.OwnerToken, t.StoreId, normal, 5);
        Assert.Equal("price_mismatch", await Api.Code(await api.Post("/api/v1/sales", t.OwnerToken, Sale(t, cash, normal, 1, 1, 1000))));
    }

    private async Task<bool> HasDoc(Guid saleId) => await f.WithDb(db => db.FiscalDocuments.IgnoreQueryFilters().AnyAsync(d => d.SaleId == saleId));

    private async Task<Guid> Sell(TenantInfo t, Guid cash, Guid product, bool? nfce = null) =>
        (await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Sale(t, cash, product, 1, 1000, 1000, nfce)), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    [Fact]
    public async Task Loja_escolhe_sempre_a_pedido_ou_nunca_emitir_nfce()
    {
        foreach (var (mode, withoutAsk, withAsk) in new[] { ("sempre", true, true), ("a_pedido", false, true), ("nunca", false, false) })
        {
            var (t, cash) = await Shop(mode);
            var p = await api.CreateProduct(t.OwnerToken, "M1", 1000);
            await api.StockEntry(t.OwnerToken, t.StoreId, p, 10);
            Assert.Equal(withoutAsk, await HasDoc(await Sell(t, cash, p)));
            Assert.Equal(withAsk, await HasDoc(await Sell(t, cash, p, nfce: true)));
            // O caixa (operador) descobre o modo para mostrar a opção "Emitir NFC-e".
            var (_, op, _) = await api.CreateUser(t, "operator");
            Assert.Equal(mode, (await Api.Expect(await api.Get("/api/v1/fiscal/mode", op), HttpStatusCode.OK)).GetProperty("mode").GetString());
            // Venda sem nota não é divergência quando a loja não é obrigada a emitir sempre.
            var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3));
            var check = (await Api.Expect(await api.Get($"/api/v1/fiscal/closing/{now.Year}/{now.Month}", t.OwnerToken), HttpStatusCode.OK))
                .GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "documentos").GetProperty("state").GetString();
            Assert.Equal(mode == "sempre" ? "concluido" : "nao_aplicavel", check);
        }
        Assert.Equal("invalid_mode", await Api.Code(await api.Put("/api/v1/fiscal/profile", (await api.CreateTenant("om-" + Guid.NewGuid().ToString("N")[..6])).OwnerToken,
            new { cnpj = "11222333000181", regime = 1, uf = "SP", environment = 2, provider = "focus", nfceSeries = 1, autoEmit = true, emissionMode = "as_vezes" })));
    }
}

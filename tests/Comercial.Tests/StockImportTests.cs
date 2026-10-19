using System.Net;
using Comercial.Domain;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Tests;

/// <summary>Importação de NF-e (XML) e planilha, lotes/validade e transferência entre lojas.</summary>
public class StockImportTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private readonly Api api = new(f);

    private static string Nfe(string key, string ean, string lote = "L123", string val = "2026-11-01") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <nfeProc xmlns="http://www.portalfiscal.inf.br/nfe" versao="4.00"><NFe><infNFe Id="NFe{key}" versao="4.00">
          <ide><nNF>4521</nNF></ide>
          <emit><CNPJ>11222333000181</CNPJ><xNome>Distribuidora Exemplo LTDA</xNome></emit>
          <det nItem="1"><prod><cProd>ABC-1</cProd><cEAN>{ean}</cEAN><xProd>Iogurte Natural 170g</xProd><NCM>04031000</NCM><CFOP>5102</CFOP>
            <uCom>UN</uCom><qCom>24.0000</qCom><vUnCom>2.5000</vUnCom><vProd>60.00</vProd>
            <rastro><nLote>{lote}</nLote><qLote>24</qLote><dFab>2026-10-01</dFab><dVal>{val}</dVal></rastro></prod></det>
          <det nItem="2"><prod><cProd>XYZ-9</cProd><cEAN>SEM GTIN</cEAN><xProd>Pão de forma</xProd><NCM>19052090</NCM><CFOP>5102</CFOP>
            <uCom>UN</uCom><qCom>10.0000</qCom><vUnCom>5.0000</vUnCom><vProd>50.00</vProd></prod></det>
          <total><ICMSTot><vNF>110.00</vNF></ICMSTot></total>
        </infNFe></NFe></nfeProc>
        """;

    private static string Key() => "3526101122233300018155001" + Random.Shared.NextInt64(0, long.MaxValue).ToString("D19")[..19];

    [Fact]
    public async Task Importa_nfe_cria_fornecedor_produtos_estoque_e_lote_uma_vez()
    {
        var t = await api.CreateTenant("nfe-" + Guid.NewGuid().ToString("N")[..6]);
        var existing = await api.CreateProduct(t.OwnerToken, "IOG", price: 450, cost: 200); // barcode "789IOG"? usa EAN próprio abaixo
        await f.WithDb(async db => { var p = await db.Products.IgnoreQueryFilters().SingleAsync(x => x.Id == existing); p.Barcode = "7891000000017"; await db.SaveChangesAsync(); });
        var key = Key();
        var xml = Nfe(key, "7891000000017");

        var pv = await Api.Expect(await api.Post("/api/v1/purchases/nfe/preview", t.OwnerToken, new { xml }), HttpStatusCode.OK);
        Assert.Equal(2, pv.GetProperty("items").GetArrayLength());
        Assert.Equal(existing, pv.GetProperty("items")[0].GetProperty("matchedProductId").GetGuid());   // achou pelo EAN
        Assert.Equal(JsonNull, pv.GetProperty("items")[1].GetProperty("matchedProductId").ValueKind);   // "SEM GTIN"

        var r = await Api.Expect(await api.Post("/api/v1/purchases/nfe/import", t.OwnerToken, new { xml, storeId = t.StoreId, markupPercent = 60m, createMissing = true }), HttpStatusCode.OK);
        Assert.Equal(1, r.GetProperty("created").GetInt32());
        Assert.Equal(24m, await api.Balance(t.OwnerToken, t.StoreId, existing));
        var pao = await f.WithDb(db => db.Products.IgnoreQueryFilters().SingleAsync(p => p.TenantId == t.Id && p.Name == "Pão de forma"));
        Assert.Equal(500, pao.CostCents);
        Assert.Equal(800, pao.PriceCents);                                       // custo + 60%
        Assert.Equal("19052090", pao.Ncm);
        Assert.True(await f.WithDb(db => db.Suppliers.IgnoreQueryFilters().AnyAsync(s => s.TenantId == t.Id && s.Document == "11222333000181")));
        Assert.Equal(250, (await f.WithDb(db => db.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == existing))).CostCents); // custo atualizado pela nota
        Assert.Equal("already_imported", await Api.Code(await api.Post("/api/v1/purchases/nfe/import", t.OwnerToken, new { xml, storeId = t.StoreId, markupPercent = 60m, createMissing = true })));

        // Lote com validade: aparece em "vencendo" e a venda consome o lote.
        var exp = await Api.Expect(await api.Get("/api/v1/stock/expiring?days=60", t.OwnerToken), HttpStatusCode.OK);
        Assert.Contains(exp.EnumerateArray(), b => b.GetProperty("batchCode").GetString() == "L123");
        var cash = await api.OpenCash(t.OwnerToken, t.StoreId, 0);
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, existing, qty: 4, pay: 1800)), HttpStatusCode.Created);
        var batch = await f.WithDb(db => db.ProductBatches.IgnoreQueryFilters().SingleAsync(b => b.TenantId == t.Id && b.BatchCode == "L123"));
        Assert.Equal(20m, batch.QuantityRemaining);
    }

    private const System.Text.Json.JsonValueKind JsonNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task Xml_com_entidade_externa_e_recusado()
    {
        var t = await api.CreateTenant("xxe-" + Guid.NewGuid().ToString("N")[..6]);
        var xxe = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///c:/windows/win.ini">]><nfeProc xmlns="http://www.portalfiscal.inf.br/nfe">&e;</nfeProc>""";
        Assert.Equal("invalid_xml", await Api.Code(await api.Post("/api/v1/purchases/nfe/preview", t.OwnerToken, new { xml = xxe })));
        Assert.Equal("invalid_xml", await Api.Code(await api.Post("/api/v1/purchases/nfe/preview", t.OwnerToken, new { xml = "<html>não é nota</html>" })));
        var (_, op, _) = await api.CreateUser(t, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post("/api/v1/purchases/nfe/preview", op, new { xml = Nfe(Key(), "7891000000017") })).StatusCode);
    }

    [Fact]
    public async Task Planilha_cria_atualiza_e_aponta_erros_por_linha()
    {
        var t = await api.CreateTenant("csv-" + Guid.NewGuid().ToString("N")[..6]);
        await api.CreateProduct(t.OwnerToken, "EXIST", price: 100);
        var csv = "\uFEFFsku;nome;preco;custo;codigo_barras;unidade;estoque_minimo;ncm;quantidade\n" +
                  "EXIST;Produto atualizado;12,50;7,00;;UN;2;;\n" +
                  "NOVO1;Arroz 5kg;29,90;22,00;7891000555001;UN;5;10063021;40\n" +
                  "NOVO2;Feijão;abc;;;UN;;;\n" +
                  "NOVO3;Queijo;49,90;30;;XX;;;\n";
        var r = await Api.Expect(await api.Post("/api/v1/products/import-csv", t.OwnerToken, new { csv, storeId = t.StoreId }), HttpStatusCode.OK);
        Assert.Equal(1, r.GetProperty("created").GetInt32());
        Assert.Equal(1, r.GetProperty("updated").GetInt32());
        Assert.Equal(2, r.GetProperty("errors").GetArrayLength());
        var novo = await f.WithDb(db => db.Products.IgnoreQueryFilters().SingleAsync(p => p.TenantId == t.Id && p.Sku == "NOVO1"));
        Assert.Equal(2990, novo.PriceCents);
        Assert.Equal(40m, await api.Balance(t.OwnerToken, t.StoreId, novo.Id));
        Assert.Equal(1250, (await f.WithDb(db => db.Products.IgnoreQueryFilters().SingleAsync(p => p.TenantId == t.Id && p.Sku == "EXIST"))).PriceCents);
    }

    [Fact]
    public async Task Transferencia_entre_lojas_move_estoque_nas_duas()
    {
        var t = await api.CreateTenant("trf-" + Guid.NewGuid().ToString("N")[..6]);
        var filial = await f.WithDb(async db =>
        {
            var s = new Store { TenantId = t.Id, Code = "filial", Name = "Filial" };
            db.Stores.Add(s);
            await db.SaveChangesAsync();
            return s.Id;
        });
        var p = await api.CreateProduct(t.OwnerToken, "TR1");
        await api.StockEntry(t.OwnerToken, t.StoreId, p, 30);
        Assert.Equal("same_store", await Api.Code(await api.Post("/api/v1/stock/transfers", t.OwnerToken, new { fromStoreId = t.StoreId, toStoreId = t.StoreId, items = new[] { new { productId = p, quantity = 1m } } })));
        var tr = await Api.Expect(await api.Post("/api/v1/stock/transfers", t.OwnerToken, new { fromStoreId = t.StoreId, toStoreId = filial, items = new[] { new { productId = p, quantity = 12m } }, notes = "reposição" }), HttpStatusCode.OK);
        Assert.Equal(1, tr.GetProperty("number").GetInt32());
        Assert.Equal(18m, await api.Balance(t.OwnerToken, t.StoreId, p));
        Assert.Equal(12m, await api.Balance(t.OwnerToken, filial, p));
        var cons = await Api.Expect(await api.Get("/api/v1/stock/consolidated", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(30m, cons.GetProperty("products").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == p).GetProperty("total").GetDecimal());
    }
}

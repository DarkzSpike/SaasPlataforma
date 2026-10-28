using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Comercial.Api.Services;
using Comercial.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Comercial.Tests;

/// <summary>Provedor fiscal simulado: autoriza, rejeita ou pede nova tentativa conforme configurado.</summary>
public sealed class FakeFiscalProvider : IFiscalProvider
{
    public string Name => "focus";
    public ConcurrentQueue<JsonObject> Payloads { get; } = new();
    public ConcurrentBag<string> Tokens { get; } = [];
    public string Mode { get; set; } = "authorize";
    /// <summary>Como o provedor responde ao download do XML: ok, missing (404), wrong_key (XML de outra nota), error (fora do ar).</summary>
    public string XmlMode { get; set; } = "ok";
    /// <summary>XMLs FICTÍCIOS, só para teste (sem valor fiscal), no formato dos originais com protocolo.</summary>
    public ConcurrentDictionary<string, byte[]> Xmls { get; } = new();
    private int _number;

    private static string Money(long c) => (c / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    public static byte[] NfeProc(string key, long vnf, int cStat = 100) => System.Text.Encoding.UTF8.GetBytes(
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><nfeProc xmlns=\"http://www.portalfiscal.inf.br/nfe\" versao=\"4.00\"><NFe><infNFe Id=\"NFe{key}\"><total><ICMSTot>" +
        $"<vICMS>0.00</vICMS><vPIS>0.00</vPIS><vCOFINS>0.00</vCOFINS><vNF>{Money(vnf)}</vNF><vTotTrib>{Money(vnf / 10)}</vTotTrib></ICMSTot></total></infNFe></NFe>" +
        $"<protNFe versao=\"4.00\"><infProt><tpAmb>2</tpAmb><chNFe>{key}</chNFe><nProt>135260000000001</nProt><cStat>{cStat}</cStat><xMotivo>TESTE</xMotivo></infProt></protNFe></nfeProc>");
    public static byte[] CancelProc(string key) => System.Text.Encoding.UTF8.GetBytes(
        $"<procEventoNFe xmlns=\"http://www.portalfiscal.inf.br/nfe\" versao=\"1.00\"><evento><infEvento><chNFe>{key}</chNFe><tpEvento>110111</tpEvento></infEvento></evento>" +
        $"<retEvento><infEvento><chNFe>{key}</chNFe><cStat>135</cStat><nProt>135260000000002</nProt></infEvento></retEvento></procEventoNFe>");
    public static byte[] VoidProc() => System.Text.Encoding.UTF8.GetBytes(
        "<procInutNFe xmlns=\"http://www.portalfiscal.inf.br/nfe\" versao=\"4.00\"><inutNFe/><retInutNFe><infInut><cStat>102</cStat><nProt>135260000000003</nProt></infInut></retInutNFe></procInutNFe>");

    public Task<FiscalProviderResult> TransmitAsync(FiscalContext c, FiscalDocument d, JsonObject payload, CancellationToken ct)
    {
        Payloads.Enqueue(payload); Tokens.Add(c.Token);
        var n = Interlocked.Increment(ref _number);
        var key = "3526" + n.ToString().PadLeft(40, '0');
        var vnf = payload["items"]!.AsArray().Sum(i => (long)Math.Round(decimal.Parse(i!["valor_bruto"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture) * 100)
            - (long)Math.Round(decimal.Parse(i!["valor_desconto"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture) * 100));
        return Task.FromResult(Mode switch
        {
            "retry" => new FiscalProviderResult(FiscalStatus.PendingTransmission, "SEFAZ indisponível", Retry: true),
            "reject" => new FiscalProviderResult(FiscalStatus.Rejected, "Rejeição 778: NCM inexistente"),
            "deny" => Store(new FiscalProviderResult(FiscalStatus.Denied, "Uso denegado: irregularidade fiscal do emitente", "135260000000009", key, n, 1,
                XmlUrl: $"https://exemplo/nota-{key}.xml"), key, NfeProc(key, vnf, 301)),
            _ => Store(new FiscalProviderResult(FiscalStatus.Authorized, "Autorizado o uso da NF-e", "135260000000001", key,
                n, 1, "https://exemplo/danfe.pdf", $"https://exemplo/nota-{key}.xml", "https://exemplo/qr"), key, NfeProc(key, vnf)),
        });
    }
    private FiscalProviderResult Store(FiscalProviderResult r, string key, byte[] xml) { Xmls[r.XmlUrl!] = xml; return r; }

    public Task<FiscalProviderResult> CancelAsync(FiscalContext c, FiscalDocument d, string reason, CancellationToken ct)
    {
        var url = $"https://exemplo/canc-{d.AccessKey}.xml";
        Xmls[url] = CancelProc(d.AccessKey!);
        return Task.FromResult(new FiscalProviderResult(FiscalStatus.Cancelled, "Cancelamento homologado", "135260000000002", XmlUrl: url));
    }
    public Task<FiscalProviderResult> VoidNumbersAsync(FiscalContext c, int s, int a, int b, string r, CancellationToken ct)
    {
        var url = $"https://exemplo/inut-{s}-{a}-{b}-{Guid.NewGuid():N}.xml";
        Xmls[url] = VoidProc();
        return Task.FromResult(new FiscalProviderResult(FiscalStatus.Authorized, "Inutilização homologada", "135260000000003", XmlUrl: url));
    }
    public Task<byte[]?> DownloadXmlAsync(FiscalContext c, string url, CancellationToken ct) => XmlMode switch
    {
        "missing" => Task.FromResult<byte[]?>(null),
        "error" => throw new HttpRequestException("provedor fora do ar"),
        "wrong_key" => Task.FromResult<byte[]?>(NfeProc("3526" + new string('9', 40), 100)),
        _ => Task.FromResult(Xmls.TryGetValue(url, out var b) ? b : null),
    };
}

public sealed class FakeNfseProvider : INfseProvider
{
    public ConcurrentBag<JsonObject> Issued { get; } = [];
    public Task<NfseResult> IssueAsync(NfseConfig c, NfseDocument d, JsonObject p, CancellationToken ct) { Issued.Add(p); return Task.FromResult(new NfseResult("processing", "Prefeitura processando.")); }
    public Task<NfseResult> QueryAsync(NfseConfig c, NfseDocument d, CancellationToken ct) => Task.FromResult(new NfseResult("issued", "NFS-e autorizada.", "123", "ABC-9", "https://exemplo/nfse.pdf"));
}

public class FiscalFactory : BillingFactory
{
    public FakeFiscalProvider Fiscal { get; } = new();
    public FakeNfseProvider Nfse { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        base.ConfigureWebHost(b);
        b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IFiscalProvider>();
            s.AddSingleton<IFiscalProvider>(Fiscal);
            s.RemoveAll<INfseProvider>();
            s.AddSingleton<INfseProvider>(Nfse);
        });
    }
}

public class FiscalTests(FiscalFactory f) : IClassFixture<FiscalFactory>
{
    private readonly Api api = new(f);
    private const string Cnpj = "11222333000181";

    private async Task<(TenantInfo T, Guid Cash)> StoreWithFiscal()
    {
        var t = await api.CreateTenant("nf-" + Guid.NewGuid().ToString("N")[..6]);
        await Api.Expect(await api.Put("/api/v1/fiscal/profile", t.OwnerToken, new
        {
            cnpj = "11.222.333/0001-81", stateRegistration = "123456789", regime = 1, uf = "sp", environment = 2, provider = "focus", providerToken = "token-focus-da-loja",
            nfceSeries = 1, autoEmit = true,
        }), HttpStatusCode.OK);
        return (t, await api.OpenCash(t.OwnerToken, t.StoreId, 0));
    }

    private async Task<Guid> ProductWithNcm(TenantInfo t, string sku, long price, string? ncm = "61091000")
    {
        var p = await Api.Expect(await api.Post("/api/v1/products", t.OwnerToken, new
        {
            sku, name = "Camiseta " + sku, unit = "UN", priceCents = price, costCents = price / 2, minStock = 0m, ncm, cest = (string?)null, cfop = "5102",
            fiscalOrigin = 0, ibsCbsCst = "000", ibsCbsClass = "000001",
        }), HttpStatusCode.Created);
        var id = p.GetProperty("id").GetGuid();
        await api.StockEntry(t.OwnerToken, t.StoreId, id, 50);
        return id;
    }

    private Task<int> RunJob() => f.WithScopeResult(sp => FiscalJob.RunOnceAsync(sp, CancellationToken.None));

    [Fact]
    public async Task Dados_fiscais_do_produto_e_do_emitente_sao_validados_e_token_nao_volta()
    {
        var t = await api.CreateTenant("nfv-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal("invalid_fiscal", await Api.Code(await api.Post("/api/v1/products", t.OwnerToken, new { sku = "X1", name = "X", unit = "UN", priceCents = 100, costCents = 50, minStock = 0m, ncm = "123" })));
        Assert.Equal("invalid_fiscal", await Api.Code(await api.Post("/api/v1/products", t.OwnerToken, new { sku = "X2", name = "X", unit = "UN", priceCents = 100, costCents = 50, minStock = 0m, ncm = "61091000", ibsCbsClass = "12" })));
        Assert.Equal("invalid_cnpj", await Api.Code(await api.Put("/api/v1/fiscal/profile", t.OwnerToken, new { cnpj = "11222333000100", regime = 1, uf = "SP", environment = 2, provider = "focus", nfceSeries = 1, autoEmit = true })));
        var view = await Api.Expect(await api.Put("/api/v1/fiscal/profile", t.OwnerToken, new { cnpj = Cnpj, regime = 1, uf = "SP", environment = 2, provider = "focus", providerToken = "segredo-focus", nfceSeries = 1, autoEmit = true }), HttpStatusCode.OK);
        Assert.DoesNotContain("segredo-focus", view.ToString());
        Assert.Equal(0.9m, view.GetProperty("cbsRate").GetDecimal()); // alíquota de teste de 2026
        var (_, op, _) = await api.CreateUser(t, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Put("/api/v1/fiscal/profile", op, new { cnpj = Cnpj, regime = 1, uf = "SP", environment = 2, provider = "focus", nfceSeries = 1, autoEmit = true })).StatusCode);
    }

    [Fact]
    public async Task Venda_vira_nfce_autorizada_com_ibs_cbs_e_desconto_rateado()
    {
        var (t, cash) = await StoreWithFiscal();
        var a = await ProductWithNcm(t, "CA", 3000);
        var b = await ProductWithNcm(t, "CB", 1000);
        var sale = Api.Sale(t.StoreId, cash, a, pay: 3600, discount: 400) with
        {
            Items = [new(a, 1, 0, 3000), new(b, 1, 0, 1000)],
            Payments = [new(4, 2000), new(1, 1600)],
        };
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, sale), HttpStatusCode.Created);
        Assert.True(await RunJob() >= 1);

        var docs = await Api.Expect(await api.Get("/api/v1/fiscal/documents", t.OwnerToken), HttpStatusCode.OK);
        var doc = docs.EnumerateArray().Single();
        Assert.True(doc.GetProperty("status").GetInt32() == 2, doc.GetProperty("lastMessage").GetString()); // autorizada
        Assert.False(string.IsNullOrEmpty(doc.GetProperty("accessKey").GetString()));
        Assert.Contains("token-focus-da-loja", f.Fiscal.Tokens);

        var payload = f.Fiscal.Payloads.Last(p => p["cnpj_emitente"]!.ToString() == Cnpj);
        var items = payload["items"]!.AsArray();
        Assert.Equal("61091000", items[0]!["codigo_ncm"]!.ToString());
        Assert.Equal("000001", items[0]!["ibs_cbs_classificacao_tributaria"]!.ToString());
        var descontos = items.Sum(i => decimal.Parse(i!["valor_desconto"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(4.00m, descontos); // desconto do total rateado nos itens
        Assert.Contains(payload["formas_pagamento"]!.AsArray(), p => p!["forma_pagamento"]!.ToString() == "17"); // PIX
    }

    [Fact]
    public async Task Rejeicao_retentativa_cancelamento_e_inutilizacao()
    {
        var (t, cash) = await StoreWithFiscal();
        var semNcm = await ProductWithNcm(t, "SN", 1000, ncm: null);
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, semNcm, pay: 1000)), HttpStatusCode.Created);
        var doc = await f.WithDb(db => db.FiscalDocuments.IgnoreQueryFilters().SingleAsync(d => d.TenantId == t.Id));
        var r = await Api.Expect(await api.Post($"/api/v1/fiscal/documents/{doc.Id}/transmit", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(3, r.GetProperty("status").GetInt32());                         // rejeitada localmente: sem NCM
        Assert.Contains("NCM", r.GetProperty("lastMessage").GetString());

        var ok = await ProductWithNcm(t, "OK", 2000);
        f.Fiscal.Mode = "retry";
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, ok, pay: 2000)), HttpStatusCode.Created);
        await RunJob();
        var pend = await f.WithDb(db => db.FiscalDocuments.IgnoreQueryFilters().SingleAsync(d => d.TenantId == t.Id && d.Status == FiscalStatus.PendingTransmission));
        Assert.NotNull(pend.NextAttemptAt);                                          // SEFAZ fora: tenta de novo depois
        f.Fiscal.Mode = "authorize";
        var auth = await Api.Expect(await api.Post($"/api/v1/fiscal/documents/{pend.Id}/transmit", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(2, auth.GetProperty("status").GetInt32());

        Assert.Equal("reason_required", await Api.Code(await api.Post($"/api/v1/fiscal/documents/{pend.Id}/cancel", t.OwnerToken, new { reason = "erro" })));
        var canc = await Api.Expect(await api.Post($"/api/v1/fiscal/documents/{pend.Id}/cancel", t.OwnerToken, new { reason = "Cliente desistiu da compra no caixa" }), HttpStatusCode.OK);
        Assert.Equal(5, canc.GetProperty("status").GetInt32());

        var v = await Api.Expect(await api.Post("/api/v1/fiscal/void-numbers", t.OwnerToken, new { series = 1, firstNumber = 10, lastNumber = 12, reason = "Falha de sistema pulou numeração" }), HttpStatusCode.OK);
        Assert.Equal("autorizada", v.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Nfse_da_mensalidade_fica_desligada_sem_cnpj_e_emite_quando_configurada()
    {
        var admin = await api.AdminToken();
        var view = await Api.Expect(await api.Get("/admin/v1/settings/nfse", admin), HttpStatusCode.OK);
        Assert.False(view.GetProperty("enabled").GetBoolean());
        Assert.Equal("invalid_cnpj", await Api.Code(await api.Put("/admin/v1/settings/nfse", admin, new { enabled = true, cnpj = "12345678901" }, api.Reauth()))); // CPF não serve
        Assert.Equal("nfse_incomplete", await Api.Code(await api.Put("/admin/v1/settings/nfse", admin, new { enabled = true, cnpj = Cnpj }, api.Reauth())));

        await Api.Expect(await api.Put("/admin/v1/settings/nfse", admin, new { enabled = true, cnpj = Cnpj, municipalRegistration = "998877", cityCode = "3550308", serviceItem = "01.07", issRate = 2m, token = "tok-nfse", environment = 2 }, api.Reauth()), HttpStatusCode.OK);
        var t = await api.CreateTenant("nfs-" + Guid.NewGuid().ToString("N")[..6]);
        var inv = await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/invoices", admin, new { dueInDays = 3 }), HttpStatusCode.OK);
        await Api.Expect(await api.Post($"/admin/v1/invoices/{inv.GetProperty("id").GetGuid()}/mark-paid", admin, new { confirmTenantCode = t.Code, reason = "pago por transferência" }, api.Reauth()), HttpStatusCode.OK);
        await Api.Expect(await api.Post("/admin/v1/nfse/run", admin), HttpStatusCode.OK); // envia (processando)
        await Api.Expect(await api.Post("/admin/v1/nfse/run", admin), HttpStatusCode.OK); // consulta (autorizada)
        var n = await f.WithDb(db => db.NfseDocuments.SingleAsync(x => x.TenantId == t.Id));
        Assert.Equal("issued", n.Status);
        Assert.Equal("123", n.Number);
        await Api.Expect(await api.Put("/admin/v1/settings/nfse", admin, new { enabled = false }, api.Reauth()), HttpStatusCode.OK); // não afeta outros testes
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using Comercial.Api.Services;
using Comercial.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Unimake.Business.DFe.Servicos;
using UniNFCe = Unimake.Business.DFe.Servicos.NFCe;

namespace Comercial.Tests;

/// <summary>
/// SEFAZ simulada para os testes: recebe o serviço JÁ montado, assinado e validado no schema oficial pela biblioteca,
/// e devolve respostas no formato real (retEnviNFe, retConsSitNFe, retEnvEvento, retInutNFe). Nada aqui tem valor fiscal.
/// </summary>
public sealed class FakeSefaz : ISefazTransport
{
    public ConcurrentQueue<XmlDocument> Sent { get; } = new();
    public ConcurrentQueue<string> Calls { get; } = new();
    /// <summary>Próximas respostas forçadas: "fail" (sem conexão), "539", "217", "100" (consulta já autorizada).</summary>
    public ConcurrentQueue<string> Script { get; } = new();
    private int _prot;
    private const string Ns = "http://www.portalfiscal.inf.br/nfe";

    public Task<string> SendAsync(ServicoBase service, CancellationToken ct)
    {
        Script.TryDequeue(out var forced);
        if (forced == "fail") throw new HttpRequestException("sem conexão (simulado)");
        var signed = service.ConteudoXMLAssinado;
        Sent.Enqueue(signed);
        string Key() => ((XmlElement)signed.SelectSingleNode("//*[local-name()='infNFe']")!).GetAttribute("Id")[3..];
        string Prot(string key, int cStat) =>
            $"<protNFe versao=\"4.00\"><infProt><tpAmb>2</tpAmb><verAplic>TESTE</verAplic><chNFe>{key}</chNFe><dhRecbto>2026-10-27T10:00:00-03:00</dhRecbto>" +
            $"<nProt>13526{Interlocked.Increment(ref _prot):0000000000}</nProt><digVal>dGVzdGU=</digVal><cStat>{cStat}</cStat><xMotivo>{(cStat == 100 ? "Autorizado o uso da NF-e" : "Rejeicao simulada")}</xMotivo></infProt></protNFe>";
        switch (service)
        {
            case UniNFCe.Autorizacao:
                Calls.Enqueue("autorizacao");
                var cStat = forced == "539" ? 539 : 100;
                return Task.FromResult($"<retEnviNFe xmlns=\"{Ns}\" versao=\"4.00\"><tpAmb>2</tpAmb><verAplic>TESTE</verAplic><cStat>104</cStat><xMotivo>Lote processado</xMotivo><cUF>35</cUF>" +
                                       $"<dhRecbto>2026-10-27T10:00:00-03:00</dhRecbto>{Prot(Key(), cStat)}</retEnviNFe>");
            case UniNFCe.ConsultaProtocolo:
                Calls.Enqueue("consulta");
                var key = signed.SelectSingleNode("//*[local-name()='chNFe']")!.InnerText;
                return Task.FromResult(forced == "100"
                    ? $"<retConsSitNFe xmlns=\"{Ns}\" versao=\"4.00\"><tpAmb>2</tpAmb><verAplic>TESTE</verAplic><cStat>100</cStat><xMotivo>Autorizado</xMotivo><cUF>35</cUF><chNFe>{key}</chNFe>{Prot(key, 100)}</retConsSitNFe>"
                    : $"<retConsSitNFe xmlns=\"{Ns}\" versao=\"4.00\"><tpAmb>2</tpAmb><verAplic>TESTE</verAplic><cStat>217</cStat><xMotivo>NF-e nao consta na base de dados da SEFAZ</xMotivo><cUF>35</cUF><chNFe>{key}</chNFe></retConsSitNFe>");
            case UniNFCe.RecepcaoEvento:
                Calls.Enqueue("cancelamento");
                var ch = signed.SelectSingleNode("//*[local-name()='chNFe']")!.InnerText;
                return Task.FromResult($"<retEnvEvento xmlns=\"{Ns}\" versao=\"1.00\"><idLote>1</idLote><tpAmb>2</tpAmb><verAplic>TESTE</verAplic><cOrgao>35</cOrgao><cStat>128</cStat><xMotivo>Lote de evento processado</xMotivo>" +
                                       $"<retEvento versao=\"1.00\"><infEvento><tpAmb>2</tpAmb><verAplic>TESTE</verAplic><cOrgao>35</cOrgao><cStat>135</cStat><xMotivo>Evento registrado e vinculado a NF-e</xMotivo><chNFe>{ch}</chNFe>" +
                                       "<tpEvento>110111</tpEvento><nSeqEvento>1</nSeqEvento><dhRegEvento>2026-10-27T10:05:00-03:00</dhRegEvento><nProt>135260000009999</nProt></infEvento></retEvento></retEnvEvento>");
            case UniNFCe.Inutilizacao:
                Calls.Enqueue("inutilizacao");
                return Task.FromResult($"<retInutNFe xmlns=\"{Ns}\" versao=\"4.00\"><infInut><tpAmb>2</tpAmb><verAplic>TESTE</verAplic><cStat>102</cStat><xMotivo>Inutilizacao de numero homologado</xMotivo>" +
                                       "<cUF>35</cUF><ano>26</ano><CNPJ>11222333000181</CNPJ><mod>65</mod><serie>1</serie><nNFIni>50</nNFIni><nNFFin>51</nNFFin><dhRecbto>2026-10-27T10:10:00-03:00</dhRecbto><nProt>135260000008888</nProt></infInut></retInutNFe>");
            default: throw new InvalidOperationException("serviço inesperado");
        }
    }
}

public class SefazFactory : ApiFactory
{
    public FakeSefaz Sefaz { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        base.ConfigureWebHost(b);
        b.ConfigureTestServices(s => { s.RemoveAll<ISefazTransport>(); s.AddSingleton<ISefazTransport>(Sefaz); });
    }
}

/// <summary>Emissão direta da NFC-e na SEFAZ (sem provedor pago), com certificado de TESTE autoassinado.</summary>
public class SefazDirectTests(SefazFactory f) : IClassFixture<SefazFactory>
{
    private readonly Api api = new(f);
    private const string Cnpj = "11222333000181";

    public static byte[] TestPfx(string cnpj, string password, int days = 365)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN=LOJA TESTE LTDA:{cnpj}, O=ICP-Brasil (TESTE), C=BR", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(days));
        return cert.Export(X509ContentType.Pkcs12, password);
    }

    private async Task<HttpResponseMessage> UploadCert(string token, byte[] pfx, string password)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(password), "password");
        var file = new ByteArrayContent(pfx);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-pkcs12");
        form.Add(file, "file", "certificado.pfx");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/fiscal/certificate") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await api.Http.SendAsync(req);
    }

    private async Task<(TenantInfo T, Guid Cash, Guid Product)> DirectShop()
    {
        var t = await api.CreateTenant("sz-" + Guid.NewGuid().ToString("N")[..6]);
        await Api.Expect(await api.Put("/api/v1/fiscal/profile", t.OwnerToken, new
        {
            cnpj = Cnpj, stateRegistration = "111222333444", regime = 1, uf = "SP", environment = 2, provider = "sefaz", nfceSeries = 1, autoEmit = true,
            legalName = "LOJA TESTE LTDA", tradeName = "Loja Teste", street = "RUA DAS FLORES", addressNumber = "100", district = "CENTRO",
            cityCode = 3550308, cityName = "SAO PAULO", cep = "01001-000", phone = "(11) 3333-4444",
        }), HttpStatusCode.OK);
        await Api.Expect(await UploadCert(t.OwnerToken, TestPfx(Cnpj, "senha-teste"), "senha-teste"), HttpStatusCode.OK);
        var p = await Api.Expect(await api.Post("/api/v1/products", t.OwnerToken, new
        {
            sku = "VAR-1", name = "Caneca de porcelana", unit = "UN", priceCents = 1990, costCents = 900, minStock = 0m, ncm = "69120000", cfop = "5102", fiscalOrigin = 0,
            ibsCbsCst = "000", ibsCbsClass = "000001",
        }), HttpStatusCode.Created);
        var id = p.GetProperty("id").GetGuid();
        await api.StockEntry(t.OwnerToken, t.StoreId, id, 100);
        return (t, await api.OpenCash(t.OwnerToken, t.StoreId, 0), id);
    }

    private Task Job() => f.WithScopeResult(sp => FiscalJob.RunOnceAsync(sp, CancellationToken.None));

    private async Task<FiscalDocument> Doc(Guid saleId) => await f.WithDb(db => db.FiscalDocuments.IgnoreQueryFilters().AsNoTracking().FirstAsync(d => d.SaleId == saleId));

    private async Task<Guid> Sell((TenantInfo T, Guid Cash, Guid Product) s, int qty = 1, int method = 1, long pay = 0) =>
        (await Api.Expect(await api.Post("/api/v1/sales", s.T.OwnerToken, Api.Sale(s.T.StoreId, s.Cash, s.Product, qty, pay == 0 ? 1990 * qty : pay, method)), HttpStatusCode.Created))
        .GetProperty("id").GetGuid();

    [Fact]
    public async Task Certificado_e_conferido_e_nunca_volta_pela_api()
    {
        var t = await api.CreateTenant("szc-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal("fiscal_profile_missing", await Api.Code(await UploadCert(t.OwnerToken, TestPfx(Cnpj, "x"), "x")));
        await Api.Expect(await api.Put("/api/v1/fiscal/profile", t.OwnerToken, new { cnpj = Cnpj, regime = 1, uf = "SP", environment = 2, provider = "sefaz", nfceSeries = 1, autoEmit = true }), HttpStatusCode.OK);
        Assert.Equal("certificate_invalid", await Api.Code(await UploadCert(t.OwnerToken, TestPfx(Cnpj, "certa"), "errada")));
        Assert.Equal("certificate_cnpj_mismatch", await Api.Code(await UploadCert(t.OwnerToken, TestPfx("99888777000166", "s"), "s")));
        Assert.Equal("certificate_expired", await Api.Code(await UploadCert(t.OwnerToken, TestPfx(Cnpj, "s", days: -2), "s")));
        var view = await Api.Expect(await UploadCert(t.OwnerToken, TestPfx(Cnpj, "senha-boa"), "senha-boa"), HttpStatusCode.OK);
        Assert.True(view.GetProperty("hasCertificate").GetBoolean());
        Assert.DoesNotContain("senha-boa", view.ToString());
        Assert.DoesNotContain("certificateProtected", view.ToString(), StringComparison.OrdinalIgnoreCase);
        // Falta endereço: a tela mostra o que falta e nada é emitido.
        Assert.Contains(view.GetProperty("directProblems").EnumerateArray(), x => x.GetString() == "endereço completo");
        var (_, op, _) = await api.CreateUser(t, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadCert(op, TestPfx(Cnpj, "s"), "s")).StatusCode);
    }

    [Fact]
    public async Task Venda_vira_nfce_autorizada_direto_na_sefaz_com_xml_valido_e_numeracao_sequencial()
    {
        var s = await DirectShop();
        var a = await Sell(s, 2, method: 4);   // PIX
        var b = await Sell(s, 1, method: 1);   // dinheiro
        await Job();
        var da = await Doc(a); var db1 = await Doc(b);
        Assert.Equal(FiscalStatus.Authorized, da.Status);
        Assert.Equal(FiscalStatus.Authorized, db1.Status);
        Assert.Equal(new[] { 1, 2 }, new[] { da.Number!.Value, db1.Number!.Value }.Order());
        Assert.Equal(44, da.AccessKey!.Length);
        Assert.False(string.IsNullOrEmpty(da.Protocol));
        Assert.False(string.IsNullOrEmpty(da.QrCodeUrl));

        // XML enviado: assinado, em homologação com o texto obrigatório, PIX com grupo de cartão "não integrado".
        var sent = f.Sefaz.Sent.Last(x => x.SelectSingleNode("//*[local-name()='chNFe' or local-name()='infNFe']") is XmlElement e && e.GetAttribute("Id") == "NFe" + da.AccessKey);
        Assert.NotNull(sent.SelectSingleNode("//*[local-name()='Signature']"));
        Assert.Equal(SefazNfceProvider.HomologationText, sent.SelectSingleNode("//*[local-name()='det'][1]//*[local-name()='xProd']")!.InnerText);
        Assert.Equal("39.80", sent.SelectSingleNode("//*[local-name()='ICMSTot']/*[local-name()='vNF']")!.InnerText);
        Assert.Equal("17", sent.SelectSingleNode("//*[local-name()='detPag']/*[local-name()='tPag']")!.InnerText);
        Assert.Equal("2", sent.SelectSingleNode("//*[local-name()='card']/*[local-name()='tpIntegra']")!.InnerText);

        // O XML autorizado (nfeProc) já entrou no arquivo fiscal, sem baixar de ninguém.
        var file = await f.WithDb(x => x.FiscalXmlFiles.IgnoreQueryFilters().AsNoTracking().FirstAsync(z => z.FiscalDocumentId == da.Id));
        Assert.Null(FiscalArchiveService.Validate(FiscalXmlKind.Authorization, file.Content, da.AccessKey));
        Assert.Equal("sefaz-direto", file.Source);
        Assert.Equal(3, await f.WithDb(x => x.FiscalProfiles.IgnoreQueryFilters().Where(p => p.TenantId == s.T.Id).Select(p => p.NfceNextNumber).FirstAsync()));
    }

    [Fact]
    public async Task Cancelamento_e_inutilizacao_guardam_o_xml_do_evento()
    {
        var s = await DirectShop();
        var sale = await Sell(s);
        await Job();
        var doc = await Doc(sale);
        await Api.Expect(await api.Post($"/api/v1/fiscal/documents/{doc.Id}/cancel", s.T.OwnerToken, new { reason = "Cliente desistiu da compra no caixa" }), HttpStatusCode.OK);
        var cancel = await f.WithDb(x => x.FiscalXmlFiles.IgnoreQueryFilters().AsNoTracking().FirstAsync(z => z.FiscalDocumentId == doc.Id && z.Kind == FiscalXmlKind.Cancellation));
        Assert.Null(FiscalArchiveService.Validate(FiscalXmlKind.Cancellation, cancel.Content, doc.AccessKey));

        var v = await Api.Expect(await api.Post("/api/v1/fiscal/void-numbers", s.T.OwnerToken, new { series = 1, firstNumber = 50, lastNumber = 51, reason = "Numeracao pulada por falha do sistema" }), HttpStatusCode.OK);
        Assert.Equal("autorizada", v.GetProperty("status").GetString());
        var voidId = v.GetProperty("id").GetGuid();
        Assert.True(await f.WithDb(x => x.FiscalXmlFiles.IgnoreQueryFilters().AnyAsync(z => z.NumberVoidId == voidId)));
    }

    [Fact]
    public async Task Queda_de_conexao_consulta_antes_de_reenviar_e_nao_duplica_a_nota()
    {
        var s = await DirectShop();
        var sale = await Sell(s);
        f.Sefaz.Script.Enqueue("fail");
        await Job();
        var doc = await Doc(sale);
        Assert.Equal(FiscalStatus.PendingTransmission, doc.Status);
        var key = doc.AccessKey;
        // A SEFAZ tinha autorizado mesmo sem responder: a nova tentativa consulta e NÃO reenvia.
        await f.WithDb(async x => { var d = await x.FiscalDocuments.IgnoreQueryFilters().FirstAsync(z => z.Id == doc.Id); d.NextAttemptAt = null; await x.SaveChangesAsync(); });
        var before = f.Sefaz.Calls.Count(c => c == "autorizacao");
        f.Sefaz.Script.Enqueue("100");
        await Job();
        doc = await Doc(sale);
        Assert.Equal(FiscalStatus.Authorized, doc.Status);
        Assert.Equal(before, f.Sefaz.Calls.Count(c => c == "autorizacao"));
        Assert.Equal(1, doc.Number); // mesmo número
        Assert.True(await f.WithDb(x => x.FiscalXmlFiles.IgnoreQueryFilters().AnyAsync(z => z.FiscalDocumentId == doc.Id)));
    }

    [Fact]
    public async Task Rejeicao_por_numero_ja_usado_pega_novo_numero_na_proxima_tentativa()
    {
        var s = await DirectShop();
        var sale = await Sell(s);
        f.Sefaz.Script.Enqueue("539"); // 1ª tentativa vai direto à autorização
        await Job();
        var doc = await Doc(sale);
        Assert.Equal(FiscalStatus.Rejected, doc.Status);
        Assert.Null(doc.Number);
        await Api.Expect(await api.Post($"/api/v1/fiscal/documents/{doc.Id}/transmit", s.T.OwnerToken), HttpStatusCode.OK);
        doc = await Doc(sale);
        Assert.Equal(FiscalStatus.Authorized, doc.Status);
        Assert.Equal(2, doc.Number);
    }

    [Fact]
    public async Task Caixa_pede_a_nfce_logo_apos_a_venda_e_recebe_o_danfe_para_imprimir()
    {
        var s = await DirectShop();
        var key = "sale-" + Guid.NewGuid().ToString("N");
        await Api.Expect(await api.Post("/api/v1/sales", s.T.OwnerToken, Api.Sale(s.T.StoreId, s.Cash, s.Product, 3, 6000, 1, key)), HttpStatusCode.Created);
        // Sem esperar o serviço automático: o caixa pede e a nota sai na hora.
        var d = await Api.Expect(await api.Post($"/api/v1/fiscal/nfce/sale/{key}", s.T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal("autorizada", d.GetProperty("status").GetString());
        Assert.Equal(44, d.GetProperty("accessKey").GetString()!.Length);
        Assert.False(string.IsNullOrEmpty(d.GetProperty("qrCode").GetString()));
        Assert.True(d.GetProperty("homologation").GetBoolean());
        Assert.Equal("LOJA TESTE LTDA", d.GetProperty("emitterName").GetString());
        Assert.Equal(5970, d.GetProperty("totalCents").GetInt64());
        Assert.Equal(30, d.GetProperty("changeCents").GetInt64());
        // Pedir de novo (reimpressão) não emite outra nota.
        var calls = f.Sefaz.Calls.Count;
        var again = await Api.Expect(await api.Get($"/api/v1/fiscal/nfce/sale/{key}", s.T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(d.GetProperty("accessKey").GetString(), again.GetProperty("accessKey").GetString());
        Assert.Equal(calls, f.Sefaz.Calls.Count);

        // Cupom térmico (ESC/POS) com os blocos obrigatórios e o QR Code nativo.
        var danfe = System.Text.Json.JsonSerializer.Deserialize<Comercial.Desktop.Core.Hardware.NfceDanfe>(d.GetRawText(), Api.Json)!;
        Assert.True(danfe.Printable);
        var bytes = Comercial.Desktop.Core.Hardware.DanfeEscPos.Build(danfe, 80, false);
        var text = Encoding.GetEncoding(860).GetString(bytes);
        Assert.Contains("CONSUMIDOR NÃO IDENTIFICADO", text);
        Assert.Contains("EMITIDA EM AMBIENTE DE HOMOLOGAÇÃO", text);
        Assert.Contains(Comercial.Desktop.Core.Hardware.NfceDanfe.GroupKey(danfe.AccessKey![..24]), text); // chave em 2 linhas de grupos de 4
        Assert.Contains(Comercial.Desktop.Core.Hardware.NfceDanfe.GroupKey(danfe.AccessKey![24..]), text);
        Assert.Contains("Protocolo de autorização: " + danfe.Protocol, text);
        Assert.True(bytes.AsSpan().IndexOf(new byte[] { 0x1D, 0x28, 0x6B }) >= 0);

        // Loja sem NFC-e configurada: o caixa imprime o comprovante de sempre.
        var plain = await api.CreateTenant("szp-" + Guid.NewGuid().ToString("N")[..6]);
        var pid = await api.CreateProduct(plain.OwnerToken, "P1");
        await api.StockEntry(plain.OwnerToken, plain.StoreId, pid, 5);
        var cash = await api.OpenCash(plain.OwnerToken, plain.StoreId, 0);
        var k2 = "sale-" + Guid.NewGuid().ToString("N");
        await Api.Expect(await api.Post("/api/v1/sales", plain.OwnerToken, Api.Sale(plain.StoreId, cash, pid, 1, 1000, 1, k2)), HttpStatusCode.Created);
        Assert.Equal("sem_nfce", (await Api.Expect(await api.Post($"/api/v1/fiscal/nfce/sale/{k2}", plain.OwnerToken), HttpStatusCode.OK)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Schema_oficial_e_conferido_antes_de_enviar()
    {
        var s = await DirectShop();
        // NCM inválido direto no banco (o cadastro normal já recusa): o schema da SEFAZ precisa barrar antes do envio.
        await f.WithDb(async x => { var p = await x.Products.IgnoreQueryFilters().FirstAsync(z => z.Id == s.Product); p.Ncm = "1234"; await x.SaveChangesAsync(); });
        var calls = f.Sefaz.Calls.Count;
        var sale = await Sell(s);
        await Job();
        var doc = await Doc(sale);
        Assert.Equal(FiscalStatus.Rejected, doc.Status);
        Assert.StartsWith("XML recusado na validação do schema", doc.LastMessage);
        Assert.Equal(calls, f.Sefaz.Calls.Count);
    }

    [Fact]
    public async Task Cst_sem_suporte_e_recusado_com_mensagem_clara_sem_enviar()
    {
        var s = await DirectShop();
        var p = await Api.Expect(await api.Post("/api/v1/products", s.T.OwnerToken, new
        {
            sku = "VAR-2", name = "Produto com credito", unit = "UN", priceCents = 1000, costCents = 500, minStock = 0m, ncm = "69120000", cfop = "5102", fiscalOrigin = 0, icmsCst = "101",
        }), HttpStatusCode.Created);
        var id = p.GetProperty("id").GetGuid();
        await api.StockEntry(s.T.OwnerToken, s.T.StoreId, id, 10);
        var calls = f.Sefaz.Calls.Count;
        var sale = (await Api.Expect(await api.Post("/api/v1/sales", s.T.OwnerToken, Api.Sale(s.T.StoreId, s.Cash, id, 1, 1000)), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await Job();
        var doc = await Doc(sale);
        Assert.Equal(FiscalStatus.Rejected, doc.Status);
        Assert.Contains("CSOSN 101", doc.LastMessage);
        Assert.Equal(calls, f.Sefaz.Calls.Count);
    }
}

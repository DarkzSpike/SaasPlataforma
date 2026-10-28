using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Comercial.Api.Services;
using Comercial.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Comercial.Tests;

public class AccountingFactory : FiscalFactory
{
    public FakeEmailTransport Mail { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        base.ConfigureWebHost(b);
        b.ConfigureTestServices(s => s.AddSingleton<IEmailTransport>(Mail));
    }
    public Task Flush() => WithScope(sp => sp.GetRequiredService<EmailSender>().SendPendingAsync());
}

/// <summary>
/// Fiscal e Contabilidade: arquivo dos XMLs originais, exportação ZIP, relatórios, fechamento mensal e envio ao contador.
/// Os XMLs usados aqui são FICTÍCIOS (gerados pelo provedor simulado), apenas para teste.
/// </summary>
public class AccountingTests(AccountingFactory f) : IClassFixture<AccountingFactory>
{
    private readonly Api api = new(f);
    private static readonly (int Year, int Month) Now = (DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3)).Year, DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3)).Month);
    private static string FirstDay => $"{Now.Year}-{Now.Month:00}-01";
    private static string LastDay => new DateOnly(Now.Year, Now.Month, 1).AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");

    private sealed record Shop(TenantInfo T, Guid Cash, Guid Product);

    private async Task<Shop> NewShop(string plan = "profissional")
    {
        var t = await api.CreateTenant("ct-" + Guid.NewGuid().ToString("N")[..6], plan);
        if (plan == "profissional")
            await Api.Expect(await api.Put("/api/v1/fiscal/profile", t.OwnerToken, new
            {
                cnpj = "11222333000181", stateRegistration = "123456789", regime = 1, uf = "SP", environment = 2, provider = "focus", providerToken = "token-teste", nfceSeries = 1, autoEmit = true,
            }), HttpStatusCode.OK);
        var p = await Api.Expect(await api.Post("/api/v1/products", t.OwnerToken, new
        {
            sku = "P1", name = "Produto teste", unit = "UN", priceCents = 2500, costCents = 1000, minStock = 0m, ncm = "61091000", cfop = "5102", fiscalOrigin = 0, ibsCbsCst = "000", ibsCbsClass = "000001",
        }), HttpStatusCode.Created);
        var id = p.GetProperty("id").GetGuid();
        await api.StockEntry(t.OwnerToken, t.StoreId, id, 100);
        return new(t, await api.OpenCash(t.OwnerToken, t.StoreId, 0), id);
    }

    private async Task<Guid> Sell(Shop s, int qty = 1, int method = 1)
    {
        var r = await Api.Expect(await api.Post("/api/v1/sales", s.T.OwnerToken, Api.Sale(s.T.StoreId, s.Cash, s.Product, qty, 2500 * qty, method)), HttpStatusCode.Created);
        return r.GetProperty("id").GetGuid();
    }

    private Task Job() => f.WithScopeResult(sp => FiscalJob.RunOnceAsync(sp, CancellationToken.None));

    private async Task<JsonElement> Docs(Shop s, string query = "") =>
        await Api.Expect(await api.Get($"/api/v1/fiscal/docs?from={FirstDay}&to={LastDay}{query}", s.T.OwnerToken), HttpStatusCode.OK);

    private async Task<byte[]> Bytes(string path, string token)
    {
        var res = await api.Get(path, token);
        Assert.True(res.StatusCode == HttpStatusCode.OK, path + " -> " + res.StatusCode + " " + await res.Content.ReadAsStringAsync());
        return await res.Content.ReadAsByteArrayAsync();
    }

    private static Dictionary<string, byte[]> Unzip(byte[] zip)
    {
        using var a = new ZipArchive(new MemoryStream(zip));
        return a.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); });
    }

    private async Task<JsonElement> Export(Shop s, bool reports = true) =>
        await Api.Expect(await api.Post("/api/v1/fiscal/exports", s.T.OwnerToken, new { year = Now.Year, month = Now.Month, includeReports = reports }), HttpStatusCode.OK);

    [Fact]
    public async Task Xml_original_e_arquivado_sem_alteracao_e_exportado_com_eventos_inutilizacao_e_manifesto()
    {
        f.Fiscal.Mode = "authorize"; f.Fiscal.XmlMode = "ok";
        var s = await NewShop();
        await Sell(s); await Sell(s, 2);
        await Job();
        var list = await Docs(s);
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var first = list.GetProperty("items").EnumerateArray().First();
        Assert.True(first.GetProperty("hasXml").GetBoolean());
        Assert.Equal("autorizada", first.GetProperty("status").GetString());
        var key = first.GetProperty("accessKey").GetString()!;

        // O XML baixado é byte a byte o que o provedor entregou.
        var xml = await Bytes($"/api/v1/fiscal/docs/{first.GetProperty("id").GetGuid()}/xml", s.T.OwnerToken);
        Assert.Equal(f.Fiscal.Xmls[$"https://exemplo/nota-{key}.xml"], xml);

        // Cancelamento (dentro de 30 min) e inutilização: eventos também arquivados.
        await Api.Expect(await api.Post($"/api/v1/fiscal/documents/{first.GetProperty("id").GetGuid()}/cancel", s.T.OwnerToken, new { reason = "Cliente desistiu da compra no caixa" }), HttpStatusCode.OK);
        await Api.Expect(await api.Post("/api/v1/fiscal/void-numbers", s.T.OwnerToken, new { series = 1, firstNumber = 900, lastNumber = 901, reason = "Numeração pulada por falha de energia" }), HttpStatusCode.OK);
        await Job();
        var cancelXml = await Bytes($"/api/v1/fiscal/docs/{first.GetProperty("id").GetGuid()}/xml?kind=cancelamento", s.T.OwnerToken);
        Assert.Contains("110111", Encoding.UTF8.GetString(cancelXml));
        // Rodar de novo não duplica nada (um original por documento e tipo).
        await Job();
        Assert.Equal(4, await f.WithDb(db => db.FiscalXmlFiles.IgnoreQueryFilters().CountAsync(x => x.TenantId == s.T.Id)));

        var exp = await Export(s);
        Assert.False(exp.GetProperty("reused").GetBoolean());
        var e = exp.GetProperty("export");
        Assert.Equal(2, e.GetProperty("documentCount").GetInt32());
        Assert.Equal(1, e.GetProperty("eventCount").GetInt32());
        Assert.Equal(1, e.GetProperty("voidCount").GetInt32());
        Assert.Equal(0, e.GetProperty("missingCount").GetInt32());

        var zipBytes = await Bytes($"/api/v1/fiscal/exports/{e.GetProperty("id").GetGuid()}/download", s.T.OwnerToken);
        Assert.Equal(e.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant());
        var files = Unzip(zipBytes);
        var root = $"Exportacao_Contabilidade/11222333000181/{Now.Year}/{Now.Month:00}_";
        Assert.Contains(files.Keys, k => k.StartsWith(root) && k.EndsWith($"/NFCe/autorizadas/{key}-procNFe.xml"));
        Assert.Equal(xml, files.Single(k => k.Key.EndsWith($"autorizadas/{key}-procNFe.xml")).Value);
        Assert.Contains(files.Keys, k => k.EndsWith($"/NFCe/eventos/{key}-110111-cancelamento-procEventoNFe.xml"));
        Assert.Contains(files.Keys, k => k.Contains("/NFCe_inutilizacoes/inutilizacao-serie1-900-a-901"));
        Assert.Contains(files.Keys, k => k.EndsWith("/Relatorios/caixa.csv"));
        Assert.Contains(files.Keys, k => k.EndsWith("/resumo_exportacao.txt"));
        Assert.Equal(files.Count(k => k.Key.EndsWith(".xml")), files.Select(k => k.Key).Distinct().Count(k => k.EndsWith(".xml")));

        // Manifesto confere com cada arquivo (formato sha256sum).
        var manifest = Encoding.UTF8.GetString(files.Single(k => k.Key.EndsWith("manifesto_sha256.txt")).Value).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var monthRoot = files.Keys.First(k => k.EndsWith("manifesto_sha256.txt"))[..^"manifesto_sha256.txt".Length];
        foreach (var line in manifest)
        {
            var (hash, path) = (line[..64], line[66..]);
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(files[monthRoot + path])).ToLowerInvariant());
        }

        // Pacote idêntico é reaproveitado (sem duplicar) e o histórico registra quem gerou.
        var again = await Export(s);
        Assert.True(again.GetProperty("reused").GetBoolean());
        Assert.Equal(e.GetProperty("id").GetGuid(), again.GetProperty("export").GetProperty("id").GetGuid());
        var history = await Api.Expect(await api.Get($"/api/v1/fiscal/exports?year={Now.Year}&month={Now.Month}", s.T.OwnerToken), HttpStatusCode.OK);
        Assert.Single(history.EnumerateArray());
        Assert.True(await f.WithDb(db => db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.TenantId == s.T.Id && a.Action == "fiscal.export_created")));
    }

    [Fact]
    public async Task Xml_que_nao_confere_nao_e_arquivado_e_vira_pendencia_e_divergencia()
    {
        f.Fiscal.Mode = "authorize";
        var s = await NewShop();
        await Sell(s);
        f.Fiscal.XmlMode = "wrong_key";
        try { await Job(); } finally { f.Fiscal.XmlMode = "ok"; }
        var doc = (await Docs(s)).GetProperty("items").EnumerateArray().Single();
        Assert.False(doc.GetProperty("hasXml").GetBoolean());
        Assert.Equal("xml_not_archived", await Api.Code(await api.Get($"/api/v1/fiscal/docs/{doc.GetProperty("id").GetGuid()}/xml", s.T.OwnerToken)));
        var detail = await Api.Expect(await api.Get($"/api/v1/fiscal/docs/{doc.GetProperty("id").GetGuid()}", s.T.OwnerToken), HttpStatusCode.OK);
        Assert.Contains(detail.GetProperty("events").EnumerateArray(), e => e.GetProperty("message").GetString()!.Contains("chave de acesso diferente"));

        var closing = await Api.Expect(await api.Get($"/api/v1/fiscal/closing/{Now.Year}/{Now.Month}", s.T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal("divergencia", closing.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "xml").GetProperty("state").GetString());
        var exp = (await Export(s)).GetProperty("export");
        Assert.Equal(1, exp.GetProperty("missingCount").GetInt32());
        Assert.Equal(0, exp.GetProperty("documentCount").GetInt32());
        var files = Unzip(await Bytes($"/api/v1/fiscal/exports/{exp.GetProperty("id").GetGuid()}/download", s.T.OwnerToken));
        Assert.Contains(files.Keys, k => k.EndsWith("pendencias.csv"));
        Assert.DoesNotContain(files.Keys, k => k.EndsWith("procNFe.xml")); // nada é reconstruído

        // "Buscar XMLs no provedor": com o provedor correto, o original entra no arquivo.
        var fetched = await Api.Expect(await api.Post("/api/v1/fiscal/archive/fetch", s.T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(1, fetched.GetProperty("stored").GetInt32());
        Assert.True((await Docs(s)).GetProperty("items").EnumerateArray().Single().GetProperty("hasXml").GetBoolean());
    }

    [Fact]
    public void Validacao_recusa_xxe_raiz_errada_e_situacao_errada()
    {
        var key = "3526" + new string('7', 40);
        Assert.Null(FiscalArchiveService.Validate(FiscalXmlKind.Authorization, FakeFiscalProvider.NfeProc(key, 1000), key));
        Assert.NotNull(FiscalArchiveService.Validate(FiscalXmlKind.Authorization, FakeFiscalProvider.NfeProc(key, 1000, 301), key)); // denegada não passa como autorizada
        Assert.Null(FiscalArchiveService.Validate(FiscalXmlKind.Authorization, FakeFiscalProvider.NfeProc(key, 1000, 301), key, denied: true));
        Assert.NotNull(FiscalArchiveService.Validate(FiscalXmlKind.Authorization, FakeFiscalProvider.CancelProc(key), key));
        Assert.NotNull(FiscalArchiveService.Validate(FiscalXmlKind.Cancellation, FakeFiscalProvider.CancelProc(key), "3526" + new string('1', 40)));
        var xxe = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><nfeProc>&e;</nfeProc>");
        Assert.Equal("arquivo não é um XML válido", FiscalArchiveService.Validate(FiscalXmlKind.Authorization, xxe, null));
        Assert.Null(FiscalArchiveService.Validate(FiscalXmlKind.NumberVoid, FakeFiscalProvider.VoidProc(), null));
    }

    [Fact]
    public async Task Denegada_rejeitada_e_pendente_sao_distintas_e_entram_nas_verificacoes()
    {
        var s = await NewShop();
        f.Fiscal.XmlMode = "ok";
        f.Fiscal.Mode = "deny"; await Sell(s); await Job();
        f.Fiscal.Mode = "reject"; await Sell(s); await Job();
        f.Fiscal.Mode = "retry"; await Sell(s); await Job();
        f.Fiscal.Mode = "authorize";
        var items = (await Docs(s)).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, d => d.GetProperty("status").GetString() == "denegada" && d.GetProperty("hasXml").GetBoolean());
        Assert.Contains(items, d => d.GetProperty("status").GetString() == "rejeitada");
        Assert.Contains(items, d => d.GetProperty("status").GetString() == "pendente");
        Assert.Equal(1, (await Docs(s, "&status=denegada")).GetProperty("total").GetInt32());

        var checks = (await Api.Expect(await api.Get($"/api/v1/fiscal/closing/{Now.Year}/{Now.Month}", s.T.OwnerToken), HttpStatusCode.OK))
            .GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("key").GetString()!, c => c.GetProperty("state").GetString());
        Assert.Equal("em_analise", checks["rejeitados"]);
        Assert.Equal("pendente", checks["vendas_documento"]);
        Assert.Equal("concluido", checks["xml"]);
        Assert.Equal("pendente", checks["caixas"]); // caixa ainda aberto

        var exp = (await Export(s)).GetProperty("export");
        var files = Unzip(await Bytes($"/api/v1/fiscal/exports/{exp.GetProperty("id").GetGuid()}/download", s.T.OwnerToken));
        Assert.Contains(files.Keys, k => k.Contains("/NFCe/denegadas/"));
    }

    [Fact]
    public async Task Relatorios_somam_corretamente_e_separam_periodos()
    {
        f.Fiscal.Mode = "authorize"; f.Fiscal.XmlMode = "ok";
        var s = await NewShop();
        await Sell(s, 1, method: 1);   // dinheiro 25,00
        await Sell(s, 2, method: 4);   // PIX 50,00 (manual, sem cobrança do Mercado Pago)
        var old = await Sell(s, 1, method: 2); // débito — movida para o mês anterior
        await f.WithDb(async db =>
        {
            var sale = await db.Sales.IgnoreQueryFilters().FirstAsync(x => x.Id == old);
            sale.ReceivedAt = sale.ReceivedAt.AddMonths(-1).AddDays(-1);
            await db.SaveChangesAsync();
        });
        await Job();

        var pays = await Api.Expect(await api.Get($"/api/v1/fiscal/reports/pagamentos?from={FirstDay}&to={LastDay}", s.T.OwnerToken), HttpStatusCode.OK);
        var rows = pays.GetProperty("rows").EnumerateArray().ToDictionary(r => r[0].GetString()!, r => r);
        Assert.Equal("25,00", rows["Dinheiro"][2].GetString());
        Assert.Equal("50,00", rows["PIX"][2].GetString());
        Assert.Equal("0,00", rows["Cartão de débito"][2].GetString()); // venda do mês anterior não entra
        Assert.Contains(pays.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("NÃO é o mesmo"));

        var fiscal = await Api.Expect(await api.Get($"/api/v1/fiscal/reports/vendas_fiscal?from={FirstDay}&to={LastDay}", s.T.OwnerToken), HttpStatusCode.OK);
        Assert.Contains(fiscal.GetProperty("totals").EnumerateArray(), t => t.GetProperty("label").GetString() == "NFC-e autorizadas" && t.GetProperty("value").GetString() == "2 • 75,00");
        Assert.Contains(fiscal.GetProperty("totals").EnumerateArray(), t => t.GetProperty("label").GetString()!.StartsWith("Tributos nos XMLs (2 notas)") && t.GetProperty("value").GetString()!.Contains("vNF 75,00"));

        var cash = await Api.Expect(await api.Get($"/api/v1/fiscal/reports/caixa?from={FirstDay}&to={LastDay}", s.T.OwnerToken), HttpStatusCode.OK);
        var c = cash.GetProperty("rows").EnumerateArray().Single();
        Assert.Equal("25,00", c[6].GetString()); // dinheiro do caixa
        Assert.Equal("aberto", c[19].GetString());

        // PIX sem confirmação do Mercado Pago aparece na conciliação.
        var recon = await Api.Expect(await api.Get($"/api/v1/fiscal/reports/conciliacao?from={FirstDay}&to={LastDay}", s.T.OwnerToken), HttpStatusCode.OK);
        Assert.Contains(recon.GetProperty("rows").EnumerateArray(), r => r[5].GetString()!.Contains("sem confirmação"));

        var csv = Encoding.UTF8.GetString(await Bytes($"/api/v1/fiscal/reports/produtos?from={FirstDay}&to={LastDay}&format=csv", s.T.OwnerToken));
        Assert.Contains("Produto teste;61091000;3;75,00", csv.Replace("P1;", ""));
        Assert.Equal("invalid_report", await Api.Code(await api.Get($"/api/v1/fiscal/reports/nada?from={FirstDay}&to={LastDay}", s.T.OwnerToken)));
    }

    [Fact]
    public async Task Fechamento_exige_observacao_com_pendencias_e_reabertura_e_auditada()
    {
        f.Fiscal.Mode = "authorize"; f.Fiscal.XmlMode = "ok";
        var s = await NewShop();
        await Sell(s); await Job();
        var url = $"/api/v1/fiscal/closing/{Now.Year}/{Now.Month}";
        Assert.Equal("closing_needs_notes", await Api.Code(await api.Post(url + "/close", s.T.OwnerToken, new { notes = "" })));
        var closed = await Api.Expect(await api.Post(url + "/close", s.T.OwnerToken, new { notes = "Caixa do dia segue aberto; conferido com o contador." }), HttpStatusCode.OK);
        Assert.Equal("concluido", closed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Array, closed.GetProperty("checksAtClosing").ValueKind);
        Assert.Equal("already_closed", await Api.Code(await api.Post(url + "/close", s.T.OwnerToken, new { notes = "de novo, mesmo mês fechado" })));
        // Fechar não altera documentos fiscais.
        Assert.Equal("autorizada", (await Docs(s)).GetProperty("items").EnumerateArray().Single().GetProperty("status").GetString());
        Assert.Equal("reason_required", await Api.Code(await api.Post(url + "/reopen", s.T.OwnerToken, new { notes = "x" })));
        var reopened = await Api.Expect(await api.Post(url + "/reopen", s.T.OwnerToken, new { notes = "Contador pediu nova conferência" }), HttpStatusCode.OK);
        Assert.Equal("aberto", reopened.GetProperty("status").GetString());
        Assert.True(await f.WithDb(db => db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.TenantId == s.T.Id && a.Action == "fiscal.month_reopened")));
    }

    [Fact]
    public async Task Envio_ao_contador_reenvio_sem_duplicar_falha_e_historico()
    {
        f.Fiscal.Mode = "authorize"; f.Fiscal.XmlMode = "ok";
        var s = await NewShop();
        await Sell(s); await Job();
        Assert.Equal("accountant_not_configured", await Api.Code(await api.Post("/api/v1/fiscal/accountant/send", s.T.OwnerToken, new { exportId = Guid.NewGuid() })));
        Assert.Equal("invalid_email", await Api.Code(await api.Put("/api/v1/fiscal/accountant", s.T.OwnerToken, new { officeName = "Contab", email = "nao-e-email", autoSend = false, dayOfMonth = 5, hour = 8 })));
        Assert.Equal("invalid_day", await Api.Code(await api.Put("/api/v1/fiscal/accountant", s.T.OwnerToken, new { officeName = "Contab", email = "c@contab.test", autoSend = false, dayOfMonth = 31, hour = 8 })));
        await Api.Expect(await api.Put("/api/v1/fiscal/accountant", s.T.OwnerToken, new
        {
            officeName = "Contabilidade Exemplo", contactName = "Ana", email = "ana@contab.test", extraEmails = "fiscal@contab.test; ana@contab.test", autoSend = false, dayOfMonth = 5, hour = 8,
        }), HttpStatusCode.OK);

        var exportId = (await Export(s)).GetProperty("export").GetProperty("id").GetGuid();
        var sent = await Api.Expect(await api.Post("/api/v1/fiscal/accountant/send", s.T.OwnerToken, new { exportId }), HttpStatusCode.OK);
        Assert.Equal("na_fila", sent.GetProperty("status").GetString());
        Assert.Equal("delivery_in_progress", await Api.Code(await api.Post("/api/v1/fiscal/accountant/send", s.T.OwnerToken, new { exportId })));
        await f.Flush();
        var mails = f.Mail.Sent.Where(m => m.Kind == "accountant_package" && m.TenantId == s.T.Id).ToList();
        Assert.Equal(2, mails.Count); // contador + e-mail adicional (sem repetir)
        Assert.All(mails, m => Assert.True(File.Exists(m.AttachmentPath)));

        var history = await Api.Expect(await api.Get("/api/v1/fiscal/accountant/deliveries", s.T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal("aceito_pelo_servidor", history.EnumerateArray().Single().GetProperty("status").GetString()); // nunca "entregue"

        // Reenvio do MESMO pacote: nenhuma exportação nova.
        var exportsBefore = await f.WithDb(db => db.FiscalExports.IgnoreQueryFilters().CountAsync(e => e.TenantId == s.T.Id));
        var resend = await Api.Expect(await api.Post("/api/v1/fiscal/accountant/send", s.T.OwnerToken, new { exportId }), HttpStatusCode.OK);
        Assert.Equal("reenvio", resend.GetProperty("trigger").GetString());
        Assert.Equal(exportsBefore, await f.WithDb(db => db.FiscalExports.IgnoreQueryFilters().CountAsync(e => e.TenantId == s.T.Id)));

        // Falha do servidor de e-mail: fica registrada e o pacote continua disponível para nova tentativa.
        f.Mail.Fail = true;
        try
        {
            for (var i = 0; i < 7; i++)
            {
                await f.WithDb(async db =>
                {
                    foreach (var m in await db.EmailMessages.IgnoreQueryFilters().Where(m => m.TenantId == s.T.Id && m.Status == EmailStatus.Pending).ToListAsync()) m.NextAttemptAt = null;
                    await db.SaveChangesAsync();
                });
                await f.Flush();
            }
        }
        finally { f.Mail.Fail = false; }
        history = await Api.Expect(await api.Get("/api/v1/fiscal/accountant/deliveries", s.T.OwnerToken), HttpStatusCode.OK);
        var failed = history.EnumerateArray().First();
        Assert.Equal("falhou", failed.GetProperty("status").GetString());
        Assert.Contains("smtp indisponível", failed.GetProperty("detail").GetString());
        await Api.Expect(await api.Post("/api/v1/fiscal/accountant/send", s.T.OwnerToken, new { exportId }), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Envio_automatico_bloqueia_com_pendencias_e_envia_quando_limpo()
    {
        f.Fiscal.XmlMode = "ok";
        var clean = await NewShop();
        var dirty = await NewShop();
        f.Fiscal.Mode = "retry";
        var sale = await Sell(dirty);
        await Job();
        f.Fiscal.Mode = "authorize";
        // Venda com nota pendente no mês anterior (o envio automático manda o mês fechado).
        await f.WithDb(async db =>
        {
            var x = await db.Sales.IgnoreQueryFilters().FirstAsync(v => v.Id == sale);
            x.ReceivedAt = new DateTimeOffset(new DateOnly(Now.Year, Now.Month, 1).AddDays(-10).ToDateTime(new TimeOnly(15, 0)), TimeSpan.FromHours(-3));
            await db.SaveChangesAsync();
        });
        foreach (var s in new[] { clean, dirty })
            await Api.Expect(await api.Put("/api/v1/fiscal/accountant", s.T.OwnerToken, new { officeName = "Contab", email = "c@contab.test", autoSend = true, dayOfMonth = 1, hour = 0 }), HttpStatusCode.OK);

        var result = await f.WithScopeResult(sp => AccountingJob.RunOnceAsync(sp, CancellationToken.None));
        Assert.Equal("enviado", result[clean.T.Id]);
        Assert.Equal("bloqueado", result[dirty.T.Id]);
        // Rodar de novo não envia em duplicidade.
        result = await f.WithScopeResult(sp => AccountingJob.RunOnceAsync(sp, CancellationToken.None));
        Assert.Equal("ja_enviado", result[clean.T.Id]);
        Assert.Equal("aguardando_nova_tentativa", result[dirty.T.Id]);
        var blocked = (await Api.Expect(await api.Get("/api/v1/fiscal/accountant/deliveries", dirty.T.OwnerToken), HttpStatusCode.OK)).EnumerateArray().Single();
        Assert.Equal("bloqueado", blocked.GetProperty("status").GetString());
        Assert.True(await f.WithDb(db => db.Notifications.IgnoreQueryFilters().AnyAsync(n => n.TenantId == dirty.T.Id && n.Type == "accountant_blocked")));
    }

    [Fact]
    public async Task Permissoes_e_modulo_controlam_o_acesso()
    {
        var s = await NewShop();
        var (_, op, _) = await api.CreateUser(s.T, "operator");
        var (_, fin, _) = await api.CreateUser(s.T, "finance");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Get($"/api/v1/fiscal/overview?from={FirstDay}&to={LastDay}", op)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post("/api/v1/fiscal/exports", op, new { year = Now.Year, month = Now.Month })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Put("/api/v1/fiscal/accountant", op, new { officeName = "x", email = "a@b.test", autoSend = false, dayOfMonth = 1, hour = 1 })).StatusCode);
        // Financeiro pode exportar e fechar.
        await Api.Expect(await api.Get($"/api/v1/fiscal/overview?from={FirstDay}&to={LastDay}", fin), HttpStatusCode.OK);
        await Api.Expect(await api.Post("/api/v1/fiscal/exports", fin, new { year = Now.Year, month = Now.Month }), HttpStatusCode.OK);
        // Outra empresa não vê o pacote.
        var other = await NewShop();
        var id = (await Api.Expect(await api.Get("/api/v1/fiscal/exports", s.T.OwnerToken), HttpStatusCode.OK)).EnumerateArray().First().GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/fiscal/exports/{id}/download", other.T.OwnerToken)).StatusCode);
        // Sem o módulo fiscal no plano: bloqueado.
        var basic = await api.CreateTenant("cb-" + Guid.NewGuid().ToString("N")[..6], "basico");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Get($"/api/v1/fiscal/overview?from={FirstDay}&to={LastDay}", basic.OwnerToken)).StatusCode);
    }
}

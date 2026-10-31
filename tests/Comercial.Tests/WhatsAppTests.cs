using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Comercial.Api.Services;
using Comercial.Domain;
using Comercial.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Comercial.Tests;

/// <summary>WhatsApp falso: guarda o que seria enviado pela API oficial da Meta.</summary>
public sealed class FakeWhatsApp : IWhatsAppClient
{
    public ConcurrentQueue<(string To, string Template, IReadOnlyList<string> Params)> Sent { get; } = new();
    public bool FailAll { get; set; }

    public Task<WaCheck> CheckAsync(string phoneNumberId, string token, CancellationToken ct) =>
        Task.FromResult(token.Contains("RECUSADO") ? new WaCheck(false, "token inválido", null, null) : new WaCheck(true, "Conectado", "+55 47 3333-4444", "Loja Teste"));

    public Task<WaSendResult> SendTemplateAsync(string phoneNumberId, string token, string to, string template, string language, IReadOnlyList<string> bodyParams, CancellationToken ct)
    {
        if (FailAll) return Task.FromResult(new WaSendResult(false, null, "Número sem WhatsApp."));
        Sent.Enqueue((to, template, bodyParams));
        return Task.FromResult(new WaSendResult(true, "wamid." + Guid.NewGuid().ToString("N"), null));
    }
}

public class WhatsAppFactory : ApiFactory
{
    public FakeWhatsApp Wa { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        base.ConfigureWebHost(b);
        b.ConfigureTestServices(s => s.AddSingleton<IWhatsAppClient>(Wa));
    }
}

/// <summary>Cobrança do fiado pelo WhatsApp da loja e campanhas de promoção com limites anti-spam.</summary>
public class WhatsAppTests(WhatsAppFactory f) : IClassFixture<WhatsAppFactory>
{
    private readonly Api api = new(f);
    private const string AppSecret = "0123456789abcdef0123456789abcdef";

    private async Task<(TenantInfo T, Guid Cash)> Store()
    {
        var t = await api.CreateTenant("wa-" + Guid.NewGuid().ToString("N")[..6]);
        var admin = await api.AdminToken();
        foreach (var k in new[] { "crm", "financeiro", "contas", "campanhas" })
            await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/modules/{k}/enable", admin, new { confirmTenantCode = t.Code, reason = "teste do WhatsApp" }, api.Reauth()), HttpStatusCode.OK);
        return (t, await api.OpenCash(t.OwnerToken, t.StoreId, 0));
    }

    private static object Settings(string? token = null, bool charges = false, bool promos = false, int limit = 200, int interval = 7, string? secret = null) => new
    {
        phoneNumberId = token is null ? null : "123456789012345", accessToken = token, appSecret = secret, disconnect = false,
        chargeTemplate = "cobranca_fiado", promoTemplate = "promocao_loja", language = "pt_BR",
        chargesEnabled = charges, promotionsEnabled = promos, dailyPromoLimit = limit, promoIntervalDays = interval,
    };

    private async Task<Guid> Customer(TenantInfo t, string name, string? phone, bool consent, long limit = 0)
    {
        var id = (await Api.Expect(await api.Post("/api/v1/customers", t.OwnerToken, new { name, phone, marketingConsent = consent }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        if (limit > 0) await Api.Expect(await api.Put($"/api/v1/customers/{id}/credit-limit", t.OwnerToken, new { limitCents = limit }), HttpStatusCode.OK);
        return id;
    }

    private Task<Dictionary<Guid, (int Charges, int Promos)>> Job() => f.WithScopeResult(sp => WhatsAppJob.RunOnceAsync(sp, CancellationToken.None));

    [Fact]
    public async Task Conecta_e_cobra_o_fiado_com_o_que_foi_comprado_e_no_dia_escolhido()
    {
        var (t, cash) = await Store();
        Assert.Equal("whatsapp_not_connected", await Api.Code(await api.Put("/api/v1/whatsapp/settings", t.OwnerToken, Settings(charges: true))));
        Assert.Equal("whatsapp_rejected", await Api.Code(await api.Put("/api/v1/whatsapp/settings", t.OwnerToken, Settings("EAAG-RECUSADO-1234567890abcdef"))));
        var s = await Api.Expect(await api.Put("/api/v1/whatsapp/settings", t.OwnerToken, Settings("EAAG-token-de-teste-1234567890abcdef", charges: true)), HttpStatusCode.OK);
        Assert.True(s.GetProperty("connected").GetBoolean());
        Assert.Equal("final ...cdef", s.GetProperty("tokenHint").GetString());
        Assert.DoesNotContain("token-de-teste", s.GetRawText());   // o token nunca volta
        var (_, op, _) = await api.CreateUser(t, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Get("/api/v1/whatsapp/settings", op)).StatusCode);

        var p = await api.CreateProduct(t.OwnerToken, "CAN", price: 2490);
        await api.StockEntry(t.OwnerToken, t.StoreId, p, 20);
        var maria = await Customer(t, "Maria Silva", "(47) 99284-1812", consent: false, limit: 20000);
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, p, qty: 2) with { Payments = [new SalePaymentRequest(5, 4980)], CustomerId = maria }), HttpStatusCode.Created);

        var preview = await Api.Expect(await api.Get($"/api/v1/customers/{maria}/charge-preview", t.OwnerToken), HttpStatusCode.OK);
        Assert.True(preview.GetProperty("canSend").GetBoolean());
        Assert.Equal(4980, preview.GetProperty("openCents").GetInt64());
        var text = preview.GetProperty("text").GetString()!;
        Assert.Contains("x2", text);
        Assert.Contains(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3)).ToString("dd/MM"), text);   // o que comprou e em que dia

        var before = f.Wa.Sent.Count;
        await Api.Expect(await api.Post($"/api/v1/customers/{maria}/charge", t.OwnerToken, new { }), HttpStatusCode.OK);
        var sent = f.Wa.Sent.Last();
        Assert.Equal(before + 1, f.Wa.Sent.Count);
        Assert.Equal("5547992841812", sent.To);
        Assert.Equal("cobranca_fiado", sent.Template);
        Assert.Equal(4, sent.Params.Count);
        Assert.Contains("49,80", sent.Params[2]);
        Assert.Contains("x2", sent.Params[3]);
        Assert.DoesNotContain(sent.Params, x => x.Contains('\n'));   // a Meta recusa quebra de linha nas variáveis
        Assert.Equal("charge_too_soon", await Api.Code(await api.Post($"/api/v1/customers/{maria}/charge", t.OwnerToken, new { })));

        // Dia de cobrança: a rotina cobra uma vez no mês, só quem deve.
        var today = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3)).Day;
        Assert.Equal("invalid_day", await Api.Code(await api.Put($"/api/v1/customers/{maria}/billing-day", t.OwnerToken, new { day = 40 })));
        var joao = await Customer(t, "João Souza", "47 98888-7777", consent: false, limit: 20000);
        var semDivida = await Customer(t, "Ana Lima", "47 97777-6666", consent: false, limit: 20000);
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, p) with { Payments = [new SalePaymentRequest(5, 2490)], CustomerId = joao }), HttpStatusCode.Created);
        foreach (var c in new[] { joao, semDivida })
            Assert.Equal(HttpStatusCode.NoContent, (await api.Put($"/api/v1/customers/{c}/billing-day", t.OwnerToken, new { day = today })).StatusCode);
        before = f.Wa.Sent.Count;
        await Job();
        Assert.Equal(before + 1, f.Wa.Sent.Count);
        Assert.Equal("5547988887777", f.Wa.Sent.Last().To);
        await Job();
        Assert.Equal(before + 1, f.Wa.Sent.Count);   // não repete no mesmo mês
        var detail = await Api.Expect(await api.Get($"/api/v1/customers/{joao}", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(today, detail.GetProperty("customer").GetProperty("billingDay").GetInt32());

        var log = await Api.Expect(await api.Get("/api/v1/whatsapp/messages?kind=charge", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(2, log.GetArrayLength());
        Assert.DoesNotContain("99284", log.GetRawText());   // telefone mascarado na lista
    }

    [Fact]
    public async Task Promocao_so_para_quem_aceitou_com_limite_diario_e_descadastro_por_SAIR()
    {
        var (t, _) = await Store();
        Assert.Equal("promotions_disabled", await Api.Code(await api.Post("/api/v1/whatsapp/campaigns", t.OwnerToken,
            new { title = "Sábado", message = "Tudo com 10% de desconto neste sábado!", scheduledAt = DateTimeOffset.UtcNow })));
        Assert.Equal("invalid_interval", await Api.Code(await api.Put("/api/v1/whatsapp/settings", t.OwnerToken, Settings("EAAG-token-de-teste-1234567890abcdef", promos: true, interval: 1))));
        var s = await Api.Expect(await api.Put("/api/v1/whatsapp/settings", t.OwnerToken,
            Settings("EAAG-token-de-teste-1234567890abcdef", promos: true, limit: 2, secret: AppSecret)), HttpStatusCode.OK);
        var verify = s.GetProperty("webhookVerifyToken").GetString()!;

        var a = await Customer(t, "Ana", "47 91111-1111", consent: true);
        await Customer(t, "Bruno", "47 92222-2222", consent: true);
        await Customer(t, "Carla", "47 93333-3333", consent: true);
        await Customer(t, "Davi", "47 94444-4444", consent: false);   // não aceitou ofertas
        await Customer(t, "Eva", null, consent: true);                 // sem celular

        Assert.Equal("too_many_links", await Api.Code(await api.Post("/api/v1/whatsapp/campaigns", t.OwnerToken,
            new { title = "Links", message = "Veja https://a.test e https://b.test agora", scheduledAt = DateTimeOffset.UtcNow })));
        var camp = (await Api.Expect(await api.Post("/api/v1/whatsapp/campaigns", t.OwnerToken,
            new { title = "Sábado", message = "Tudo com 10% de desconto neste sábado!", scheduledAt = DateTimeOffset.UtcNow }), HttpStatusCode.OK)).GetProperty("id").GetGuid();
        Assert.Equal("campaign_same_day", await Api.Code(await api.Post("/api/v1/whatsapp/campaigns", t.OwnerToken,
            new { title = "Outra", message = "Mais uma promoção no mesmo dia", scheduledAt = DateTimeOffset.UtcNow.AddMinutes(30) })));

        var summary = await Api.Expect(await api.Get("/api/v1/whatsapp/campaigns", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(3, summary.GetProperty("eligibleNow").GetInt32());

        var before = f.Wa.Sent.Count;
        await Job();
        var promos = f.Wa.Sent.Skip(before).ToList();
        Assert.Equal(2, promos.Count);                                // teto diário
        Assert.All(promos, x => Assert.Equal("promocao_loja", x.Template));
        Assert.DoesNotContain(promos, x => x.To == "5547944444444");  // sem consentimento nunca recebe
        await Job();
        Assert.Equal(before + 2, f.Wa.Sent.Count);                    // limite do dia já usado
        summary = await Api.Expect(await api.Get("/api/v1/whatsapp/campaigns", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(2, summary.GetProperty("sentToday").GetInt32());
        Assert.Equal(1, summary.GetProperty("eligibleNow").GetInt32());   // quem recebeu espera o intervalo
        Assert.Equal("sending", summary.GetProperty("campaigns")[0].GetProperty("status").GetString());

        // Cliente responde SAIR: sai da lista de ofertas (assinatura da Meta conferida).
        var http = f.CreateClient();
        Assert.Equal(verify, await (await http.GetAsync($"/webhooks/whatsapp/{t.Id}?hub.mode=subscribe&hub.verify_token={verify}&hub.challenge={verify}")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/webhooks/whatsapp/{t.Id}?hub.mode=subscribe&hub.verify_token=errado&hub.challenge=x")).StatusCode);
        var body = """{"entry":[{"changes":[{"value":{"messages":[{"from":"5547911111111","type":"text","text":{"body":"sair"}}]}}]}]}""";
        async Task<HttpStatusCode> Post(string sig)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/whatsapp/{t.Id}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Hub-Signature-256", sig);
            return (await http.SendAsync(req)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await Post("sha256=" + new string('0', 64)));
        Assert.Equal(HttpStatusCode.OK, await Post("sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppSecret), Encoding.UTF8.GetBytes(body)))));
        Assert.False(await f.WithDb(db => db.Customers.IgnoreQueryFilters().Where(c => c.Id == a).Select(c => c.MarketingConsent).FirstAsync()));

        Assert.Equal(HttpStatusCode.NoContent, (await api.Post($"/api/v1/whatsapp/campaigns/{camp}/cancel", t.OwnerToken, new { })).StatusCode);
        summary = await Api.Expect(await api.Get("/api/v1/whatsapp/campaigns", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal("cancelled", summary.GetProperty("campaigns")[0].GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("(47) 99284-1812", "5547992841812")]
    [InlineData("4733334444", "554733334444")]
    [InlineData("+55 47 99284-1812", "5547992841812")]
    [InlineData("12345", null)]
    [InlineData("", null)]
    public void Celular_no_formato_internacional(string input, string? expected) => Assert.Equal(expected, WhatsAppService.NormalizePhone(input));
}

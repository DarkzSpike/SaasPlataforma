using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Comercial.Api.Services;
using Comercial.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Comercial.Tests;

/// <summary>Mercado Pago falso: guarda os pagamentos criados e permite simular a aprovação.</summary>
public sealed class FakeMercadoPago : IMercadoPagoClient
{
    public ConcurrentDictionary<string, MpPayment> Payments { get; } = new();
    public ConcurrentBag<string> TokensUsed { get; } = [];

    public Task<MpPixResult> CreatePixAsync(string token, MpPixRequest r, CancellationToken ct)
    {
        TokensUsed.Add(token);
        var id = Random.Shared.NextInt64(10_000_000, 99_999_999).ToString();
        Payments[id] = new MpPayment(id, "pending", r.ExternalReference, r.AmountCents, "BRL", "bank_transfer");
        return Task.FromResult(new MpPixResult(id, "00020126pix-copia-e-cola-" + id, Convert.ToBase64String(TestImages.Png(40, 40)), null));
    }
    public Task<MpPreferenceResult> CreatePreferenceAsync(string token, MpPreferenceRequest r, CancellationToken ct) =>
        Task.FromResult(new MpPreferenceResult("pref-" + Guid.NewGuid().ToString("N")[..8], "https://www.mercadopago.com.br/checkout/v1/redirect?pref_id=abc"));
    public Task<MpPayment?> GetPaymentAsync(string token, string id, CancellationToken ct) => Task.FromResult(Payments.TryGetValue(id, out var p) ? p : null);
    public Task<IReadOnlyList<MpPayment>> SearchByReferenceAsync(string token, string reference, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MpPayment>>(Payments.Values.Where(p => p.ExternalReference == reference).ToList());
    public Task<(bool Ok, string Message)> CheckAsync(string token, CancellationToken ct) => Task.FromResult(token.Contains("RECUSADO") ? (false, "token inválido") : (true, "Conta conectada: TESTE"));
    public Task CancelPaymentAsync(string token, string id, CancellationToken ct)
    {
        if (Payments.TryGetValue(id, out var p)) Payments[id] = p with { Status = "cancelled" };
        return Task.CompletedTask;
    }

    public void Approve(string id, long? amount = null) => Payments[id] = Payments[id] with { Status = "approved", AmountCents = amount ?? Payments[id].AmountCents };
}

public class BillingFactory : ApiFactory
{
    public FakeMercadoPago Mp { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        base.ConfigureWebHost(b);
        b.ConfigureTestServices(s => s.AddSingleton<IMercadoPagoClient>(Mp));
    }
}

public class BillingTests(BillingFactory f) : IClassFixture<BillingFactory>, IAsyncLifetime
{
    private readonly Api api = new(f);
    private const string Secret = "segredo-do-webhook-mercado-pago-123";
    private string admin = "";

    public async Task InitializeAsync()
    {
        admin = await api.AdminToken();
        await Api.Expect(await api.Put("/admin/v1/settings/mercadopago", admin,
            new { accessToken = "TEST-1234567890-abcdef-token-de-teste", publicKey = "TEST-pk", webhookSecret = Secret, notificationBaseUrl = "" }, api.Reauth()), HttpStatusCode.OK);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<TenantInfo> TenantWithPeriodEndingIn(double days)
    {
        var t = await api.CreateTenant("fat-" + Guid.NewGuid().ToString("N")[..6]);
        await f.WithDb(async db =>
        {
            var sub = await db.Subscriptions.IgnoreQueryFilters().FirstAsync(s => s.TenantId == t.Id);
            sub.CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(days);
            await db.SaveChangesAsync();
        });
        return t;
    }

    private async Task<Invoice> GenerateFor(TenantInfo t)
    {
        await Api.Expect(await api.Post("/admin/v1/billing/run", admin, new { }), HttpStatusCode.OK);
        return await f.WithDb(db => db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.TenantId == t.Id));
    }

    private async Task<HttpResponseMessage> Webhook(string paymentId, string? secret = Secret, long? ts = null)
    {
        var stamp = (ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        var requestId = Guid.NewGuid().ToString();
        var manifest = $"id:{paymentId};request-id:{requestId};ts:{stamp};";
        var v1 = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret ?? "x"), Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/mercadopago?data.id={paymentId}&type=payment")
        {
            Content = new StringContent($"{{\"type\":\"payment\",\"data\":{{\"id\":\"{paymentId}\"}}}}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-signature", $"ts={stamp},v1={v1}");
        req.Headers.Add("x-request-id", requestId);
        return await api.Http.SendAsync(req);
    }

    [Fact]
    public async Task Fatura_e_gerada_automaticamente_uma_vez_e_o_dono_e_avisado()
    {
        var t = await TenantWithPeriodEndingIn(5);
        var far = await TenantWithPeriodEndingIn(40); // ainda longe do fim do período
        var inv = await GenerateFor(t);
        Assert.Equal("auto", inv.Origin);
        Assert.Equal(InvoiceStatus.Open, inv.Status);
        var sub = await f.WithDb(db => db.Subscriptions.IgnoreQueryFilters().FirstAsync(s => s.TenantId == t.Id));
        Assert.Equal(sub.CurrentPeriodEnd, inv.PeriodStart);
        Assert.Equal(sub.PriceCents, inv.AmountCents);

        await Api.Expect(await api.Post("/admin/v1/billing/run", admin, new { }), HttpStatusCode.OK);
        Assert.Equal(1, await f.WithDb(db => db.Invoices.IgnoreQueryFilters().CountAsync(i => i.TenantId == t.Id)));     // não duplica
        Assert.Equal(0, await f.WithDb(db => db.Invoices.IgnoreQueryFilters().CountAsync(i => i.TenantId == far.Id)));
        // Gerar manualmente pelo painel devolve a mesma fatura do período.
        var manual = await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/invoices", admin, new { dueInDays = 3 }), HttpStatusCode.OK);
        Assert.Equal(inv.Id, manual.GetProperty("id").GetGuid());

        var notes = await Api.Expect(await api.Get("/api/v1/notifications", t.OwnerToken), HttpStatusCode.OK);
        Assert.Contains(notes.EnumerateArray(), n => n.GetProperty("type").GetString() == "invoice.available");
        var list = await Api.Expect(await api.Get("/api/v1/billing/invoices", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal("aberta", Assert.Single(list.EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Lembrete_e_painel_mostram_faturas_que_vencem_em_3_dias()
    {
        var t = await TenantWithPeriodEndingIn(2);
        var inv = await GenerateFor(t);
        var upcoming = await Api.Expect(await api.Get("/admin/v1/invoices/upcoming?days=3", admin), HttpStatusCode.OK);
        Assert.Contains(upcoming.EnumerateArray(), r => r.GetProperty("id").GetGuid() == inv.Id && r.GetProperty("daysToDue").GetInt32() <= 3);
        var notes = await Api.Expect(await api.Get("/api/v1/notifications", t.OwnerToken), HttpStatusCode.OK);
        Assert.Single(notes.EnumerateArray(), n => n.GetProperty("type").GetString() == "invoice.due_soon");
        await Api.Expect(await api.Post("/admin/v1/billing/run", admin, new { }), HttpStatusCode.OK);
        notes = await Api.Expect(await api.Get("/api/v1/notifications", t.OwnerToken), HttpStatusCode.OK);
        Assert.Single(notes.EnumerateArray(), n => n.GetProperty("type").GetString() == "invoice.due_soon");           // lembrete só uma vez
        // Token de loja não acessa o painel de faturas.
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/admin/v1/invoices/upcoming", t.OwnerToken)).StatusCode);
    }

    [Fact]
    public async Task Pix_pago_confirmado_pelo_webhook_assinado_reativa_a_licenca()
    {
        var t = await TenantWithPeriodEndingIn(30);
        var (_, op, _) = await api.CreateUser(t, "operator");
        await f.WithDb(async db =>
        {
            var sub = await db.Subscriptions.IgnoreQueryFilters().FirstAsync(s => s.TenantId == t.Id);
            sub.CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(-20); // licença restrita por falta de pagamento
            await db.SaveChangesAsync();
        });
        var inv = await GenerateFor(t);
        Assert.Equal("restricted", (await Api.Expect(await api.Get("/api/v1/me", t.OwnerToken), HttpStatusCode.OK)).GetProperty("license").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post($"/api/v1/billing/invoices/{inv.Id}/pix", op, new { })).StatusCode);

        var pix = await Api.Expect(await api.Post($"/api/v1/billing/invoices/{inv.Id}/pix", t.OwnerToken, new { }), HttpStatusCode.OK); // pagar continua liberado
        Assert.StartsWith("00020126", pix.GetProperty("pixCopyPaste").GetString());
        Assert.False(string.IsNullOrEmpty(pix.GetProperty("pixQrBase64").GetString()));
        var again = await Api.Expect(await api.Post($"/api/v1/billing/invoices/{inv.Id}/pix", t.OwnerToken, new { }), HttpStatusCode.OK);
        Assert.Equal(pix.GetProperty("pixCopyPaste").GetString(), again.GetProperty("pixCopyPaste").GetString()); // reaproveita o PIX válido
        Assert.All(f.Mp.TokensUsed, tk => Assert.Equal("TEST-1234567890-abcdef-token-de-teste", tk));

        var paymentId = (await f.WithDb(db => db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == inv.Id))).MpPaymentId!;
        Assert.Equal("status_pending", (await Api.Expect(await Webhook(paymentId), HttpStatusCode.OK)).GetProperty("result").GetString());
        f.Mp.Approve(paymentId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Webhook(paymentId, secret: "assinatura-falsa")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Webhook(paymentId, ts: DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds())).StatusCode);
        Assert.Equal("restricted", (await Api.Expect(await api.Get("/api/v1/me", t.OwnerToken), HttpStatusCode.OK)).GetProperty("license").GetString());

        Assert.Equal("applied", (await Api.Expect(await Webhook(paymentId), HttpStatusCode.OK)).GetProperty("result").GetString());
        Assert.Equal("duplicate", (await Api.Expect(await Webhook(paymentId), HttpStatusCode.OK)).GetProperty("result").GetString());
        var paid = await f.WithDb(db => db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == inv.Id));
        Assert.Equal(InvoiceStatus.Paid, paid.Status);
        Assert.Equal("pix", paid.PaidMethod);
        Assert.Equal("active", (await Api.Expect(await api.Get("/api/v1/me", t.OwnerToken), HttpStatusCode.OK)).GetProperty("license").GetString());
        var notes = await Api.Expect(await api.Get("/api/v1/notifications", t.OwnerToken), HttpStatusCode.OK);
        Assert.Contains(notes.EnumerateArray(), n => n.GetProperty("type").GetString() == "invoice.paid");
    }

    [Fact]
    public async Task Valor_diferente_nao_baixa_fatura_e_ja_paguei_consulta_o_mercado_pago()
    {
        var t = await TenantWithPeriodEndingIn(4);
        var inv = await GenerateFor(t);
        await Api.Expect(await api.Post($"/api/v1/billing/invoices/{inv.Id}/pix", t.OwnerToken, new { }), HttpStatusCode.OK);
        var paymentId = (await f.WithDb(db => db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == inv.Id))).MpPaymentId!;

        f.Mp.Approve(paymentId, amount: 1);
        Assert.Equal("amount_mismatch", (await Api.Expect(await Webhook(paymentId), HttpStatusCode.OK)).GetProperty("result").GetString());
        Assert.Equal(InvoiceStatus.Open, (await f.WithDb(db => db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == inv.Id))).Status);

        // Pagamento correto aparece no Mercado Pago, mas o aviso não chegou: o cliente clica em "Já paguei".
        var id2 = paymentId + "9";
        f.Mp.Payments[id2] = new MpPayment(id2, "approved", inv.Id.ToString(), inv.AmountCents, "BRL", "credit_card");
        var r = await Api.Expect(await api.Post($"/api/v1/billing/invoices/{inv.Id}/refresh", t.OwnerToken, new { }), HttpStatusCode.OK);
        Assert.Equal("paga", r.GetProperty("status").GetString());
        Assert.Equal("cartão de crédito", r.GetProperty("paidMethod").GetString());
        // Outra empresa não enxerga nem paga esta fatura.
        var other = await api.CreateTenant("fat-x" + Guid.NewGuid().ToString("N")[..5]);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post($"/api/v1/billing/invoices/{inv.Id}/checkout", other.OwnerToken, new { })).StatusCode);
    }

    [Fact]
    public async Task Checkout_de_cartao_e_boleto_gera_link_do_mercado_pago()
    {
        var t = await TenantWithPeriodEndingIn(6);
        var inv = await GenerateFor(t);
        var r = await Api.Expect(await api.Post($"/api/v1/billing/invoices/{inv.Id}/checkout", t.OwnerToken, new { }), HttpStatusCode.OK);
        Assert.StartsWith("https://www.mercadopago.com.br/", r.GetProperty("checkoutUrl").GetString());
    }

    [Fact]
    public async Task Credenciais_do_mercado_pago_ficam_protegidas()
    {
        var view = await Api.Expect(await api.Get("/admin/v1/settings/mercadopago", admin), HttpStatusCode.OK);
        Assert.True(view.GetProperty("configured").GetBoolean());
        Assert.True(view.GetProperty("isTestToken").GetBoolean());
        Assert.DoesNotContain("1234567890-abcdef", view.ToString());
        Assert.DoesNotContain(Secret, view.ToString());
        var stored = await f.WithDb(db => db.PlatformSettings.SingleAsync(s => s.Key == PlatformSettingsService.MpAccessToken));
        Assert.DoesNotContain("abcdef", stored.Value);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Put("/admin/v1/settings/mercadopago", admin, new { accessToken = "TEST-outro-token-qualquer" })).StatusCode); // sem MFA
        Assert.Equal("invalid_token", await Api.Code(await api.Put("/admin/v1/settings/mercadopago", admin, new { accessToken = "token-invalido" }, api.Reauth())));
        Assert.Equal("invalid_url", await Api.Code(await api.Put("/admin/v1/settings/mercadopago", admin, new { notificationBaseUrl = "http://localhost:5080" }, api.Reauth())));
    }

    [Fact]
    public async Task Baixa_manual_exige_mfa_confirmacao_e_motivo()
    {
        var t = await TenantWithPeriodEndingIn(3);
        var inv = await GenerateFor(t);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Post($"/admin/v1/invoices/{inv.Id}/mark-paid", admin, new { confirmTenantCode = t.Code, reason = "pago por transferência" })).StatusCode);
        Assert.Equal("confirmation_mismatch", await Api.Code(await api.Post($"/admin/v1/invoices/{inv.Id}/mark-paid", admin, new { confirmTenantCode = "outra", reason = "pago por transferência" }, api.Reauth())));
        await Api.Expect(await api.Post($"/admin/v1/invoices/{inv.Id}/mark-paid", admin, new { confirmTenantCode = t.Code, reason = "pago por transferência" }, api.Reauth()), HttpStatusCode.OK);
        Assert.Equal("manual", (await f.WithDb(db => db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == inv.Id))).PaidMethod);
    }
}

/// <summary>Suporte rápido: com autorização prévia do dono + MFA, o técnico altera a loja sem pedir a cada vez.</summary>
public class QuickSupportTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private readonly Api api = new(f);

    [Fact]
    public async Task Pre_autorizacao_do_dono_libera_so_o_que_ele_marcou()
    {
        var t = await api.CreateTenant("sup-" + Guid.NewGuid().ToString("N")[..6]);
        var product = await api.CreateProduct(t.OwnerToken, "SP1", price: 1000);
        var admin = await api.AdminToken();
        Assert.Equal("operation_not_elevatable", await Api.Code(await api.Put("/api/v1/support/preauthorization", t.OwnerToken, new { operations = new[] { "sales.cancel" } })));
        await Api.Expect(await api.Put("/api/v1/support/preauthorization", t.OwnerToken, new { operations = new[] { "products.write" } }), HttpStatusCode.OK);

        var s = await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/support-sessions", admin,
            new { reason = "Corrigir preços importados errado", durationMinutes = 30, totpCode = Api.TotpNow() }), HttpStatusCode.OK);
        Assert.Equal(2, s.GetProperty("mode").GetInt32()); // já começa podendo alterar
        var h = new Dictionary<string, string> { ["X-Support-Session"] = s.GetProperty("id").GetString()! };

        var p = await Api.Expect(await api.Get($"/api/v1/products/{product}", admin, h), HttpStatusCode.OK);
        await Api.Expect(await api.Put($"/api/v1/products/{product}", admin, new
        {
            sku = p.GetProperty("sku").GetString(), barcode = (string?)null, name = "Produto corrigido pelo suporte", description = (string?)null, unit = "UN",
            priceCents = 1290L, costCents = 500L, minStock = 0m, categoryId = (Guid?)null, expectedVersion = p.GetProperty("version").GetInt32(),
        }, h), HttpStatusCode.OK);
        Assert.Equal("support_scope", await Api.Code(await api.Get("/api/v1/users", admin, h)));                 // usuários não foram autorizados
        Assert.Equal("support_scope", await Api.Code(await api.Post("/api/v1/sales", admin, new { }, h)));      // vendas nunca

        var notes = await Api.Expect(await api.Get("/api/v1/notifications", t.OwnerToken), HttpStatusCode.OK);
        Assert.Contains(notes.EnumerateArray(), n => n.GetProperty("type").GetString() == "support.session_started");
        Assert.True(await f.WithDb(db => db.AuditLogs.AnyAsync(a => a.TenantId == t.Id && a.Action == "product.updated" && a.SupportSessionId != null)));
    }

    [Fact]
    public async Task Sem_pre_autorizacao_ou_sem_mfa_a_sessao_e_somente_consulta()
    {
        var t = await api.CreateTenant("sup2-" + Guid.NewGuid().ToString("N")[..5]);
        var admin = await api.AdminToken();
        var s = await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/support-sessions", admin, new { reason = "Verificar estoque da loja", durationMinutes = 30, totpCode = Api.TotpNow() }), HttpStatusCode.OK);
        Assert.Equal(1, s.GetProperty("mode").GetInt32());

        await Api.Expect(await api.Put("/api/v1/support/preauthorization", t.OwnerToken, new { operations = new[] { "products.write" } }), HttpStatusCode.OK);
        var semMfa = await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/support-sessions", admin, new { reason = "Verificar estoque da loja", durationMinutes = 30 }), HttpStatusCode.OK);
        Assert.Equal(1, semMfa.GetProperty("mode").GetInt32());
        Assert.Equal("reauth_required", await Api.Code(await api.Post($"/admin/v1/tenants/{t.Id}/support-sessions", admin, new { reason = "Verificar estoque da loja", durationMinutes = 30, totpCode = "000000" })));

        // Pedido de liberação avisa o dono.
        await Api.Expect(await api.Post($"/admin/v1/support-sessions/{semMfa.GetProperty("id").GetString()}/elevation-requests", admin,
            new { reason = "Cliente pediu para desbloquear usuário", operations = new[] { "users.manage" }, durationMinutes = 15, totpCode = Api.TotpNow() }), HttpStatusCode.OK);
        var notes = await Api.Expect(await api.Get("/api/v1/notifications", t.OwnerToken), HttpStatusCode.OK);
        Assert.Contains(notes.EnumerateArray(), n => n.GetProperty("type").GetString() == "support.elevation_requested");
    }
}

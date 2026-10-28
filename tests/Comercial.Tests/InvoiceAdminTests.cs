using System.Net;
using Comercial.Api.Services;
using Comercial.Domain;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Tests;

/// <summary>Painel: gerar fatura agora (com data e valor), editar, cancelar e mudar o dia de cobrança.</summary>
public class InvoiceAdminTests(BillingFactory f) : IClassFixture<BillingFactory>, IAsyncLifetime
{
    private readonly Api api = new(f);
    private string admin = "";
    private static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3)).DateTime);

    public async Task InitializeAsync()
    {
        admin = await api.AdminToken();
        await Api.Expect(await api.Put("/admin/v1/settings/mercadopago", admin,
            new { accessToken = "TEST-1234567890-abcdef-token-de-teste", publicKey = "TEST-pk", webhookSecret = "segredo-do-webhook-123", notificationBaseUrl = "" }, api.Reauth()), HttpStatusCode.OK);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Gera_agora_com_data_e_valor_avisa_o_cliente_e_nao_duplica()
    {
        var t = await api.CreateTenant("ga-" + Guid.NewGuid().ToString("N")[..6]);   // período termina daqui a 1 mês: o automático ainda não geraria
        var due = Today.AddDays(2);
        var inv = await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/invoices", admin, new { dueDate = due, amountCents = 1990 }), HttpStatusCode.OK);
        Assert.False(inv.GetProperty("alreadyExisted").GetBoolean());
        Assert.Equal(1990, inv.GetProperty("amountCents").GetInt64());
        Assert.Equal(BillingService.DueAtOf(due), inv.GetProperty("dueAt").GetDateTimeOffset());
        var notes = await Api.Expect(await api.Get("/api/v1/notifications", t.OwnerToken), HttpStatusCode.OK);
        Assert.Contains(notes.EnumerateArray(), n => n.GetProperty("type").GetString() == "invoice.available"
            && n.GetProperty("invoiceId").GetGuid() == inv.GetProperty("id").GetGuid());   // o caixa mostra "Pagar agora" nesse aviso
        // O cliente vê a fatura no menu Faturas.
        var list = await Api.Expect(await api.Get("/api/v1/billing/invoices", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(1990, Assert.Single(list.EnumerateArray()).GetProperty("amountCents").GetInt64());

        var again = await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/invoices", admin, new { dueDate = due }), HttpStatusCode.OK);
        Assert.True(again.GetProperty("alreadyExisted").GetBoolean());
        Assert.Equal(inv.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid());
        Assert.Equal("invalid_due_date", await Api.Code(await api.Post($"/admin/v1/tenants/{Guid.NewGuid()}/invoices", admin, new { dueDate = Today.AddDays(-1) })));
    }

    [Fact]
    public async Task Editar_fatura_invalida_o_pix_antigo_e_pagamento_antigo_nao_baixa()
    {
        var t = await api.CreateTenant("ed-" + Guid.NewGuid().ToString("N")[..6]);
        var inv = await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/invoices", admin, new { dueInDays = 3 }), HttpStatusCode.OK);
        var id = inv.GetProperty("id").GetGuid();
        var pix = await Api.Expect(await api.Post($"/api/v1/billing/invoices/{id}/pix", t.OwnerToken, new { }), HttpStatusCode.OK);
        var oldPayment = await f.WithDb(db => db.Invoices.IgnoreQueryFilters().Where(i => i.Id == id).Select(i => i.MpPaymentId).SingleAsync());
        Assert.NotNull(pix.GetProperty("pixCopyPaste").GetString());

        // Proteções: código da empresa, motivo e código do autenticador.
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Put($"/admin/v1/invoices/{id}", admin, new { amountCents = 5000, confirmTenantCode = t.Code, reason = "desconto combinado" })).StatusCode);
        Assert.Equal("confirmation_mismatch", await Api.Code(await api.Put($"/admin/v1/invoices/{id}", admin, new { amountCents = 5000, confirmTenantCode = "outra", reason = "desconto combinado" }, api.Reauth())));
        Assert.Equal("reason_required", await Api.Code(await api.Put($"/admin/v1/invoices/{id}", admin, new { amountCents = 5000, confirmTenantCode = t.Code, reason = "" }, api.Reauth())));

        var due = Today.AddDays(15);
        var edited = await Api.Expect(await api.Put($"/admin/v1/invoices/{id}", admin, new { amountCents = 5000, dueDate = due, confirmTenantCode = t.Code, reason = "desconto combinado com o cliente" }, api.Reauth()), HttpStatusCode.OK);
        Assert.Equal(5000, edited.GetProperty("amountCents").GetInt64());
        Assert.Equal(BillingService.DueAtOf(due), edited.GetProperty("dueAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKindNull, edited.GetProperty("pixCopyPaste").ValueKind);   // PIX antigo sumiu da fatura
        Assert.Equal("cancelled", f.Mp.Payments[oldPayment!].Status);                    // e foi cancelado no Mercado Pago

        // Se o PIX antigo (valor antigo) ainda for pago, a fatura NÃO baixa: o valor é conferido.
        f.Mp.Approve(oldPayment!);
        await Api.Expect(await api.Post($"/api/v1/billing/invoices/{id}/refresh", t.OwnerToken, new { }), HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.Open, await f.WithDb(db => db.Invoices.IgnoreQueryFilters().Where(i => i.Id == id).Select(i => i.Status).SingleAsync()));

        // Cancelar a fatura.
        await Api.Expect(await api.Post($"/admin/v1/invoices/{id}/cancel", admin, new { confirmTenantCode = t.Code, reason = "cobrança em duplicidade" }, api.Reauth()), HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.Void, await f.WithDb(db => db.Invoices.IgnoreQueryFilters().Where(i => i.Id == id).Select(i => i.Status).SingleAsync()));
        Assert.Equal("invoice_not_open", await Api.Code(await api.Put($"/admin/v1/invoices/{id}", admin, new { amountCents = 1, confirmTenantCode = t.Code, reason = "teste de novo" }, api.Reauth())));
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task Mudar_dia_de_cobranca_move_o_vencimento_e_a_fatura_aberta()
    {
        var t = await api.CreateTenant("dc-" + Guid.NewGuid().ToString("N")[..6]);
        var inv = await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/invoices", admin, new { dueInDays = 3 }), HttpStatusCode.OK);
        var newDate = Today.AddDays(40);
        var r = await Api.Expect(await api.Put($"/admin/v1/tenants/{t.Id}/billing-date", admin,
            new { nextDueDate = newDate, confirmTenantCode = t.Code, reason = "cliente pediu para pagar todo dia " + newDate.Day }, api.Reauth()), HttpStatusCode.OK);
        Assert.True(r.GetProperty("invoiceMoved").GetBoolean());
        var sub = await f.WithDb(db => db.Subscriptions.IgnoreQueryFilters().SingleAsync(s => s.TenantId == t.Id));
        Assert.Equal(BillingService.DueAtOf(newDate), sub.CurrentPeriodEnd);
        var moved = await f.WithDb(db => db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == inv.GetProperty("id").GetGuid()));
        Assert.Equal(BillingService.DueAtOf(newDate), moved.DueAt);
        Assert.Equal(BillingService.DueAtOf(newDate).AddMonths(1), moved.PeriodEnd);   // próximo período segue o mesmo dia

        Assert.Equal("invalid_due_date", await Api.Code(await api.Put($"/admin/v1/tenants/{t.Id}/billing-date", admin,
            new { nextDueDate = Today.AddDays(-3), confirmTenantCode = t.Code, reason = "data no passado" }, api.Reauth())));
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Put($"/admin/v1/tenants/{t.Id}/billing-date", admin,
            new { nextDueDate = newDate, confirmTenantCode = t.Code, reason = "sem o código do autenticador" })).StatusCode);
    }
}

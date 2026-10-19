using System.Net;
using Comercial.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Tests;

/// <summary>PIX dinâmico no caixa, na conta Mercado Pago da própria loja.</summary>
public class StorePixTests(BillingFactory f) : IClassFixture<BillingFactory>
{
    private readonly Api api = new(f);

    [Fact]
    public async Task Venda_so_aceita_pix_confirmado_pelo_mercado_pago_e_uma_vez()
    {
        var t = await api.CreateTenant("pix-" + Guid.NewGuid().ToString("N")[..6]);
        var p = await api.CreateProduct(t.OwnerToken, "PX1", price: 2500);
        await api.StockEntry(t.OwnerToken, t.StoreId, p, 10);
        var (_, op, _) = await api.CreateUser(t, "operator");
        var cash = await api.OpenCash(op, t.StoreId);

        Assert.Equal("pix_not_configured", await Api.Code(await api.Post("/api/v1/pos/pix-charges", op, new { cashSessionId = cash, amountCents = 2500 })));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Put("/api/v1/payments/settings", op, new { mpAccessToken = "TEST-loja-123", pixEnabled = true })).StatusCode); // só o dono
        Assert.Equal("invalid_token", await Api.Code(await api.Put("/api/v1/payments/settings", t.OwnerToken, new { mpAccessToken = "TEST-RECUSADO-1", pixEnabled = true })));
        var cfg = await Api.Expect(await api.Put("/api/v1/payments/settings", t.OwnerToken, new { mpAccessToken = "TEST-token-da-loja-9876", pixEnabled = true }), HttpStatusCode.OK);
        Assert.True(cfg.GetProperty("pixEnabled").GetBoolean());
        Assert.DoesNotContain("token-da-loja", cfg.ToString());

        var charge = await Api.Expect(await api.Post("/api/v1/pos/pix-charges", op, new { cashSessionId = cash, amountCents = 2500 }), HttpStatusCode.OK);
        var id = charge.GetProperty("id").GetGuid();
        Assert.StartsWith("00020126", charge.GetProperty("qrCode").GetString());
        Assert.Contains("TEST-token-da-loja-9876", f.Mp.TokensUsed); // cobrança na conta DA LOJA

        var sale = Api.Sale(t.StoreId, cash, p, pay: 2500, method: 4) with { PixChargeId = id };
        Assert.Equal("pix_not_confirmed", await Api.Code(await api.Post("/api/v1/sales", op, sale)));      // ainda não pago
        Assert.Equal("pending", (await Api.Expect(await api.Get($"/api/v1/pos/pix-charges/{id}", op), HttpStatusCode.OK)).GetProperty("status").GetString());

        var mpId = await f.WithDb(db => db.PosPixCharges.IgnoreQueryFilters().Where(c => c.Id == id).Select(c => c.MpPaymentId).SingleAsync());
        f.Mp.Approve(mpId);
        Assert.Equal("approved", (await Api.Expect(await api.Get($"/api/v1/pos/pix-charges/{id}", op), HttpStatusCode.OK)).GetProperty("status").GetString());
        Assert.Equal("pix_not_confirmed", await Api.Code(await api.Post("/api/v1/sales", op, Api.Sale(t.StoreId, cash, p, pay: 2000, method: 4) with { PixChargeId = id }))); // valor diferente
        await Api.Expect(await api.Post("/api/v1/sales", op, sale), HttpStatusCode.Created);
        Assert.Equal("pix_not_confirmed", await Api.Code(await api.Post("/api/v1/sales", op, sale with { IdempotencyKey = Guid.NewGuid().ToString("N") }))); // não reaproveita

        // Cancelar uma cobrança pendente.
        var c2 = (await Api.Expect(await api.Post("/api/v1/pos/pix-charges", op, new { cashSessionId = cash, amountCents = 1000 }), HttpStatusCode.OK)).GetProperty("id").GetGuid();
        await Api.Expect(await api.Post($"/api/v1/pos/pix-charges/{c2}/cancel", op), HttpStatusCode.NoContent);
        Assert.Equal("cancelled", (await Api.Expect(await api.Get($"/api/v1/pos/pix-charges/{c2}", op), HttpStatusCode.OK)).GetProperty("status").GetString());

        // Outra empresa não consulta a cobrança.
        var other = await api.CreateTenant("pxo-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/pos/pix-charges/{id}", other.OwnerToken)).StatusCode);
    }
}

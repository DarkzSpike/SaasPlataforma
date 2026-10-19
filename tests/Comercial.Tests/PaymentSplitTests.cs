using System.Net;
using Comercial.Desktop.Core;
using Comercial.Shared.Contracts;

namespace Comercial.Tests;

public class PaymentSplitTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private readonly Api api = new(f);

    [Fact]
    public void Regras_de_divisao_do_pagamento()
    {
        Assert.True(PaymentSplit.Validate(10000, [(4, 5000), (1, 5000)]).Ok);                    // PIX + dinheiro exato
        var troco = PaymentSplit.Validate(10000, [(4, 5000), (1, 6000)]);
        Assert.True(troco.Ok); Assert.Equal(1000, troco.ChangeCents);                             // troco só do dinheiro
        Assert.False(PaymentSplit.Validate(10000, [(3, 8000), (4, 3000)]).Ok);                   // cartão + PIX passando do total
        Assert.False(PaymentSplit.Validate(10000, [(1, 4000), (2, 5000)]).Ok);                   // falta receber
        Assert.False(PaymentSplit.Validate(10000, []).Ok);
        Assert.Equal(3000, PaymentSplit.Remaining(10000, [(4, 7000)]));
    }

    [Fact]
    public async Task Venda_com_pix_e_dinheiro_e_suprimento_entram_no_caixa_certo()
    {
        var t = await api.CreateTenant("spl-" + Guid.NewGuid().ToString("N")[..6]);
        var p = await api.CreateProduct(t.OwnerToken, "SPL", price: 10000);
        await api.StockEntry(t.OwnerToken, t.StoreId, p, 10);
        var cash = await api.OpenCash(t.OwnerToken, t.StoreId, 2000);
        var sale = Api.Sale(t.StoreId, cash, p) with { Payments = [new SalePaymentRequest(4, 6000), new SalePaymentRequest(1, 5000)] };
        var r = await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, sale), HttpStatusCode.Created);
        Assert.Equal(1000, r.GetProperty("changeCents").GetInt64());
        await Api.Expect(await api.Post($"/api/v1/cash/sessions/{cash}/supply", t.OwnerToken, new { amountCents = 3000, reason = "reforço de troco" }), HttpStatusCode.OK);
        var cur = await Api.Expect(await api.Get("/api/v1/cash/sessions/current", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(2000 + 4000 + 3000, cur.GetProperty("expectedCashCents").GetInt64()); // abertura + dinheiro líquido + suprimento
    }
}

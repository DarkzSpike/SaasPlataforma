using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace Comercial.Tests;

public class ReportsTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private readonly Api api = new(f);
    private static string Q(DateTimeOffset d) => Uri.EscapeDataString(d.ToString("O"));
    private static string Period => $"from={Q(DateTimeOffset.UtcNow.AddDays(-1))}&to={Q(DateTimeOffset.UtcNow.AddDays(1))}";

    private async Task<(TenantInfo T, Guid A, Guid B, Guid C, Guid Op)> Seed()
    {
        var t = await api.CreateTenant("rp-" + Guid.NewGuid().ToString("N")[..6]);
        var admin = await api.AdminToken();
        await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/modules/relatorios_premium/enable", admin, new { confirmTenantCode = t.Code, reason = "teste de relatórios" }, api.Reauth()), HttpStatusCode.OK);
        var a = await api.CreateProduct(t.OwnerToken, "RA", price: 10000, cost: 4000);
        var b = await api.CreateProduct(t.OwnerToken, "RB", price: 1000, cost: 500);
        var c = await api.CreateProduct(t.OwnerToken, "RC", price: 500, cost: 100);
        foreach (var p in new[] { a, b, c }) await api.StockEntry(t.OwnerToken, t.StoreId, p, 100);
        var (opId, op, _) = await api.CreateUser(t, "operator");
        await Api.Expect(await api.Put($"/api/v1/users/{opId}/commission", t.OwnerToken, new { percent = 5m }), HttpStatusCode.NoContent);
        var cash = await api.OpenCash(op, t.StoreId, 0);
        await Api.Expect(await api.Post("/api/v1/sales", op, Api.Sale(t.StoreId, cash, a, qty: 8, pay: 80000)), HttpStatusCode.Created);
        await Api.Expect(await api.Post("/api/v1/sales", op, Api.Sale(t.StoreId, cash, b, qty: 10, pay: 10000)), HttpStatusCode.Created);
        await Api.Expect(await api.Post("/api/v1/sales", op, Api.Sale(t.StoreId, cash, c, qty: 2, pay: 1000)), HttpStatusCode.Created);
        return (t, a, b, c, opId);
    }

    [Fact]
    public async Task Curva_abc_giro_vendedores_horarios_e_dre()
    {
        var (t, a, b, c, opId) = await Seed();
        var abc = await Api.Expect(await api.Get("/api/v1/reports/abc?" + Period, t.OwnerToken), HttpStatusCode.OK);
        var prods = abc.GetProperty("products").EnumerateArray().ToList();
        Assert.Equal(a, prods[0].GetProperty("productId").GetGuid());
        Assert.Equal("A", prods[0].GetProperty("class").GetString());                   // o maior faturamento é sempre A
        Assert.Equal("B", prods[1].GetProperty("class").GetString());                   // acumulado antes dele: 87,9%
        Assert.Equal("C", prods[^1].GetProperty("class").GetString());

        var turn = await Api.Expect(await api.Get($"/api/v1/reports/turnover?{Period}&storeId={t.StoreId}", t.OwnerToken), HttpStatusCode.OK);
        var ra = turn.GetProperty("products").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == a);
        Assert.Equal(8m, ra.GetProperty("soldQuantity").GetDecimal());
        Assert.Equal(92m, ra.GetProperty("stockQuantity").GetDecimal());

        var sellers = await Api.Expect(await api.Get("/api/v1/reports/sellers?" + Period, t.OwnerToken), HttpStatusCode.OK);
        var s = sellers.EnumerateArray().Single(x => x.GetProperty("userId").GetGuid() == opId);
        Assert.Equal(91000, s.GetProperty("revenueCents").GetInt64());
        Assert.Equal(4550, s.GetProperty("commissionCents").GetInt64());               // 5%

        var hours = await Api.Expect(await api.Get("/api/v1/reports/hours?" + Period, t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(3, hours.GetProperty("sales").GetInt32());
        Assert.Equal(91000 / 3, hours.GetProperty("averageTicketCents").GetInt64());

        var dre = await Api.Expect(await api.Get("/api/v1/reports/dre?" + Period, t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(91000, dre.GetProperty("netRevenueCents").GetInt64());
        Assert.Equal(8 * 4000 + 10 * 500 + 2 * 100, dre.GetProperty("costOfGoodsCents").GetInt64());
        Assert.Equal(91000 - 37200, dre.GetProperty("grossProfitCents").GetInt64());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Get("/api/v1/reports/dre?" + Period, await api.Login(t.Code, (await api.CreateUser(t, "operator")).Email, "Senha-Func-123"))).StatusCode);
    }

    [Fact]
    public async Task Pacote_do_contador_tem_os_arquivos_e_fica_na_auditoria()
    {
        var (t, _, _, _, _) = await Seed();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/accountant.zip?" + Period);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.OwnerToken);
        using var res = await api.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var zip = new ZipArchive(await res.Content.ReadAsStreamAsync());
        Assert.Equal(new[] { "LEIA-ME.txt", "contas_pagas_e_recebidas.csv", "nfce.csv", "produtos.csv", "vendas.csv" }, zip.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        using var sr = new StreamReader(zip.GetEntry("vendas.csv")!.Open());
        Assert.Equal(4, (await sr.ReadToEndAsync()).Split("\r\n").Length);             // cabeçalho + 3 vendas
    }
}

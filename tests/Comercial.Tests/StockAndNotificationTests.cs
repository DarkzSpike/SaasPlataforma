using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Comercial.Api.Services;
using Comercial.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Comercial.Tests;

/// <summary>Inventário, fotos de produto e avisos de quebra de caixa (sem provedor de push configurado).</summary>
public class StockAndNotificationTests(ApiFactory f) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly Api api = new(f);
    private TenantInfo T = null!;
    private Guid product;

    public async Task InitializeAsync()
    {
        T = await api.CreateTenant("inv-" + Guid.NewGuid().ToString("N")[..6]);
        product = await api.CreateProduct(T.OwnerToken, "I1", price: 1000, cost: 500);
        await api.StockEntry(T.OwnerToken, T.StoreId, product, 10);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Inventario_ajusta_para_o_contado_preserva_historico_e_exige_permissao()
    {
        var semEstoque = await api.CreateProduct(T.OwnerToken, "I2");
        var body = new { storeId = T.StoreId, reason = "contagem mensal", counts = new[] { new { productId = product, countedQuantity = 7m }, new { productId = semEstoque, countedQuantity = 5m } } };
        var (_, op, _) = await api.CreateUser(T, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post("/api/v1/stock/inventory", op, body)).StatusCode);
        Assert.Equal("reason_required", await Api.Code(await api.Post("/api/v1/stock/inventory", T.OwnerToken, body with { reason = "" })));

        var r = await Api.Expect(await api.Post("/api/v1/stock/inventory", T.OwnerToken, body), HttpStatusCode.OK);
        Assert.Equal(-3m, r[0].GetProperty("difference").GetDecimal());
        Assert.Equal(5m, r[1].GetProperty("difference").GetDecimal());
        Assert.Equal(7m, await api.Balance(T.OwnerToken, T.StoreId, product));
        Assert.Equal(5m, await api.Balance(T.OwnerToken, T.StoreId, semEstoque));
        var mov = await f.WithDb(db => db.StockMovements.IgnoreQueryFilters().Where(m => m.ProductId == product && m.Type == StockMovementType.Adjustment).ToListAsync());
        Assert.Single(mov);
        Assert.StartsWith("Inventário:", mov[0].Reason);
        Assert.Equal(2, await f.WithDb(db => db.StockMovements.IgnoreQueryFilters().CountAsync(m => m.ProductId == product))); // entrada original preservada
    }

    [Fact]
    public async Task Saldo_lista_produtos_ainda_sem_estoque()
    {
        var novo = await api.CreateProduct(T.OwnerToken, "NOVO");
        var rows = await Api.Expect(await api.Get($"/api/v1/stock/balances?storeId={T.StoreId}", T.OwnerToken), HttpStatusCode.OK);
        var row = rows.EnumerateArray().Single(x => x.GetProperty("productId").GetGuid() == novo);
        Assert.Equal(0m, row.GetProperty("quantity").GetDecimal());
        Assert.False(row.GetProperty("hasImage").GetBoolean());
    }

    private async Task<HttpResponseMessage> UploadImage(string token, Guid productId, byte[] bytes, string name = "foto.png")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", name);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/products/{productId}/image") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await api.Http.SendAsync(req);
    }

    [Fact]
    public async Task Foto_do_produto_valida_conteudo_e_so_e_lida_pela_propria_empresa()
    {
        var png = TestImages.Png(120, 90);
        await Api.Expect(await UploadImage(T.OwnerToken, product, png), HttpStatusCode.OK);
        var res = await api.Get($"/api/v1/products/{product}/image", T.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(png, await res.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", res.Content.Headers.ContentType!.MediaType);

        var other = await api.CreateTenant("img-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/products/{product}/image", other.OwnerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadImage(other.OwnerToken, product, png)).StatusCode);
        Assert.Equal((HttpStatusCode)415, (await UploadImage(T.OwnerToken, product, "<?php echo 1; ?>"u8.ToArray(), "x.php.png")).StatusCode);
        var (_, op, _) = await api.CreateUser(T, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadImage(op, product, png)).StatusCode);
        Assert.True(await f.WithDb(db => db.AuditLogs.AnyAsync(a => a.Action == "product.image_uploaded" && a.TenantId == T.Id)));
    }

    [Fact]
    public async Task Quebra_de_caixa_gera_aviso_so_para_o_dono_da_propria_empresa()
    {
        var (_, op, _) = await api.CreateUser(T, "operator");
        var cash = await api.OpenCash(op, T.StoreId, 10000);
        var closed = await api.Post($"/api/v1/cash/sessions/{cash}/close", op, new { declaredCashCents = 9750, justification = "faltou troco" });
        await Api.Expect(closed, HttpStatusCode.OK);

        var list = await Api.Expect(await api.Get("/api/v1/notifications", T.OwnerToken), HttpStatusCode.OK);
        var n = list.EnumerateArray().Single(x => x.GetProperty("type").GetString() == "cash.shortage");
        Assert.Contains("2,50", n.GetProperty("title").GetString());
        Assert.Contains("faltou troco", n.GetProperty("body").GetString());
        Assert.Equal(PushStatus.NoProvider, n.GetProperty("pushStatus").GetString()); // sem Firebase configurado: só no app

        Assert.Equal(0, (await Api.Expect(await api.Get("/api/v1/notifications", op), HttpStatusCode.OK)).GetArrayLength());
        var other = await api.CreateTenant("ntf-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal(0, (await Api.Expect(await api.Get("/api/v1/notifications", other.OwnerToken), HttpStatusCode.OK)).GetArrayLength());

        await Api.Expect(await api.Post($"/api/v1/notifications/{n.GetProperty("id").GetGuid()}/read", T.OwnerToken), HttpStatusCode.NoContent);
        Assert.Equal(0, (await Api.Expect(await api.Get("/api/v1/notifications?unread=true", T.OwnerToken), HttpStatusCode.OK)).GetArrayLength());
    }

    [Fact]
    public async Task Fechamento_sem_falta_nao_gera_aviso()
    {
        var t = await api.CreateTenant("nof-" + Guid.NewGuid().ToString("N")[..6]);
        var cash = await api.OpenCash(t.OwnerToken, t.StoreId, 5000);
        await Api.Expect(await api.Post($"/api/v1/cash/sessions/{cash}/close", t.OwnerToken, new { declaredCashCents = 5200, justification = "sobrou" }), HttpStatusCode.OK);
        Assert.Equal(0, (await Api.Expect(await api.Get("/api/v1/notifications", t.OwnerToken), HttpStatusCode.OK)).GetArrayLength());
    }
}

/// <summary>Provedor de push falso: registra o que seria enviado ao Firebase.</summary>
public sealed class FakePushSender : IPushSender
{
    public ConcurrentBag<(string Token, string Title)> Sent { get; } = [];
    public bool Configured => true;
    public Task<PushResult> SendAsync(string token, string title, string body, IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        if (token.StartsWith("invalido")) return Task.FromResult(new PushResult(false, true, "fcm_404"));
        Sent.Add((token, title));
        return Task.FromResult(new PushResult(true, false, null));
    }
}

public class PushFactory : ApiFactory
{
    public FakePushSender Push { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        base.ConfigureWebHost(b);
        b.ConfigureTestServices(s => s.AddSingleton<IPushSender>(Push));
    }
}

public class PushTests(PushFactory f) : IClassFixture<PushFactory>
{
    [Fact]
    public async Task Quebra_de_caixa_envia_push_aos_aparelhos_do_dono_e_remove_token_invalido()
    {
        var api = new Api(f);
        var t = await api.CreateTenant("push-" + Guid.NewGuid().ToString("N")[..6]);
        await Api.Expect(await api.Post("/api/v1/push/tokens", t.OwnerToken, new { token = "token-celular-dono", platform = "android" }), HttpStatusCode.NoContent);
        await Api.Expect(await api.Post("/api/v1/push/tokens", t.OwnerToken, new { token = "invalido-antigo", platform = "ios" }), HttpStatusCode.NoContent);
        var (_, op, _) = await api.CreateUser(t, "operator");
        await Api.Expect(await api.Post("/api/v1/push/tokens", op, new { token = "token-do-operador", platform = "android" }), HttpStatusCode.NoContent);

        var cash = await api.OpenCash(op, t.StoreId, 10000);
        await Api.Expect(await api.Post($"/api/v1/cash/sessions/{cash}/close", op, new { declaredCashCents = 9000, justification = "diferença" }), HttpStatusCode.OK);

        Assert.Contains(f.Push.Sent, s => s.Token == "token-celular-dono" && s.Title.Contains("10,00"));
        Assert.DoesNotContain(f.Push.Sent, s => s.Token == "token-do-operador"); // só o dono recebe
        var n = await Api.Expect(await api.Get("/api/v1/notifications", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(PushStatus.Sent, n[0].GetProperty("pushStatus").GetString());
        Assert.False(await f.WithDb(db => db.PushTokens.IgnoreQueryFilters().AnyAsync(x => x.Token == "invalido-antigo")));
    }
}

using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Comercial.Tests;

/// <summary>Isolamento entre empresas (seção 3 e 18: acesso entre tenants, cache, identidade visual).</summary>
public class IsolationTests(ApiFactory f) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly Api api = new(f);
    private TenantInfo A = null!, B = null!;
    private Guid productA, cashA;

    public async Task InitializeAsync()
    {
        A = await api.CreateTenant("iso-a-" + Guid.NewGuid().ToString("N")[..6]);
        B = await api.CreateTenant("iso-b-" + Guid.NewGuid().ToString("N")[..6]);
        productA = await api.CreateProduct(A.OwnerToken, "A1");
        await api.StockEntry(A.OwnerToken, A.StoreId, productA, 10);
        cashA = await api.OpenCash(A.OwnerToken, A.StoreId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Tenant_B_nao_le_produto_de_A_por_id_e_tentativa_e_auditada()
    {
        var res = await api.Get($"/api/v1/products/{productA}", B.OwnerToken);
        await Api.Expect(res, HttpStatusCode.NotFound);
        var logged = await f.WithDb(db => db.AuditLogs.AnyAsync(a => a.Action == "security.cross_tenant_probe" && a.TenantId == B.Id && a.EntityId == productA.ToString()));
        Assert.True(logged);
    }

    [Fact]
    public async Task Listagens_retornam_apenas_dados_da_propria_empresa()
    {
        await api.CreateProduct(B.OwnerToken, "B1");
        var list = await Api.Expect(await api.Get("/api/v1/products", B.OwnerToken), HttpStatusCode.OK);
        Assert.All(list.EnumerateArray(), p => Assert.Equal(B.Id, p.GetProperty("tenantId").GetGuid()));
        Assert.DoesNotContain(list.EnumerateArray(), p => p.GetProperty("id").GetGuid() == productA);
    }

    [Fact]
    public async Task Tenant_B_nao_vende_produto_nem_usa_caixa_de_A()
    {
        var cashB = await api.OpenCash(B.OwnerToken, B.StoreId);
        var withProductA = await api.Post("/api/v1/sales", B.OwnerToken, Api.Sale(B.StoreId, cashB, productA));
        Assert.Equal(HttpStatusCode.NotFound, withProductA.StatusCode);

        var productB = await api.CreateProduct(B.OwnerToken, "B2");
        var withCashA = await api.Post("/api/v1/sales", B.OwnerToken, Api.Sale(B.StoreId, cashA, productB));
        Assert.Equal(HttpStatusCode.NotFound, withCashA.StatusCode);

        var withStoreA = await api.Post("/api/v1/sales", B.OwnerToken, Api.Sale(A.StoreId, cashB, productB));
        Assert.Equal(HttpStatusCode.NotFound, withStoreA.StatusCode);
        Assert.Equal(10m, await api.Balance(A.OwnerToken, A.StoreId, productA));
    }

    [Fact]
    public async Task TenantId_enviado_no_corpo_e_ignorado()
    {
        var res = await api.Post("/api/v1/products", B.OwnerToken, new { tenantId = A.Id, sku = "INJ", name = "Injetado", unit = "UN", priceCents = 1, costCents = 1, minStock = 0 });
        var body = await Api.Expect(res, HttpStatusCode.Created);
        Assert.Equal(B.Id, body.GetProperty("tenantId").GetGuid());
        Assert.False(await f.WithDb(db => db.Products.IgnoreQueryFilters().AnyAsync(p => p.TenantId == A.Id && p.Sku == "INJ")));
    }

    [Fact]
    public async Task Tenant_B_nao_cancela_venda_de_A_nem_le_estoque_de_A()
    {
        var sale = await Api.Expect(await api.Post("/api/v1/sales", A.OwnerToken, Api.Sale(A.StoreId, cashA, productA)), HttpStatusCode.Created);
        var saleId = sale.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post($"/api/v1/sales/{saleId}/cancel", B.OwnerToken, new { reason = "tentativa indevida" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/stock/balances?storeId={A.StoreId}", B.OwnerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/sales/{saleId}", B.OwnerToken)).StatusCode);
    }

    [Fact]
    public async Task Identidade_visual_nao_mistura_entre_empresas_nem_por_cache()
    {
        var admin = await api.AdminToken();
        var draft = await Api.Expect(await api.Get($"/admin/v1/tenants/{A.Id}/branding/draft", admin), HttpStatusCode.OK);
        var cfg = System.Text.Json.JsonSerializer.Deserialize<Comercial.Shared.Branding.BrandingConfig>(draft.GetRawText(), Api.Json)!;
        cfg.SystemName = "Sistema Exclusivo A";
        cfg.PrimaryColor = "#AA0000";
        await Api.Expect(await api.Put($"/admin/v1/tenants/{A.Id}/branding/draft", admin, cfg), HttpStatusCode.OK);
        await Api.Expect(await api.Post($"/admin/v1/tenants/{A.Id}/branding/publish", admin, new { confirmTenantCode = A.Code, note = "v1" }), HttpStatusCode.OK);

        var resA = await api.Get("/api/v1/branding", A.OwnerToken);
        var a = await Api.Expect(resA, HttpStatusCode.OK);
        Assert.Equal("Sistema Exclusivo A", a.GetProperty("config").GetProperty("systemName").GetString());
        var etagA = resA.Headers.ETag!.Tag;

        // B reapresenta o ETag de A: deve receber a SUA identidade (200), nunca um 304 com a de A.
        var resB = await api.Get("/api/v1/branding", B.OwnerToken, new() { ["If-None-Match"] = etagA });
        var b = await Api.Expect(resB, HttpStatusCode.OK);
        Assert.NotEqual("Sistema Exclusivo A", b.GetProperty("config").GetProperty("systemName").GetString());
        Assert.Equal(B.Id, b.GetProperty("tenantId").GetGuid());
        Assert.NotEqual(etagA, resB.Headers.ETag!.Tag);
        Assert.Contains("Authorization", resB.Headers.Vary);

        // A com o próprio ETag: 304 (cache válido somente no escopo de A).
        var again = await api.Get("/api/v1/branding", A.OwnerToken, new() { ["If-None-Match"] = etagA });
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
    }

    [Fact]
    public async Task Asset_de_identidade_de_A_nao_e_acessivel_por_B()
    {
        var admin = await api.AdminToken();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("logo"), "kind");
        var file = new ByteArrayContent(TestImages.Png(64, 64));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "logo.png");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/admin/v1/tenants/{A.Id}/branding/assets") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        var asset = await Api.Expect(await api.Http.SendAsync(req), HttpStatusCode.OK);
        var assetId = asset.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await api.Get($"/api/v1/branding/assets/{assetId}", A.OwnerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/branding/assets/{assetId}", B.OwnerToken)).StatusCode);

        // B não pode referenciar o asset de A na própria identidade.
        var draftB = await Api.Expect(await api.Get($"/admin/v1/tenants/{B.Id}/branding/draft", admin), HttpStatusCode.OK);
        var cfgB = System.Text.Json.JsonSerializer.Deserialize<Comercial.Shared.Branding.BrandingConfig>(draftB.GetRawText(), Api.Json)!;
        cfgB.LogoAssetId = assetId;
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/admin/v1/tenants/{B.Id}/branding/draft", admin, cfgB)).StatusCode);
    }

    [Fact]
    public async Task DbContext_recusa_gravar_em_outro_tenant()
    {
        using var scope = f.Services.CreateScope();
        var tc = (Comercial.Infrastructure.Data.TenantContext)scope.ServiceProvider.GetService(typeof(Comercial.Infrastructure.Data.TenantContext))!;
        tc.SetTenant(B.Id);
        var db = (Comercial.Infrastructure.Data.AppDbContext)scope.ServiceProvider.GetService(typeof(Comercial.Infrastructure.Data.AppDbContext))!;
        db.Products.Add(new Comercial.Domain.Product { TenantId = A.Id, Sku = "X", Name = "X" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Empty(await db.Products.Where(p => p.TenantId == A.Id).ToListAsync()); // filtro global
    }
}

public static class TestImages
{
    /// <summary>PNG mínimo válido (IHDR + IDAT + IEND) gerado em memória.</summary>
    public static byte[] Png(int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var len = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            ms.Write(len);
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            ms.Write(t); ms.Write(data);
            var crc = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, System.IO.Hashing.Crc32.HashToUInt32([.. t, .. data]));
            ms.Write(crc);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr);
        var raw = new byte[h * (1 + w * 3)];
        using var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, true)) zs.Write(raw);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", []);
        return ms.ToArray();
    }
}

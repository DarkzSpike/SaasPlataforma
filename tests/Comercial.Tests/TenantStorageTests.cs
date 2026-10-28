using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Comercial.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Comercial.Tests;

/// <summary>Google Drive falso: guarda os arquivos em memória e registra com qual credencial/pasta cada um foi gravado.</summary>
public sealed class FakeDriveGateway : IDriveGateway
{
    public ConcurrentDictionary<string, (DriveTarget Target, byte[] Bytes, IDictionary<string, string> Props)> Files { get; } = new();

    public Task<DriveFolderInfo> GetFolderAsync(DriveTarget t, CancellationToken ct) => Task.FromResult(t.FolderId.StartsWith("semacesso")
        ? new DriveFolderInfo(t.FolderId, "Pasta X", true, false)
        : new DriveFolderInfo(t.FolderId, "Produtos da loja", true, true));

    public Task<string> UploadAsync(DriveTarget t, string name, byte[] bytes, string contentType, IDictionary<string, string> props, CancellationToken ct)
    {
        var id = "file" + Guid.NewGuid().ToString("N");
        Files[id] = (t, bytes, props);
        return Task.FromResult(id);
    }

    public Task<(byte[] Bytes, IDictionary<string, string>? AppProperties)?> DownloadAsync(DriveTarget t, string fileId, CancellationToken ct) =>
        Task.FromResult(Files.TryGetValue(fileId, out var f) ? ((byte[], IDictionary<string, string>?)?)(f.Bytes, f.Props) : null);

    /// <summary>Pastas criadas na conta Google conectada (credencial usada, nome).</summary>
    public ConcurrentDictionary<string, (string CredentialsJson, string Name)> Folders { get; } = new();

    public Task<string> CreateFolderAsync(string credentialsJson, string name, CancellationToken ct)
    {
        var id = "pasta" + Guid.NewGuid().ToString("N");
        Folders[id] = (credentialsJson, name);
        return Task.FromResult(id);
    }
}

public class DriveFactory : ApiFactory
{
    public FakeDriveGateway Drive { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        base.ConfigureWebHost(b);
        b.ConfigureTestServices(s => s.AddSingleton<IDriveGateway>(Drive));
    }
}

public class TenantStorageTests(DriveFactory f) : IClassFixture<DriveFactory>
{
    private readonly Api api = new(f);

    private static string ServiceAccount(string email) =>
        "{\"type\":\"service_account\",\"client_email\":\"" + email + "\",\"private_key\":\"-----BEGIN PRIVATE KEY-----\\nSEGREDO-" + email + "\\n-----END PRIVATE KEY-----\\n\"}";

    private async Task<HttpResponseMessage> Save(TenantInfo t, object body, bool reauth = true) =>
        await api.Put($"/admin/v1/tenants/{t.Id}/storage", await api.AdminToken(), body, reauth ? api.Reauth() : null);

    private async Task<HttpResponseMessage> Upload(string token, Guid productId)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(TestImages.Png(64, 64));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "foto.png");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/products/{productId}/image") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await api.Http.SendAsync(req);
    }

    [Fact]
    public async Task Cada_empresa_grava_fotos_na_propria_pasta_com_a_propria_credencial()
    {
        var a = await api.CreateTenant("dra-" + Guid.NewGuid().ToString("N")[..6]);
        var b = await api.CreateTenant("drb-" + Guid.NewGuid().ToString("N")[..6]);
        await Api.Expect(await Save(a, new { provider = "gdrive", driveFolder = "https://drive.google.com/drive/folders/PastaDaEmpresaA_123?usp=sharing",
            credentialsJson = ServiceAccount("loja-a@proj.iam.gserviceaccount.com"), confirmTenantCode = a.Code }), HttpStatusCode.OK);
        await Api.Expect(await Save(b, new { provider = "gdrive", driveFolder = "PastaDaEmpresaB_456",
            credentialsJson = ServiceAccount("loja-b@proj.iam.gserviceaccount.com"), confirmTenantCode = b.Code }), HttpStatusCode.OK);

        var pa = await api.CreateProduct(a.OwnerToken, "FA");
        var pb = await api.CreateProduct(b.OwnerToken, "FB");
        await Api.Expect(await Upload(a.OwnerToken, pa), HttpStatusCode.OK);
        await Api.Expect(await Upload(b.OwnerToken, pb), HttpStatusCode.OK);

        var fa = f.Drive.Files.Values.Single(x => x.Props["tenantId"] == a.Id.ToString());
        var fb = f.Drive.Files.Values.Single(x => x.Props["tenantId"] == b.Id.ToString());
        Assert.Equal("PastaDaEmpresaA_123", fa.Target.FolderId);              // link do Drive convertido em ID
        Assert.Contains("loja-a@", fa.Target.CredentialsJson);
        Assert.Equal("PastaDaEmpresaB_456", fb.Target.FolderId);
        Assert.Contains("loja-b@", fb.Target.CredentialsJson);

        // Leitura pela API da própria empresa funciona; a outra empresa não enxerga o produto.
        Assert.Equal(HttpStatusCode.OK, (await api.Get($"/api/v1/products/{pa}/image", a.OwnerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/products/{pa}/image", b.OwnerToken)).StatusCode);
    }

    [Fact]
    public async Task Credencial_fica_criptografada_e_nunca_volta_pela_api()
    {
        var t = await api.CreateTenant("drc-" + Guid.NewGuid().ToString("N")[..6]);
        var json = ServiceAccount("seguro@proj.iam.gserviceaccount.com");
        var saved = await Api.Expect(await Save(t, new { provider = "gdrive", driveFolder = "PastaSegura_789", credentialsJson = json, confirmTenantCode = t.Code }), HttpStatusCode.OK);
        var view = await Api.Expect(await api.Get($"/admin/v1/tenants/{t.Id}/storage", await api.AdminToken()), HttpStatusCode.OK);

        foreach (var text in new[] { saved.ToString(), view.ToString() })
        {
            Assert.DoesNotContain("PRIVATE KEY", text);
            Assert.DoesNotContain("SEGREDO", text);
        }
        Assert.True(view.GetProperty("hasCredentials").GetBoolean());
        Assert.Equal("seguro@proj.iam.gserviceaccount.com", view.GetProperty("credentialsClientEmail").GetString());

        var row = await f.WithDb(db => db.StorageSettings.IgnoreQueryFilters().SingleAsync(x => x.TenantId == t.Id));
        Assert.DoesNotContain("SEGREDO", row.CredentialsProtected);                  // gravado criptografado
        var audit = await f.WithDb(db => db.AuditLogs.Where(x => x.TenantId == t.Id && x.Action == "tenant.storage_updated").ToListAsync());
        Assert.Single(audit);
        Assert.DoesNotContain("SEGREDO", audit[0].DataJson ?? "");

        // Salvar de novo sem enviar o JSON mantém a credencial atual.
        var again = await Api.Expect(await Save(t, new { provider = "gdrive", driveFolder = "PastaSegura_789", confirmTenantCode = t.Code }), HttpStatusCode.OK);
        Assert.True(again.GetProperty("hasCredentials").GetBoolean());
    }

    [Fact]
    public async Task Alteracao_exige_mfa_confirmacao_e_dados_validos()
    {
        var t = await api.CreateTenant("drd-" + Guid.NewGuid().ToString("N")[..6]);
        var ok = new { provider = "gdrive", driveFolder = "PastaValida_000", credentialsJson = ServiceAccount("x@proj.iam.gserviceaccount.com"), confirmTenantCode = t.Code };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Save(t, ok, reauth: false)).StatusCode);
        Assert.Equal("confirmation_mismatch", await Api.Code(await Save(t, ok with { confirmTenantCode = "outra" })));
        Assert.Equal("invalid_credentials", await Api.Code(await Save(t, ok with { credentialsJson = "{\"type\":\"authorized_user\",\"client_id\":\"x\"}" })));
        Assert.Equal("invalid_credentials", await Api.Code(await Save(t, ok with { credentialsJson = "não é json" })));
        Assert.Equal("invalid_folder", await Api.Code(await Save(t, ok with { driveFolder = "../../etc" })));
        Assert.Equal("credentials_required", await Api.Code(await Save(t, new { provider = "gdrive", driveFolder = "PastaValida_000", confirmTenantCode = t.Code })));
        Assert.Equal("invalid_provider", await Api.Code(await Save(t, ok with { provider = "ftp" })));
        // Token de loja não acessa a configuração.
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get($"/admin/v1/tenants/{t.Id}/storage", t.OwnerToken)).StatusCode);
    }

    [Fact]
    public async Task Testar_conexao_informa_quando_a_pasta_nao_foi_compartilhada()
    {
        var t = await api.CreateTenant("dre-" + Guid.NewGuid().ToString("N")[..6]);
        await Api.Expect(await Save(t, new { provider = "gdrive", driveFolder = "semacesso_pasta1", credentialsJson = ServiceAccount("bot@proj.iam.gserviceaccount.com"), confirmTenantCode = t.Code }), HttpStatusCode.OK);
        var r = await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/storage/test", await api.AdminToken(), new { }), HttpStatusCode.OK);
        Assert.False(r.GetProperty("lastTestOk").GetBoolean());
        Assert.Contains("bot@proj.iam.gserviceaccount.com", r.GetProperty("lastTestMessage").GetString());

        await Api.Expect(await Save(t, new { provider = "gdrive", driveFolder = "PastaBoa_00001", confirmTenantCode = t.Code }), HttpStatusCode.OK);
        r = await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/storage/test", await api.AdminToken(), new { }), HttpStatusCode.OK);
        Assert.True(r.GetProperty("lastTestOk").GetBoolean());
    }

    [Fact]
    public async Task Empresa_sem_configuracao_continua_no_servidor()
    {
        var t = await api.CreateTenant("drf-" + Guid.NewGuid().ToString("N")[..6]);
        var view = await Api.Expect(await api.Get($"/admin/v1/tenants/{t.Id}/storage", await api.AdminToken()), HttpStatusCode.OK);
        Assert.Equal("local", view.GetProperty("provider").GetString());
        var p = await api.CreateProduct(t.OwnerToken, "LOC");
        await Api.Expect(await Upload(t.OwnerToken, p), HttpStatusCode.OK);
        Assert.DoesNotContain(f.Drive.Files.Values, x => x.Props["tenantId"] == t.Id.ToString());
        Assert.StartsWith("local:", await f.WithDb(db => db.Products.IgnoreQueryFilters().Where(x => x.Id == p).Select(x => x.ImageKey!).SingleAsync()));
    }
}

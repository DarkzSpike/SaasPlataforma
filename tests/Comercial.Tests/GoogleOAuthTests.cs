using System.Net;
using System.Text.Json;
using Comercial.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Comercial.Tests;

public sealed class FakeGoogleOAuth : IGoogleOAuthClient
{
    public bool OmitRefreshToken { get; set; }
    public List<(string Code, string Redirect)> Exchanges { get; } = [];
    public Task<GoogleTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct)
    {
        Exchanges.Add((code, redirectUri));
        return Task.FromResult(new GoogleTokens(OmitRefreshToken ? null : "refresh-" + code, "access-" + code));
    }
    public Task<string> GetEmailAsync(string accessToken, CancellationToken ct) => Task.FromResult("dono.da.loja@gmail.com");
}

public class OAuthFactory : ApiFactory
{
    public FakeDriveGateway Drive { get; } = new();
    public FakeGoogleOAuth Google { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        base.ConfigureWebHost(b);
        b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IDriveGateway>(Drive);
            s.RemoveAll<IGoogleOAuthClient>();
            s.AddSingleton<IGoogleOAuthClient>(Google);
        });
    }
}

/// <summary>"Conectar minha conta Google": fotos no Google Drive pessoal (OAuth, escopo drive.file).</summary>
public class GoogleOAuthTests(OAuthFactory f) : IClassFixture<OAuthFactory>
{
    private readonly Api api = new(f);

    private static string StateOf(string url) =>
        Uri.UnescapeDataString(url.Split('?')[1].Split('&').Single(p => p.StartsWith("state="))["state=".Length..]);

    private async Task Configure()
    {
        var admin = await api.AdminToken();
        await Api.Expect(await api.Put("/admin/v1/settings/google-oauth", admin,
            new { clientId = "123-abc.apps.googleusercontent.com", clientSecret = "GOCSPX-segredo-de-teste" }, api.Reauth()), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Conectar_conta_google_cria_pasta_e_as_fotos_vao_para_o_drive_pessoal()
    {
        var t = await api.CreateTenant("go-" + Guid.NewGuid().ToString("N")[..6]);
        var admin = await api.AdminToken();
        Assert.Equal("invalid_client_id", await Api.Code(await api.Put("/admin/v1/settings/google-oauth", admin, new { clientId = "qualquer", clientSecret = "x" }, api.Reauth())));
        await Configure();
        var view = await Api.Expect(await api.Get("/admin/v1/settings/google-oauth", admin), HttpStatusCode.OK);
        Assert.True(view.GetProperty("configured").GetBoolean());
        Assert.EndsWith("/oauth/google/callback", view.GetProperty("redirectUri").GetString());
        Assert.DoesNotContain("GOCSPX", view.GetRawText()); // o segredo nunca volta para o painel

        var start = await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/storage/google/start", admin, new { }, api.Reauth()), HttpStatusCode.OK);
        var url = start.GetProperty("url").GetString()!;
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains(Uri.EscapeDataString("https://www.googleapis.com/auth/drive.file"), url);
        Assert.Contains("access_type=offline", url);
        var state = StateOf(url);

        // O Google devolve o navegador para a API com o código.
        var res = await f.CreateClient().GetAsync($"/oauth/google/callback?code=abc&state={Uri.EscapeDataString(state)}");
        var html = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Conta Google conectada", html);

        var storage = await Api.Expect(await api.Get($"/admin/v1/tenants/{t.Id}/storage", admin), HttpStatusCode.OK);
        Assert.Equal("gdrive", storage.GetProperty("provider").GetString());
        Assert.Equal("dono.da.loja@gmail.com", storage.GetProperty("credentialsClientEmail").GetString());
        Assert.DoesNotContain("refresh", storage.GetRawText()); // o token fica criptografado no servidor
        var folderId = storage.GetProperty("driveFolderId").GetString()!;
        var folder = f.Drive.Folders[folderId];
        Assert.Contains(t.Code, folder.Name);
        using (var doc = JsonDocument.Parse(folder.CredentialsJson))
        {
            Assert.Equal("authorized_user", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("refresh-abc", doc.RootElement.GetProperty("refresh_token").GetString());
        }

        // Foto de produto da loja vai para a pasta criada, com a credencial da conta Google.
        var prod = await api.CreateProduct(t.OwnerToken, "GO1");
        var png = TestImages.Png(64, 64);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png); file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "foto.png");
        var http = f.CreateClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", t.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/api/v1/products/{prod}/image", form)).StatusCode);
        var stored = f.Drive.Files.Values.Single(x => x.Props["productId"] == prod.ToString());
        Assert.Equal(folderId, stored.Target.FolderId);
        Assert.Contains("authorized_user", stored.Target.CredentialsJson);

        // O mesmo link não vale duas vezes.
        var again = await f.CreateClient().GetAsync($"/oauth/google/callback?code=abc&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("já foi usado", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Link_adulterado_cancelado_ou_sem_permissao_permanente_nao_altera_nada()
    {
        var t = await api.CreateTenant("gx-" + Guid.NewGuid().ToString("N")[..6]);
        var admin = await api.AdminToken();
        await Configure();
        var client = f.CreateClient();

        var bad = await client.GetAsync("/oauth/google/callback?code=abc&state=forjado");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("expirado ou inválido", await bad.Content.ReadAsStringAsync());

        var denied = await client.GetAsync("/oauth/google/callback?error=access_denied");
        Assert.Contains("cancelada", await denied.Content.ReadAsStringAsync());

        f.Google.OmitRefreshToken = true;
        try
        {
            var url = (await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/storage/google/start", admin, new { }, api.Reauth()), HttpStatusCode.OK)).GetProperty("url").GetString()!;
            var r = await client.GetAsync($"/oauth/google/callback?code=x&state={Uri.EscapeDataString(StateOf(url))}");
            Assert.Contains("permissão permanente", await r.Content.ReadAsStringAsync());
        }
        finally { f.Google.OmitRefreshToken = false; }

        var storage = await Api.Expect(await api.Get($"/admin/v1/tenants/{t.Id}/storage", admin), HttpStatusCode.OK);
        Assert.Equal("local", storage.GetProperty("provider").GetString());

        // Iniciar a conexão exige o código do autenticador do admin; lojista não acessa.
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Post($"/admin/v1/tenants/{t.Id}/storage/google/start", admin, new { })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await api.Post($"/admin/v1/tenants/{t.Id}/storage/google/start", t.OwnerToken, new { })).StatusCode);
    }

    [Fact]
    public void Chave_de_api_e_recusada_com_explicacao()
    {
        var ex = Assert.Throws<Comercial.Domain.DomainException>(() => TenantStorageService.ValidateCredentials("AIzaSyExemploDeChaveDeApi0000000000000"));
        Assert.Equal("api_key_not_supported", ex.Code);
        Assert.Contains("Conectar minha conta Google", ex.Message);
    }
}

using System.Net;
using System.Security.Cryptography;
using Comercial.Desktop.Core;

namespace Comercial.Tests;

/// <summary>Atualização automática do caixa: versão assinada pelo servidor + SHA-256 do instalador.</summary>
public class UpdateTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private readonly Api api = new(f);
    private static readonly byte[] Installer = "instalador-falso-para-teste"u8.ToArray();
    private static readonly string Sha = Convert.ToHexStringLower(SHA256.HashData(Installer));

    private sealed class FileHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    [Fact]
    public async Task Versao_publicada_e_assinada_e_o_caixa_rejeita_adulteracao()
    {
        var admin = await api.AdminToken();
        Assert.Equal("invalid_url", await Api.Code(await api.Put("/admin/v1/settings/desktop-release", admin,
            new { version = "1.2.0", url = "http://exemplo.com/setup.exe", sha256 = Sha, mandatory = false }, api.Reauth())));
        Assert.Equal("invalid_sha256", await Api.Code(await api.Put("/admin/v1/settings/desktop-release", admin,
            new { version = "1.2.0", url = "https://exemplo.com/setup.exe", sha256 = "abc", mandatory = false }, api.Reauth())));
        await Api.Expect(await api.Put("/admin/v1/settings/desktop-release", admin,
            new { version = "1.2.0", url = "https://exemplo.com/setup.exe", sha256 = Sha, notes = "Correções", mandatory = false }, api.Reauth()), HttpStatusCode.OK);

        var pem = (await Api.Expect(await api.Get("/api/v1/license/public-key", (await api.CreateTenant("up-" + Guid.NewGuid().ToString("N")[..6])).OwnerToken), HttpStatusCode.OK)).GetProperty("pem").GetString()!;
        var http = f.CreateClient();
        var u = await Updater.CheckAsync(http, new Version(1, 0, 0), pem);
        Assert.NotNull(u);
        Assert.Equal("1.2.0", u!.Version);
        Assert.Null(await Updater.CheckAsync(http, new Version(1, 2, 0), pem));          // já está na versão
        Assert.False(Updater.VerifySignature(u with { Url = "https://atacante.com/setup.exe" }, pem));
        Assert.False(Updater.VerifySignature(u with { Sha256 = new string('0', 64) }, pem));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(Updater.VerifySignature(u, other.ExportSubjectPublicKeyInfoPem())); // outra chave

        var dir = Path.Combine(Path.GetTempPath(), "upd-" + Guid.NewGuid().ToString("N"));
        var ok = await Updater.DownloadAsync(new HttpClient(new FileHandler(Installer)), u, dir);
        Assert.True(File.Exists(ok));
        var bad = await Assert.ThrowsAsync<InvalidOperationException>(() => Updater.DownloadAsync(new HttpClient(new FileHandler("outro programa"u8.ToArray())), u, dir));
        Assert.Contains("SHA-256", bad.Message);
        Assert.Empty(Directory.GetFiles(dir)); // o arquivo adulterado (mesmo nome) foi apagado
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Comparacao_de_versoes()
    {
        Assert.True(Updater.IsNewer("1.10.0", new Version(1, 9, 3)));
        Assert.False(Updater.IsNewer("1.0", new Version(1, 0, 0, 0)));
        Assert.False(Updater.IsNewer("lixo", new Version(1, 0)));
        Assert.False(Updater.AllowedUrl("http://exemplo.com/a.exe"));
        Assert.True(Updater.AllowedUrl("https://github.com/x/y/releases/download/v1/a.exe"));
    }
}

using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Comercial.Api.Services;

namespace Comercial.Tests;

/// <summary>WhatsApp de atendimento do botão flutuante do caixa: padrão, troca pelo painel e desligamento.</summary>
public class SupportContactTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private readonly Api api = new(f);

    [Fact]
    public async Task Numero_padrao_pode_ser_trocado_pelo_painel_e_desligado()
    {
        var pub = await Api.Expect(await api.Get("/api/v1/public/support", null), HttpStatusCode.OK);   // sem login
        Assert.Equal("5547992841812", pub.GetProperty("whatsapp").GetString());
        Assert.True(pub.GetProperty("enabled").GetBoolean());

        var admin = await api.AdminToken();
        Assert.Equal("invalid_phone", await Api.Code(await api.Put("/admin/v1/settings/support", admin, new { whatsapp = "1234" }, api.Reauth())));
        await Api.Expect(await api.Put("/admin/v1/settings/support", admin, new { whatsapp = "(47) 3333-4444", message = "Oi, sou cliente" }, api.Reauth()), HttpStatusCode.OK);
        pub = await Api.Expect(await api.Get("/api/v1/public/support", null), HttpStatusCode.OK);
        Assert.Equal("554733334444", pub.GetProperty("whatsapp").GetString());
        Assert.Equal("Oi, sou cliente", pub.GetProperty("message").GetString());

        await Api.Expect(await api.Put("/admin/v1/settings/support", admin, new { whatsapp = "" }, api.Reauth()), HttpStatusCode.OK);
        Assert.False((await Api.Expect(await api.Get("/api/v1/public/support", null), HttpStatusCode.OK)).GetProperty("enabled").GetBoolean());

        // Lojista não troca o número (rota exclusiva do admin).
        var t = await api.CreateTenant("wa-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.NotEqual(HttpStatusCode.OK, (await api.Put("/admin/v1/settings/support", t.OwnerToken, new { whatsapp = "47999999999" })).StatusCode);
        await Api.Expect(await api.Put("/admin/v1/settings/support", admin, new { whatsapp = "47992841812" }, api.Reauth()), HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("47992841812", "5547992841812")]
    [InlineData("+55 (47) 99284-1812", "5547992841812")]
    [InlineData("", "")]
    public void Normaliza_numero(string raw, string expected) => Assert.Equal(expected, PlatformSettingsService.NormalizeWhatsapp(raw));
}

/// <summary>Link do botão do WhatsApp no caixa.</summary>
public class WhatsappLinkTests
{
    [Fact]
    public void Link_leva_mensagem_loja_e_usuario()
    {
        var url = Comercial.Desktop.Core.WhatsappLink.Build("5547992841812", "Olá! Preciso de ajuda", "loja-demo", "dona@loja.test")!;
        Assert.StartsWith("https://wa.me/5547992841812?text=", url);
        var text = Uri.UnescapeDataString(url[(url.IndexOf("text=") + 5)..]);
        Assert.Equal("Olá! Preciso de ajuda\n\nLoja: loja-demo • Usuário: dona@loja.test", text);
    }

    [Fact]
    public void Sem_numero_valido_o_botao_some()
    {
        Assert.Null(Comercial.Desktop.Core.WhatsappLink.Build("", null));
        Assert.Null(Comercial.Desktop.Core.WhatsappLink.Build("4799", null));
        Assert.Contains("Preciso%20de%20ajuda", Comercial.Desktop.Core.WhatsappLink.Build("5547992841812", null)); // mensagem padrão, sem loja (tela de login)
    }
}

/// <summary>Versão do app Android publicada no painel e download do APK pelo próprio servidor.</summary>
public class MobileReleaseTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private readonly Api api = new(f);

    [Fact]
    public async Task Versao_do_app_e_publicada_e_apk_e_servido_com_seguranca()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await api.Get("/api/v1/public/mobile/latest", null)).StatusCode);
        var admin = await api.AdminToken();
        Assert.Equal("invalid_url", await Api.Code(await api.Put("/admin/v1/settings/mobile-release", admin,
            new { versionCode = 2, versionName = "1.0.1", url = "http://exemplo.com/app.apk" }, api.Reauth())));      // internet sem HTTPS
        await Api.Expect(await api.Put("/admin/v1/settings/mobile-release", admin,
            new { versionCode = 2, versionName = "1.0.1", url = "http://192.168.100.176:5080/downloads/comercial-1.0.1.apk", notes = "Teste" }, api.Reauth()), HttpStatusCode.OK);
        var r = await Api.Expect(await api.Get("/api/v1/public/mobile/latest", null), HttpStatusCode.OK);
        Assert.Equal(2, r.GetProperty("versionCode").GetInt32());
        Assert.Equal("1.0.1", r.GetProperty("versionName").GetString());

        var dir = Path.Combine(Path.GetTempPath(), "apk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "teste-app.apk"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(dir, "segredo.txt"), "x");
        using var host = f.WithWebHostBuilder(b => b.UseSetting("Downloads:Root", dir));
        var http = host.CreateClient();
        var ok = await http.GetAsync("/downloads/teste-app.apk");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("application/vnd.android.package-archive", ok.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/downloads/segredo.txt")).StatusCode);             // só .apk
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/downloads/..%2Fsegredo.apk")).StatusCode);       // sem subpastas
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/downloads/nao-existe.apk")).StatusCode);

        // Caminho relativo (como em desenvolvimento: dev-data/storage) é resolvido a partir da pasta da API.
        var rel = "apk-rel-" + Guid.NewGuid().ToString("N")[..8];
        var relDir = Path.Combine(Directory.GetCurrentDirectory(), rel);
        Directory.CreateDirectory(relDir);
        await File.WriteAllBytesAsync(Path.Combine(relDir, "teste-app.apk"), [1, 2, 3]);
        using var host2 = f.WithWebHostBuilder(b => { b.UseSetting("Downloads:Root", rel); b.UseContentRoot(Directory.GetCurrentDirectory()); });
        Assert.Equal(HttpStatusCode.OK, (await host2.CreateClient().GetAsync("/downloads/teste-app.apk")).StatusCode);
        Directory.Delete(relDir, true);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Apk_gerado_no_servidor_vira_a_versao_mais_nova_sem_publicar_no_painel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "apk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var host = f.WithWebHostBuilder(b => b.UseSetting("Downloads:Root", dir));
        var http = host.CreateClient();
        async Task<JsonElement?> Latest()
        {
            var res = await http.GetAsync("/api/v1/public/mobile/latest");
            return res.StatusCode == HttpStatusCode.NoContent ? null : await res.Content.ReadFromJsonAsync<JsonElement>();
        }
        var before = await Latest();   // o outro teste pode ter publicado uma versão pelo painel

        // Manifesto apontando para um APK que não existe (ou nome inválido): ignorado.
        await File.WriteAllTextAsync(Path.Combine(dir, "mobile-latest.json"), """{"versionCode":900,"versionName":"9.0.0","file":"comercial-9.0.0.apk"}""");
        Assert.Equal(before?.GetProperty("versionCode").GetInt32(), (await Latest())?.GetProperty("versionCode").GetInt32());
        await File.WriteAllTextAsync(Path.Combine(dir, "mobile-latest.json"), """{"versionCode":900,"versionName":"9.0.0","file":"../segredo.apk"}""");
        Assert.Equal(before?.GetProperty("versionCode").GetInt32(), (await Latest())?.GetProperty("versionCode").GetInt32());

        await File.WriteAllBytesAsync(Path.Combine(dir, "comercial-9.0.0.apk"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(dir, "mobile-latest.json"), """{"versionCode":900,"versionName":"9.0.0","file":"comercial-9.0.0.apk","notes":"Novidades"}""");
        var r = (await Latest())!.Value;
        Assert.Equal(900, r.GetProperty("versionCode").GetInt32());
        Assert.Equal("9.0.0", r.GetProperty("versionName").GetString());
        Assert.Equal("/downloads/comercial-9.0.0.apk", r.GetProperty("path").GetString());   // relativo: baixa do servidor em uso
        Assert.EndsWith("/downloads/comercial-9.0.0.apk", r.GetProperty("url").GetString());   // completo: apps até a 1.0.7
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(r.GetProperty("path").GetString())).StatusCode);
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("http://192.168.0.5:5080/a.apk", true)]
    [InlineData("http://10.1.2.3/a.apk", true)]
    [InlineData("http://172.20.0.1/a.apk", true)]
    [InlineData("http://172.40.0.1/a.apk", false)]
    [InlineData("http://exemplo.com/a.apk", false)]
    public void Rede_local(string url, bool local) => Assert.Equal(local, PlatformSettingsService.IsLocalHost(new Uri(url)));
}

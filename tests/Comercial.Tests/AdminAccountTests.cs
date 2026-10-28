using System.Net;
using Comercial.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Tests;

/// <summary>Fábrica própria: a troca de senha do admin não pode afetar as outras classes de teste.</summary>
public class AdminAccountFactory : ApiFactory;

/// <summary>Minha conta do painel: trocar senha e e-mail do administrador.</summary>
public class AdminAccountTests(AdminAccountFactory f) : IClassFixture<AdminAccountFactory>
{
    private readonly Api api = new(f);
    private static string Code(int offset) => Totp.Code(ApiFactory.TotpSecret, Totp.CurrentStep(DateTimeOffset.UtcNow) + offset);

    [Fact]
    public async Task Troca_senha_e_email_com_senha_atual_e_codigo_e_derruba_as_outras_sessoes()
    {
        var token = await api.AdminToken();   // login com o código atual

        // Proteções: sem o código do autenticador, senha atual errada, senha fraca ou igual.
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Put("/admin/v1/me/password", token, new { currentPassword = ApiFactory.AdminPassword, newPassword = "Nova-Senha-456" })).StatusCode);
        Assert.Equal("invalid_password", await Api.Code(await api.Put("/admin/v1/me/password", token, new { currentPassword = "errada-1234", newPassword = "Nova-Senha-456" }, api.Reauth())));
        Assert.Equal("weak_password", await Api.Code(await api.Put("/admin/v1/me/password", token, new { currentPassword = ApiFactory.AdminPassword, newPassword = "123" }, api.Reauth())));
        Assert.Equal("same_password", await Api.Code(await api.Put("/admin/v1/me/password", token, new { currentPassword = ApiFactory.AdminPassword, newPassword = ApiFactory.AdminPassword }, api.Reauth())));
        Assert.Equal("invalid_email", await Api.Code(await api.Put("/admin/v1/me/email", token, new { newEmail = "sem-arroba", currentPassword = ApiFactory.AdminPassword }, api.Reauth())));

        // Troca de senha: o token antigo deixa de valer; a senha antiga não entra; a nova entra.
        await Task.Delay(1100); // token emitido num segundo anterior ao da troca
        await Api.Expect(await api.Put("/admin/v1/me/password", token, new { currentPassword = ApiFactory.AdminPassword, newPassword = "Nova-Senha-456" }, api.Reauth()), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/admin/v1/me", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Post("/admin/v1/auth/login", null, new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword, totpCode = Code(1) })).StatusCode);
        var login = await Api.Expect(await api.Post("/admin/v1/auth/login", null, new { email = ApiFactory.AdminEmail, password = "Nova-Senha-456", totpCode = Code(1) }), HttpStatusCode.OK);
        var token2 = login.GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/admin/v1/me", token2)).StatusCode);

        // Troca de e-mail (fica em minúsculas) e a sessão anterior cai.
        await Task.Delay(1100);
        await Api.Expect(await api.Put("/admin/v1/me/email", token2, new { newEmail = "Dono.Do.Sistema@Gmail.com", currentPassword = "Nova-Senha-456" }, api.Reauth()), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/admin/v1/me", token2)).StatusCode);
        Assert.True(await f.WithDb(db => db.AdminUsers.AnyAsync(a => a.Email == "dono.do.sistema@gmail.com")));
        Assert.False(await f.WithDb(db => db.AdminUsers.AnyAsync(a => a.Email == ApiFactory.AdminEmail)));
        f.AdminTokenCache = null;
    }
}

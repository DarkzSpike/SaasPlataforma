using System.Net;
using System.Text.RegularExpressions;
using Comercial.Api.Security;
using Comercial.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Tests;

/// <summary>Cadastro com teste grátis (com código por e-mail) e verificação em duas etapas dos usuários das lojas.</summary>
public class OnboardingTests(EmailFactory f) : IClassFixture<EmailFactory>
{
    private readonly Api api = new(f);

    private string LastCode(string to) => Regex.Match(f.Mail.Sent.Where(m => m.To == to && m.Kind == "signup").OrderBy(m => m.CreatedAt).Last().HtmlBody, @">(\d{6})<").Groups[1].Value;

    [Fact]
    public async Task Cadastro_cria_a_loja_so_depois_do_codigo_e_com_teste_gratis()
    {
        var mail = $"dono-{Guid.NewGuid():N}"[..20] + "@exemplo.test";
        var start = await Api.Expect(await api.Post("/api/v1/public/signup", null, new { companyName = "Mercadinho São João", ownerName = "João", email = mail, password = "Senha-Forte-123" }), HttpStatusCode.OK);
        var code = start.GetProperty("tenantCode").GetString()!;
        Assert.StartsWith("mercadinho-sao-joao", code);
        Assert.False(await f.WithDb(db => db.Tenants.AnyAsync(t => t.Code == code)));          // ainda não existe
        await f.Flush();
        var id = start.GetProperty("signupId").GetGuid();
        Assert.Equal("invalid_code", await Api.Code(await api.Post("/api/v1/public/signup/verify", null, new { signupId = id, code = "000000" })));
        var done = await Api.Expect(await api.Post("/api/v1/public/signup/verify", null, new { signupId = id, code = LastCode(mail) }), HttpStatusCode.OK);
        var trialEnd = done.GetProperty("trialEndsAt").GetDateTimeOffset();
        Assert.InRange((trialEnd - DateTimeOffset.UtcNow).TotalDays, 13, 15);                  // 14 dias de teste
        var token = await api.Login(code, mail, "Senha-Forte-123");
        Assert.Equal("active", (await Api.Expect(await api.Get("/api/v1/me", token), HttpStatusCode.OK)).GetProperty("license").GetString());
        Assert.Equal("invalid_code", await Api.Code(await api.Post("/api/v1/public/signup/verify", null, new { signupId = id, code = LastCode(mail) }))); // uso único
        Assert.Equal("", (await f.WithDb(db => db.PendingSignups.SingleAsync(p => p.Id == id))).PayloadProtected);  // senha não fica guardada
    }

    [Fact]
    public async Task Cadastro_recusa_robo_senha_fraca_e_pode_ser_desligado()
    {
        Assert.Equal("invalid_request", await Api.Code(await api.Post("/api/v1/public/signup", null, new { companyName = "X Loja", ownerName = "X", email = "x@y.test", password = "Senha-Forte-123", website = "http://spam" })));
        Assert.Equal("weak_password", await Api.Code(await api.Post("/api/v1/public/signup", null, new { companyName = "X Loja", ownerName = "X", email = "x@y.test", password = "123" })));
        var admin = await api.AdminToken();
        await Api.Expect(await api.Put("/admin/v1/settings/signup", admin, new { enabled = false, planCode = "basico", trialDays = 7 }, api.Reauth()), HttpStatusCode.OK);
        Assert.Equal("signup_disabled", await Api.Code(await api.Post("/api/v1/public/signup", null, new { companyName = "Y Loja", ownerName = "Y", email = "y@y.test", password = "Senha-Forte-123" })));
        await Api.Expect(await api.Put("/admin/v1/settings/signup", admin, new { enabled = true, planCode = "profissional", trialDays = 14 }, api.Reauth()), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Dois_fatores_exige_codigo_no_login_e_nao_aceita_repeticao()
    {
        var t = await api.CreateTenant("tf-" + Guid.NewGuid().ToString("N")[..6]);
        var setup = await Api.Expect(await api.Post("/api/v1/me/2fa/setup", t.OwnerToken), HttpStatusCode.OK);
        var secret = setup.GetProperty("secret").GetString()!;
        Assert.StartsWith("otpauth://totp/", setup.GetProperty("otpauth").GetString());
        Assert.Equal("invalid_code", await Api.Code(await api.Post("/api/v1/me/2fa/enable", t.OwnerToken, new { code = "000000" })));
        var now = Totp.Code(secret, Totp.CurrentStep(DateTimeOffset.UtcNow));
        await Api.Expect(await api.Post("/api/v1/me/2fa/enable", t.OwnerToken, new { code = now }), HttpStatusCode.NoContent);

        Assert.Equal("totp_required", await Api.Code(await api.Post("/api/v1/auth/login", null, new LoginRequest(t.Code, t.OwnerEmail, t.OwnerPassword, null))));
        Assert.Equal("invalid_totp", await Api.Code(await api.Post("/api/v1/auth/login", null, new LoginRequest(t.Code, t.OwnerEmail, t.OwnerPassword, null, "123456"))));
        var next = Totp.Code(secret, Totp.CurrentStep(DateTimeOffset.UtcNow) + 1);
        await Api.Expect(await api.Post("/api/v1/auth/login", null, new LoginRequest(t.Code, t.OwnerEmail, t.OwnerPassword, null, next)), HttpStatusCode.OK);
        Assert.Equal("invalid_totp", await Api.Code(await api.Post("/api/v1/auth/login", null, new LoginRequest(t.Code, t.OwnerEmail, t.OwnerPassword, null, next)))); // mesmo código não vale 2x
        Assert.Equal("invalid_credentials", await Api.Code(await api.Post("/api/v1/auth/login", null, new LoginRequest(t.Code, t.OwnerEmail, "senha-errada-1", null, next)))); // senha é conferida antes
    }
}

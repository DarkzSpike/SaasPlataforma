using System.Net;
using System.Text;
using Comercial.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Comercial.Tests;

/// <summary>Autenticação, sessões, tokens, força bruta, dispositivos e acesso ao painel.</summary>
public class AuthTests(ApiFactory f) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly Api api = new(f);
    private TenantInfo T = null!;

    public async Task InitializeAsync() => T = await api.CreateTenant("auth-" + Guid.NewGuid().ToString("N")[..6]);
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Forca_bruta_bloqueia_conta_mesmo_com_senha_correta_depois()
    {
        var (_, _, email) = await api.CreateUser(T, "operator");
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.Post("/api/v1/auth/login", null, new { tenantCode = T.Code, email, password = "errada-123456" })).StatusCode);
        var locked = await api.Post("/api/v1/auth/login", null, new { tenantCode = T.Code, email, password = "Senha-Func-123" });
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
    }

    [Fact]
    public async Task Mensagem_de_erro_nao_revela_se_usuario_ou_empresa_existem()
    {
        var a = await api.Post("/api/v1/auth/login", null, new { tenantCode = T.Code, email = "naoexiste@x.test", password = "qualquer-123" });
        var b = await api.Post("/api/v1/auth/login", null, new { tenantCode = "empresa-inexistente", email = T.OwnerEmail, password = "qualquer-123" });
        var c = await api.Post("/api/v1/auth/login", null, new { tenantCode = T.Code, email = T.OwnerEmail, password = "senha-errada-1" });
        Assert.Equal(await a.Content.ReadAsStringAsync(), await b.Content.ReadAsStringAsync());
        Assert.Equal(await a.Content.ReadAsStringAsync(), await c.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reuso_de_refresh_token_revoga_toda_a_familia()
    {
        var first = await api.LoginFull(T.Code, T.OwnerEmail, T.OwnerPassword);
        var second = await Api.Expect(await api.Post("/api/v1/auth/refresh", null, new { refreshToken = first.RefreshToken }), HttpStatusCode.OK);
        var newRefresh = second.GetProperty("refreshToken").GetString();
        var newAccess = second.GetProperty("accessToken").GetString();

        // Reapresentar o token antigo (roubado) => 401 e revogação da família inteira.
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Post("/api/v1/auth/refresh", null, new { refreshToken = first.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Post("/api/v1/auth/refresh", null, new { refreshToken = newRefresh })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/api/v1/me", newAccess)).StatusCode);
        Assert.True(await f.WithDb(db => db.AuditLogs.AnyAsync(a => a.Action == "security.refresh_reuse" && a.TenantId == T.Id)));
    }

    [Fact]
    public async Task Access_token_antigo_invalido_apos_rotacao_e_logout()
    {
        var login = await api.LoginFull(T.Code, T.OwnerEmail, T.OwnerPassword);
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/me", login.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Post("/api/v1/auth/logout", login.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/api/v1/me", login.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Token_expirado_adulterado_ou_de_outro_publico_e_recusado()
    {
        var me = await Api.Expect(await api.Get("/api/v1/me", T.OwnerToken), HttpStatusCode.OK);
        var key = new SymmetricSecurityKey(Convert.FromBase64String(ApiFactory.TenantJwtKey));
        var session = await f.WithDb(db => db.UserSessions.IgnoreQueryFilters().Where(s => s.TenantId == T.Id && s.RevokedAt == null).Select(s => new { s.Id, s.UserId }).FirstAsync());
        string Make(DateTime exp, string aud) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TokenService.Issuer, Audience = aud, NotBefore = exp.AddHours(-1), Expires = exp, IssuedAt = exp.AddHours(-1),
            Claims = new Dictionary<string, object> { ["sub"] = session.UserId.ToString(), ["tid"] = T.Id.ToString(), ["sid"] = session.Id.ToString() },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/me", Make(DateTime.UtcNow.AddMinutes(5), TokenService.TenantAudience))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/api/v1/me", Make(DateTime.UtcNow.AddMinutes(-5), TokenService.TenantAudience))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/api/v1/me", Make(DateTime.UtcNow.AddMinutes(5), TokenService.AdminAudience))).StatusCode);

        // Troca do tenant no payload sem reassinar => assinatura inválida.
        var parts = T.OwnerToken.Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1])).Replace(T.Id.ToString(), Guid.NewGuid().ToString());
        var forged = parts[0] + "." + Base64UrlEncoder.Encode(payload) + "." + parts[2];
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/api/v1/me", forged)).StatusCode);
    }

    [Fact]
    public async Task Usuario_bloqueado_perde_acesso_imediatamente()
    {
        var (id, token, _) = await api.CreateUser(T, "operator");
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/me", token)).StatusCode);
        await Api.Expect(await api.Post($"/api/v1/users/{id}/block", T.OwnerToken), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/api/v1/me", token)).StatusCode);
    }

    [Fact]
    public async Task Dispositivo_revogado_nao_loga_nao_sincroniza_e_nao_reativa()
    {
        var device = await api.RegisterDevice(T.OwnerToken, "PDV revogado");
        var token = await api.Login(T.Code, T.OwnerEmail, T.OwnerPassword, device);
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/license/grant", token)).StatusCode);

        await Api.Expect(await api.Post($"/api/v1/devices/{device}/revoke", T.OwnerToken), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/api/v1/license/grant", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Post("/api/v1/sync/batch", token, new { deviceId = device, deviceClock = DateTimeOffset.UtcNow, operations = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Post("/api/v1/auth/login", null, new { tenantCode = T.Code, email = T.OwnerEmail, password = T.OwnerPassword, deviceId = device })).StatusCode);
    }

    [Fact]
    public async Task Limite_de_dispositivos_do_plano_e_respeitado()
    {
        // Plano profissional: 3 dispositivos.
        var t = await api.CreateTenant("dev-" + Guid.NewGuid().ToString("N")[..6]);
        for (var i = 0; i < 3; i++) await api.RegisterDevice(t.OwnerToken, "PDV " + i);
        var res = await api.Post("/api/v1/devices", t.OwnerToken, new { name = "PDV extra", platform = "windows" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("device_limit", await Api.Code(res));
    }

    [Fact]
    public async Task Painel_admin_recusa_sem_token_com_token_de_loja_e_admin_sem_mfa()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/admin/v1/tenants", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/admin/v1/tenants", T.OwnerToken)).StatusCode);
        var noMfa = await api.Post("/admin/v1/auth/login", null, new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword, totpCode = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, noMfa.StatusCode);
        var wrongPwd = await api.Post("/admin/v1/auth/login", null, new { email = ApiFactory.AdminEmail, password = "errada-12345", totpCode = Api.TotpNow() });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPwd.StatusCode);
    }

    [Fact]
    public async Task Codigo_TOTP_nao_pode_ser_reutilizado_no_login()
    {
        await api.AdminToken();
        // Usa o código do PRÓXIMO passo (aceito pela janela de ±30s), depois tenta reutilizá-lo.
        var next = Totp.Code(ApiFactory.TotpSecret, Totp.CurrentStep(DateTimeOffset.UtcNow) + 1);
        var body = new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword, totpCode = next };
        Assert.Equal(HttpStatusCode.OK, (await api.Post("/admin/v1/auth/login", null, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Post("/admin/v1/auth/login", null, body)).StatusCode);
    }

    [Fact]
    public async Task Token_admin_nao_acessa_api_de_loja_sem_sessao_de_suporte()
    {
        var admin = await api.AdminToken();
        var res = await api.Get("/api/v1/products", admin);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("support_session_required", await Api.Code(res));
    }

    [Fact]
    public async Task Senha_fraca_e_recusada()
    {
        var res = await api.Post("/api/v1/users", T.OwnerToken, new { email = "fraca@x.test", displayName = "x", password = "123", roles = new[] { "operator" } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var stored = await f.WithDb(db => db.Users.IgnoreQueryFilters().Where(u => u.TenantId == T.Id).Select(u => u.PasswordHash).FirstAsync());
        Assert.StartsWith("argon2id$", stored);
    }

    [Fact]
    public async Task Erros_nao_expoem_detalhes_internos()
    {
        var res = await api.Send(HttpMethod.Post, "/api/v1/products", T.OwnerToken, null, null);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True((int)res.StatusCode is >= 400 and < 500, text);
        Assert.DoesNotContain("   at ", text);
        Assert.DoesNotContain("SELECT", text, StringComparison.OrdinalIgnoreCase);
    }
}

public class RateLimitFactory : ApiFactory
{
    protected override int AuthRateLimit => 5;
}

public class RateLimitTests(RateLimitFactory f) : IClassFixture<RateLimitFactory>
{
    [Fact]
    public async Task Excesso_de_tentativas_de_login_retorna_429()
    {
        var api = new Api(f);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
            codes.Add((await api.Post("/api/v1/auth/login", null, new { tenantCode = "x", email = "a@b.c", password = "errada-1234" })).StatusCode);
        Assert.Contains((HttpStatusCode)429, codes);
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Comercial.Api.Services;
using Comercial.Domain;
using Comercial.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Comercial.Tests;

public sealed class FakeEmailTransport : IEmailTransport
{
    public ConcurrentBag<EmailMessage> Sent { get; } = [];
    public bool Fail { get; set; }
    public Task SendAsync(SmtpConfig? config, EmailMessage m, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("smtp indisponível");
        Sent.Add(m);
        return Task.CompletedTask;
    }
}

public class EmailFactory : ApiFactory
{
    public FakeEmailTransport Mail { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        base.ConfigureWebHost(b);
        b.ConfigureTestServices(s => s.AddSingleton<IEmailTransport>(Mail));
    }

    public async Task Flush() => await WithScope(sp => sp.GetRequiredService<EmailSender>().SendPendingAsync());
}

public class EmailTests(EmailFactory f) : IClassFixture<EmailFactory>
{
    private readonly Api api = new(f);

    private string CodeFor(string to) =>
        Regex.Match(f.Mail.Sent.Where(m => m.To == to && m.Kind == "password_reset").OrderBy(m => m.CreatedAt).Last().HtmlBody, @"[A-Z2-9]{4}-[A-Z2-9]{4}").Value;

    [Fact]
    public async Task Esqueci_minha_senha_por_codigo_de_uso_unico()
    {
        var t = await api.CreateTenant("em-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal(HttpStatusCode.Accepted, (await api.Post("/api/v1/auth/forgot", null, new { tenantCode = t.Code, email = "ninguem@x.test" })).StatusCode); // não revela
        await Api.Expect(await api.Post("/api/v1/auth/forgot", null, new { tenantCode = t.Code, email = t.OwnerEmail }), HttpStatusCode.Accepted);
        await f.Flush();
        var code = CodeFor(t.OwnerEmail);
        Assert.Matches(@"^[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        Assert.DoesNotContain(f.Mail.Sent, m => m.To == "ninguem@x.test");
        Assert.False(await f.WithDb(db => db.PasswordResetTokens.IgnoreQueryFilters().AnyAsync(x => x.CodeHash.Contains(code.Replace("-", ""))))); // só o hash

        Assert.Equal("invalid_code", await Api.Code(await api.Post("/api/v1/auth/reset", null, new { tenantCode = t.Code, email = t.OwnerEmail, code = "AAAA-BBBB", newPassword = "Nova-Senha-123" })));
        Assert.Equal("weak_password", await Api.Code(await api.Post("/api/v1/auth/reset", null, new { tenantCode = t.Code, email = t.OwnerEmail, code, newPassword = "123" })));
        await Api.Expect(await api.Post("/api/v1/auth/reset", null, new { tenantCode = t.Code, email = t.OwnerEmail, code, newPassword = "Nova-Senha-123" }), HttpStatusCode.NoContent);
        Assert.Equal("invalid_code", await Api.Code(await api.Post("/api/v1/auth/reset", null, new { tenantCode = t.Code, email = t.OwnerEmail, code, newPassword = "Outra-Senha-456" }))); // uso único

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/api/v1/me", t.OwnerToken)).StatusCode); // sessões antigas encerradas
        await api.Login(t.Code, t.OwnerEmail, "Nova-Senha-123");
        await f.Flush();
        Assert.Contains(f.Mail.Sent, m => m.To == t.OwnerEmail && m.Kind == "password_changed");
    }

    [Fact]
    public async Task Codigo_e_queimado_apos_cinco_tentativas_erradas()
    {
        var t = await api.CreateTenant("em2-" + Guid.NewGuid().ToString("N")[..5]);
        await Api.Expect(await api.Post("/api/v1/auth/forgot", null, new { tenantCode = t.Code, email = t.OwnerEmail }), HttpStatusCode.Accepted);
        await f.Flush();
        var code = CodeFor(t.OwnerEmail);
        for (var i = 0; i < 5; i++)
            Assert.Equal("invalid_code", await Api.Code(await api.Post("/api/v1/auth/reset", null, new { tenantCode = t.Code, email = t.OwnerEmail, code = "ZZZZ-ZZZZ", newPassword = "Nova-Senha-123" })));
        Assert.Equal("invalid_code", await Api.Code(await api.Post("/api/v1/auth/reset", null, new { tenantCode = t.Code, email = t.OwnerEmail, code, newPassword = "Nova-Senha-123" })));
    }

    [Fact]
    public async Task Funcionario_novo_recebe_boas_vindas_e_falha_de_smtp_tenta_de_novo()
    {
        var t = await api.CreateTenant("em3-" + Guid.NewGuid().ToString("N")[..5]);
        f.Mail.Fail = true;
        var (_, _, email) = await api.CreateUser(t, "operator");
        await f.Flush();
        var msg = await f.WithDb(db => db.EmailMessages.SingleAsync(m => m.To == email && m.Kind == "welcome"));
        Assert.Equal(EmailStatus.Pending, msg.Status);
        Assert.NotNull(msg.NextAttemptAt);
        Assert.DoesNotContain("Senha-Func-123", msg.HtmlBody); // a senha nunca vai por e-mail
        f.Mail.Fail = false;
        await f.WithDb(async db => { var m = await db.EmailMessages.SingleAsync(x => x.Id == msg.Id); m.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync(); });
        await f.Flush();
        Assert.Contains(f.Mail.Sent, m => m.To == email && m.Kind == "welcome" && m.HtmlBody.Contains(t.Code));
    }

    [Fact]
    public async Task Smtp_configurado_pelo_painel_guarda_senha_protegida()
    {
        var admin = await api.AdminToken();
        var v = await Api.Expect(await api.Put("/admin/v1/settings/smtp", admin, new { host = "smtp.exemplo.com", port = 587, ssl = true, user = "contato@exemplo.com", password = "senha-smtp-secreta", from = "contato@exemplo.com", fromName = "Sistema" }, api.Reauth()), HttpStatusCode.OK);
        Assert.True(v.GetProperty("configured").GetBoolean());
        Assert.DoesNotContain("senha-smtp-secreta", v.ToString());
        Assert.False(await f.WithDb(db => db.PlatformSettings.AnyAsync(s => s.Value!.Contains("senha-smtp-secreta"))));
        Assert.Equal("invalid_email", await Api.Code(await api.Put("/admin/v1/settings/smtp", admin, new { from = "invalido" }, api.Reauth())));
        var test = await Api.Expect(await api.Post("/admin/v1/settings/smtp/test", admin, new { to = "eu@exemplo.com" }), HttpStatusCode.OK);
        Assert.True(test.GetProperty("ok").GetBoolean());
    }
}

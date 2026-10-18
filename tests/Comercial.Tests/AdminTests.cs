using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Comercial.Api.Security;
using Comercial.Api.Services;
using Comercial.Domain;
using Comercial.Domain.Security;
using Comercial.Shared.Branding;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Comercial.Tests;

/// <summary>Painel do desenvolvedor: módulos, planos, suporte, identidade visual, pagamentos e auditoria.</summary>
public class AdminTests(ApiFactory f) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly Api api = new(f);
    private TenantInfo T = null!;
    private string admin = null!;

    public async Task InitializeAsync()
    {
        T = await api.CreateTenant("adm-" + Guid.NewGuid().ToString("N")[..6]);
        admin = await api.AdminToken();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private object Change(string reason = "ajuste comercial") => new { confirmTenantCode = T.Code, reason };

    // ---------------- Módulos ----------------

    [Fact]
    public async Task Alterar_modulo_exige_reautenticacao_mfa_e_codigo_da_empresa()
    {
        Assert.Equal("reauth_required", await Api.Code(await api.Post($"/admin/v1/tenants/{T.Id}/modules/fiscal/disable", admin, Change())));
        var wrong = await api.Post($"/admin/v1/tenants/{T.Id}/modules/fiscal/disable", admin, new { confirmTenantCode = "outra-empresa", reason = "ajuste comercial" }, api.Reauth());
        Assert.Equal("confirmation_mismatch", await Api.Code(wrong));
    }

    [Fact]
    public async Task Desativar_modulo_com_dependentes_e_bloqueado_e_impacto_explica()
    {
        var impact = await Api.Expect(await api.Get($"/admin/v1/tenants/{T.Id}/modules/estoque/impact?enable=false", admin), HttpStatusCode.OK);
        Assert.False(impact.GetProperty("allowed").GetBoolean());
        Assert.Contains("pdv", impact.GetProperty("dependentModulesEnabled").EnumerateArray().Select(x => x.GetString()));
        var res = await api.Post($"/admin/v1/tenants/{T.Id}/modules/estoque/disable", admin, Change(), api.Reauth());
        Assert.Equal("module_change_blocked", await Api.Code(res));
    }

    [Fact]
    public async Task Modulo_planejado_nao_pode_ser_habilitado()
    {
        var res = await api.Post($"/admin/v1/tenants/{T.Id}/modules/ecommerce/enable", admin, Change(), api.Reauth());
        Assert.Equal("module_change_blocked", await Api.Code(res));
    }

    [Fact]
    public async Task Desativar_modulo_bloqueia_funcao_e_preserva_historico()
    {
        var t = await api.CreateTenant("mod-" + Guid.NewGuid().ToString("N")[..6]);
        var p = await api.CreateProduct(t.OwnerToken, "M1");
        var c = await api.OpenCash(t.OwnerToken, t.StoreId);
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, c, p)), HttpStatusCode.Created);
        var docs = await Api.Expect(await api.Get("/api/v1/fiscal/documents", t.OwnerToken), HttpStatusCode.OK);
        Assert.Single(docs.EnumerateArray());

        // Fiscal tem documento pendente: a desativação deve ser bloqueada até resolver.
        var blocked = await api.Post($"/admin/v1/tenants/{t.Id}/modules/fiscal/disable", admin, new { confirmTenantCode = t.Code, reason = "cliente cancelou fiscal" }, api.Reauth());
        Assert.Equal("module_change_blocked", await Api.Code(blocked));

        // App do proprietário não tem dependentes nem pendências.
        await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/modules/app_proprietario/disable", admin, new { confirmTenantCode = t.Code, reason = "cliente cancelou app" }, api.Reauth()), HttpStatusCode.OK);
        Assert.Equal("module_not_enabled", await Api.Code(await api.Get("/api/v1/mobile/dashboard", t.OwnerToken)));
        // Dados de vendas continuam no banco e consultáveis.
        Assert.Equal(1, await f.WithDb(db => db.Sales.IgnoreQueryFilters().CountAsync(s => s.TenantId == t.Id)));
        Assert.True(await f.WithDb(db => db.AuditLogs.AnyAsync(a => a.Action == "module.disabled" && a.TenantId == t.Id)));

        // Reativar devolve o acesso.
        await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/modules/app_proprietario/enable", admin, new { confirmTenantCode = t.Code, reason = "cliente recontratou" }, api.Reauth()), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/mobile/dashboard", t.OwnerToken)).StatusCode);
    }

    [Fact]
    public async Task Modulo_com_vigencia_expirada_deixa_de_valer()
    {
        var t = await api.CreateTenant("vig-" + Guid.NewGuid().ToString("N")[..6]);
        await f.WithDb(async db =>
        {
            var m = await db.TenantModules.IgnoreQueryFilters().FirstAsync(x => x.TenantId == t.Id && x.ModuleKey == "app_proprietario");
            m.ValidUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });
        Assert.Equal("module_not_enabled", await Api.Code(await api.Get("/api/v1/mobile/dashboard", t.OwnerToken)));
    }

    [Fact]
    public async Task Admin_de_suporte_nao_altera_modulos_nem_planos()
    {
        var (supportToken, _) = await CreateSupportAdmin();
        Assert.Equal("admin_role_required", await Api.Code(await api.Post($"/admin/v1/tenants/{T.Id}/modules/fiscal/disable", supportToken, Change())));
        Assert.Equal("admin_role_required", await Api.Code(await api.Put("/admin/v1/plans", supportToken, new { code = "x", name = "x", priceCents = 1, period = 1, maxUsers = 1, maxDevices = 1, maxStores = 1, graceDays = 1, modules = Array.Empty<string>() })));
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/admin/v1/tenants", supportToken)).StatusCode);
    }

    // ---------------- Planos ----------------

    [Fact]
    public async Task Alterar_preco_do_plano_nao_altera_assinatura_existente()
    {
        var code = "plano-" + Guid.NewGuid().ToString("N")[..6];
        var plan = new { code, name = "Teste", priceCents = 5000, period = 1, maxUsers = 2, maxDevices = 1, maxStores = 1, graceDays = 5, modules = new[] { "produtos", "estoque", "caixa", "pdv" } };
        await Api.Expect(await api.Put("/admin/v1/plans", admin, plan, api.Reauth()), HttpStatusCode.OK);
        var t = await Api.Expect(await api.Post("/admin/v1/tenants", admin, new
        {
            code = "snap-" + Guid.NewGuid().ToString("N")[..6], legalName = "Snap LTDA", tradeName = "Snap", planCode = code, storeName = "Matriz",
            owner = new { email = "dono@snap.test", displayName = "Dono", password = "Senha-Dono-123" },
        }), HttpStatusCode.Created);
        await Api.Expect(await api.Put("/admin/v1/plans", admin, plan with { priceCents = 9999 }, api.Reauth()), HttpStatusCode.OK);
        var price = await f.WithDb(db => db.Subscriptions.IgnoreQueryFilters().Where(s => s.TenantId == t.GetProperty("id").GetGuid()).Select(s => s.PriceCents).FirstAsync());
        Assert.Equal(5000, price);
    }

    [Fact]
    public async Task Plano_com_dependencia_faltando_e_recusado()
    {
        var res = await api.Put("/admin/v1/plans", admin, new { code = "invalido", name = "x", priceCents = 1, period = 1, maxUsers = 1, maxDevices = 1, maxStores = 1, graceDays = 1, modules = new[] { "pdv" } }, api.Reauth());
        Assert.Equal("plan_dependency", await Api.Code(res));
    }

    // ---------------- Suporte ----------------

    [Fact]
    public async Task Suporte_le_em_modo_consulta_e_nao_altera_sem_elevacao()
    {
        var product = await api.CreateProduct(T.OwnerToken, "SUP1");
        var session = await Api.Expect(await api.Post($"/admin/v1/tenants/{T.Id}/support-sessions", admin, new { reason = "cliente relata erro de sync", durationMinutes = 30 }), HttpStatusCode.OK);
        var h = new Dictionary<string, string> { ["X-Support-Session"] = session.GetProperty("id").GetString()! };

        Assert.Equal(HttpStatusCode.OK, (await api.Get($"/api/v1/products/{product}", admin, h)).StatusCode);
        Assert.Equal("support_scope", await Api.Code(await api.Put($"/api/v1/products/{product}", admin, new { sku = "SUP1", name = "alterado", unit = "UN", priceCents = 1, costCents = 1, minStock = 0 }, h)));
        Assert.Equal("support_scope", await Api.Code(await api.Get("/api/v1/sales", admin, h)));
        Assert.Equal("support_scope", await Api.Code(await api.Post("/api/v1/cash/sessions", admin, new { storeId = T.StoreId, openingCents = 0 }, h)));
        var diag = await Api.Expect(await api.Get($"/admin/v1/support-sessions/{session.GetProperty("id").GetString()}/diagnostics", admin), HttpStatusCode.OK);
        Assert.Equal("active", diag.GetProperty("license").GetString());
    }

    [Fact]
    public async Task Elevacao_exige_mfa_aprovacao_do_cliente_e_respeita_escopo_e_revogacao()
    {
        var product = await api.CreateProduct(T.OwnerToken, "SUP2");
        var session = await Api.Expect(await api.Post($"/admin/v1/tenants/{T.Id}/support-sessions", admin, new { reason = "corrigir cadastro de produto", durationMinutes = 30 }), HttpStatusCode.OK);
        var sid = session.GetProperty("id").GetString()!;
        var h = new Dictionary<string, string> { ["X-Support-Session"] = sid };
        var edit = new { sku = "SUP2", name = "corrigido", unit = "UN", priceCents = 1000, costCents = 600, minStock = 0 };

        Assert.Equal("reauth_required", await Api.Code(await api.Post($"/admin/v1/support-sessions/{sid}/elevation-requests", admin, new { reason = "corrigir nome do produto", operations = new[] { "products.write" }, durationMinutes = 10, totpCode = "000000" })));
        Assert.Equal("operation_not_elevatable", await Api.Code(await api.Post($"/admin/v1/support-sessions/{sid}/elevation-requests", admin, new { reason = "quero vender pelo cliente", operations = new[] { "sales.create" }, durationMinutes = 10, totpCode = Api.TotpNow() })));
        var req = await Api.Expect(await api.Post($"/admin/v1/support-sessions/{sid}/elevation-requests", admin, new { reason = "corrigir nome do produto", operations = new[] { "products.write" }, durationMinutes = 10, totpCode = Api.TotpNow() }), HttpStatusCode.OK);

        // Antes da aprovação, continua só consulta.
        Assert.Equal("support_scope", await Api.Code(await api.Put($"/api/v1/products/{product}", admin, edit, h)));
        // O próprio suporte não pode aprovar.
        Assert.Equal("support_scope", await Api.Code(await api.Post($"/api/v1/support/elevation-requests/{req.GetProperty("id").GetString()}/approve", admin, null, h)));
        // Operador da loja também não.
        var (_, op, _) = await api.CreateUser(T, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post($"/api/v1/support/elevation-requests/{req.GetProperty("id").GetString()}/approve", op)).StatusCode);

        await Api.Expect(await api.Post($"/api/v1/support/elevation-requests/{req.GetProperty("id").GetString()}/approve", T.OwnerToken), HttpStatusCode.OK);
        await Api.Expect(await api.Put($"/api/v1/products/{product}", admin, edit, h), HttpStatusCode.OK);
        // Elevação não libera o que não foi pedido.
        Assert.Equal("support_scope", await Api.Code(await api.Post("/api/v1/stock/adjustments", admin, new { storeId = T.StoreId, productId = product, quantity = 1, reason = "teste suporte" }, h)));

        var audit = await f.WithDb(db => db.AuditLogs.Where(a => a.Action == "product.updated" && a.EntityId == product.ToString()).FirstAsync());
        Assert.Equal(ActorType.Support, audit.ActorType);
        Assert.Equal(Guid.Parse(sid), audit.SupportSessionId);

        // A empresa revoga: suporte perde acesso imediatamente.
        await Api.Expect(await api.Post($"/api/v1/support/sessions/{sid}/revoke", T.OwnerToken), HttpStatusCode.NoContent);
        Assert.Equal("support_session_required", await Api.Code(await api.Get($"/api/v1/products/{product}", admin, h)));
    }

    [Fact]
    public async Task Sessao_de_suporte_expirada_ou_de_outro_admin_e_recusada()
    {
        var session = await Api.Expect(await api.Post($"/admin/v1/tenants/{T.Id}/support-sessions", admin, new { reason = "verificar sincronização", durationMinutes = 5 }), HttpStatusCode.OK);
        var sid = session.GetProperty("id").GetGuid();
        var h = new Dictionary<string, string> { ["X-Support-Session"] = sid.ToString() };

        var (other, _) = await CreateSupportAdmin();
        Assert.Equal("support_session_required", await Api.Code(await api.Get("/api/v1/products", other, h)));

        await f.WithDb(async db =>
        {
            var s = await db.SupportSessions.FirstAsync(x => x.Id == sid);
            s.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        });
        Assert.Equal("support_session_required", await Api.Code(await api.Get("/api/v1/products", admin, h)));
    }

    // ---------------- Identidade visual ----------------

    [Fact]
    public async Task Identidade_rejeita_cores_invalidas_scripts_e_baixo_contraste()
    {
        var cfg = BrandingRules.Preset("moderno");
        cfg.SystemName = "<script>alert(1)</script>";
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/admin/v1/tenants/{T.Id}/branding/draft", admin, cfg)).StatusCode);
        cfg = BrandingRules.Preset("moderno"); cfg.PrimaryColor = "red; background:url(x)";
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/admin/v1/tenants/{T.Id}/branding/draft", admin, cfg)).StatusCode);
        cfg = BrandingRules.Preset("moderno"); cfg.TextColor = "#EEEEEE"; cfg.BackgroundColor = "#FFFFFF";
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/admin/v1/tenants/{T.Id}/branding/draft", admin, cfg)).StatusCode);
        cfg = BrandingRules.Preset("moderno"); cfg.FontFamily = "Comic Sans Hack";
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/admin/v1/tenants/{T.Id}/branding/draft", admin, cfg)).StatusCode);
        foreach (var theme in BrandingRules.Themes) Assert.Empty(BrandingRules.Validate(BrandingRules.Preset(theme)));
    }

    [Fact]
    public async Task Previa_nao_altera_publicacao_e_versoes_podem_ser_restauradas()
    {
        var cfg = BrandingRules.Preset("vibrante"); cfg.SystemName = "Versão 1";
        await Api.Expect(await api.Put($"/admin/v1/tenants/{T.Id}/branding/draft", admin, cfg), HttpStatusCode.OK);
        var v1 = await Api.Expect(await api.Post($"/admin/v1/tenants/{T.Id}/branding/publish", admin, new { confirmTenantCode = T.Code, note = "v1" }), HttpStatusCode.OK);

        var preview = BrandingRules.Preset("elegante"); preview.SystemName = "Prévia";
        await Api.Expect(await api.Post($"/admin/v1/tenants/{T.Id}/branding/preview", admin, preview), HttpStatusCode.OK);
        var live = await Api.Expect(await api.Get("/api/v1/branding", T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal("Versão 1", live.GetProperty("config").GetProperty("systemName").GetString());

        cfg.SystemName = "Versão 2";
        await Api.Expect(await api.Put($"/admin/v1/tenants/{T.Id}/branding/draft", admin, cfg), HttpStatusCode.OK);
        await Api.Expect(await api.Post($"/admin/v1/tenants/{T.Id}/branding/publish", admin, new { confirmTenantCode = T.Code, note = "v2" }), HttpStatusCode.OK);
        await Api.Expect(await api.Post($"/admin/v1/tenants/{T.Id}/branding/versions/{v1.GetProperty("version").GetInt32()}/restore", admin, new { confirmTenantCode = T.Code, note = "volta" }), HttpStatusCode.OK);
        live = await Api.Expect(await api.Get("/api/v1/branding", T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal("Versão 1", live.GetProperty("config").GetProperty("systemName").GetString());
        var versions = await Api.Expect(await api.Get($"/admin/v1/tenants/{T.Id}/branding/versions", admin), HttpStatusCode.OK);
        Assert.True(versions.GetArrayLength() >= 3); // histórico preservado

        await Api.Expect(await api.Post($"/admin/v1/tenants/{T.Id}/branding/reset", admin, new { confirmTenantCode = T.Code }), HttpStatusCode.NoContent);
        live = await Api.Expect(await api.Get("/api/v1/branding", T.OwnerToken), HttpStatusCode.OK);
        Assert.False(live.GetProperty("custom").GetBoolean());
    }

    [Fact]
    public async Task Sem_modulo_white_label_cliente_recebe_identidade_padrao()
    {
        var basic = await api.CreateTenant("wl-" + Guid.NewGuid().ToString("N")[..6], "basico");
        var cfg = BrandingRules.Preset("vibrante"); cfg.SystemName = "Não deveria aparecer";
        await Api.Expect(await api.Put($"/admin/v1/tenants/{basic.Id}/branding/draft", admin, cfg), HttpStatusCode.OK);
        await Api.Expect(await api.Post($"/admin/v1/tenants/{basic.Id}/branding/publish", admin, new { confirmTenantCode = basic.Code, note = "x" }), HttpStatusCode.OK);
        var live = await Api.Expect(await api.Get("/api/v1/branding", basic.OwnerToken), HttpStatusCode.OK);
        Assert.False(live.GetProperty("custom").GetBoolean());
        // Identidade visual não concede módulos.
        var me = await Api.Expect(await api.Get("/api/v1/me", basic.OwnerToken), HttpStatusCode.OK);
        Assert.DoesNotContain("white_label", me.GetProperty("modules").EnumerateArray().Select(x => x.GetString()));
    }

    [Theory]
    [InlineData("shell.php.png", "<?php system($_GET['c']); ?>")]
    [InlineData("logo.svg", "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>")]
    [InlineData("app.png", "MZ\u0090\0executavel")]
    public async Task Upload_malicioso_e_recusado(string name, string content)
    {
        var res = await Upload(Encoding.UTF8.GetBytes(content), name, "image/png");
        Assert.Equal((HttpStatusCode)415, res.StatusCode);
    }

    [Fact]
    public async Task Upload_valida_tamanho_dimensoes_e_aceita_png_real()
    {
        Assert.Equal(HttpStatusCode.OK, (await Upload(TestImages.Png(128, 64), "logo.png", "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(TestImages.Png(8, 8), "mini.png", "image/png")).StatusCode);
        var big = new byte[2 * 1024 * 1024 + 10];
        TestImages.Png(32, 32).CopyTo(big, 0);
        var tooBig = await Upload(big, "grande.png", "image/png");
        Assert.True(tooBig.StatusCode is (HttpStatusCode)413 or HttpStatusCode.BadRequest, tooBig.StatusCode.ToString());
        // PNG truncado (sem IEND) é recusado.
        var truncated = TestImages.Png(64, 64)[..^12];
        Assert.Equal((HttpStatusCode)415, (await Upload(truncated, "trunc.png", "image/png")).StatusCode);
        // Nome salvo nunca é o enviado.
        var stored = await f.WithDb(db => db.BrandingAssets.IgnoreQueryFilters().Where(a => a.TenantId == T.Id).Select(a => a.StorageName).ToListAsync());
        Assert.All(stored, s => Assert.Matches("^[0-9a-f]{32}\\.bin$", s));
    }

    private async Task<HttpResponseMessage> Upload(byte[] bytes, string name, string type)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("logo"), "kind");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", name);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/admin/v1/tenants/{T.Id}/branding/assets") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        return await api.Http.SendAsync(req);
    }

    // ---------------- Pagamentos ----------------

    private async Task<HttpResponseMessage> Webhook(object payload, string? secret = ApiFactory.WebhookSecret, long? ts = null)
    {
        var body = JsonSerializer.Serialize(payload, Api.Json);
        var t = (ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/payments/generic") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Webhook-Timestamp", t);
        req.Headers.Add("X-Webhook-Signature", PaymentWebhookService.Sign(secret ?? "x", t, body));
        return await api.Http.SendAsync(req);
    }

    [Fact]
    public async Task Webhook_de_pagamento_valida_assinatura_replay_valor_e_idempotencia()
    {
        var t = await api.CreateTenant("pay-" + Guid.NewGuid().ToString("N")[..6]);
        await f.WithDb(async db =>
        {
            var sub = await db.Subscriptions.IgnoreQueryFilters().FirstAsync(s => s.TenantId == t.Id);
            sub.CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(-20); // restrita
            await db.SaveChangesAsync();
        });
        Assert.Equal("restricted", (await Api.Expect(await api.Get("/api/v1/me", t.OwnerToken), HttpStatusCode.OK)).GetProperty("license").GetString());
        var inv = await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/invoices", admin, new { dueInDays = 3 }), HttpStatusCode.OK);
        var invoiceId = inv.GetProperty("id").GetGuid();
        var amount = inv.GetProperty("amountCents").GetInt64();
        var periodEnd = inv.GetProperty("periodEnd").GetDateTimeOffset();
        // Simula fatura cobrindo período atual (cliente pagou o atraso).
        await f.WithDb(async db =>
        {
            var i = await db.Invoices.IgnoreQueryFilters().FirstAsync(x => x.Id == invoiceId);
            i.PeriodEnd = DateTimeOffset.UtcNow.AddDays(30);
            await db.SaveChangesAsync();
        });

        var evt = new { eventId = "evt-" + Guid.NewGuid().ToString("N"), type = "invoice.paid", invoiceId, amountCents = amount };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Webhook(evt, "segredo-errado")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Webhook(evt, ts: DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds())).StatusCode);
        var wrongAmount = await Api.Expect(await Webhook(evt with { eventId = "evt-x" + Guid.NewGuid().ToString("N"), amountCents = 1 }), HttpStatusCode.OK);
        Assert.Equal("amount_mismatch", wrongAmount.GetProperty("result").GetString());
        Assert.Equal("restricted", (await Api.Expect(await api.Get("/api/v1/me", t.OwnerToken), HttpStatusCode.OK)).GetProperty("license").GetString());

        var ok = await Api.Expect(await Webhook(evt), HttpStatusCode.OK);
        Assert.Equal("applied", ok.GetProperty("result").GetString());
        var dup = await Api.Expect(await Webhook(evt), HttpStatusCode.OK);
        Assert.Equal("duplicate", dup.GetProperty("result").GetString());
        Assert.Equal("active", (await Api.Expect(await api.Get("/api/v1/me", t.OwnerToken), HttpStatusCode.OK)).GetProperty("license").GetString());
        Assert.Equal(1, await f.WithDb(db => db.PaymentEvents.CountAsync(e => e.ProviderEventId == evt.eventId)));
        _ = periodEnd;
    }

    // ---------------- Auditoria ----------------

    [Fact]
    public async Task Auditoria_e_imutavel_e_adulteracao_direta_e_detectada()
    {
        await api.CreateProduct(T.OwnerToken, "AUD1");
        var verify = await Api.Expect(await api.Get("/admin/v1/audit/verify", admin), HttpStatusCode.OK);
        Assert.True(verify.GetProperty("ok").GetBoolean(), verify.GetRawText());

        await f.WithDb(async db =>
        {
            var a = await db.AuditLogs.OrderBy(x => x.Id).FirstAsync();
            a.Action = "adulterado";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        });
        await f.WithDb(async db =>
        {
            var a = await db.AuditLogs.OrderBy(x => x.Id).FirstAsync();
            db.AuditLogs.Remove(a);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        });
    }

    private async Task<(string Token, Guid Id)> CreateSupportAdmin()
    {
        var secret = Totp.NewSecret();
        var email = $"suporte-{Guid.NewGuid():N}@saas.test";
        var id = await f.WithDb(async db =>
        {
            using var scope = f.Services.CreateScope();
            var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("admin-totp-v1");
            var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
            var a = new AdminUser { Email = email, DisplayName = "Suporte", Role = AdminRoles.Support, PasswordHash = hasher.Hash("Senha-Suporte-1"), TotpSecretProtected = protector.Protect(secret) };
            db.AdminUsers.Add(a);
            await db.SaveChangesAsync();
            return a.Id;
        });
        var res = await api.Post("/admin/v1/auth/login", null, new { email, password = "Senha-Suporte-1", totpCode = Totp.Code(secret, Totp.CurrentStep(DateTimeOffset.UtcNow)) });
        var body = await Api.Expect(res, HttpStatusCode.OK);
        return (body.GetProperty("accessToken").GetString()!, id);
    }
}

/// <summary>Teste isolado (fábrica própria) porque altera diretamente o banco para simular adulteração.</summary>
public class AuditTamperTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Alteracao_por_SQL_direto_quebra_a_cadeia_de_hash()
    {
        var api = new Api(f);
        await api.CreateTenant("tamper-" + Guid.NewGuid().ToString("N")[..6]);
        var admin = await api.AdminToken();
        Assert.True((await Api.Expect(await api.Get("/admin/v1/audit/verify", admin), HttpStatusCode.OK)).GetProperty("ok").GetBoolean());
        const string tamper = "UPDATE audit_logs SET \"DataJson\" = 'forjado' WHERE \"Id\" = ";
        var id = await f.WithDb(db => db.AuditLogs.OrderBy(a => a.Id).Skip(1).Select(a => a.Id).FirstAsync());
        if (f.Postgres is { } pg)
        {
            // O usuário da aplicação não consegue alterar a auditoria (permissão revogada + trigger).
            await f.WithDb(async db => await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() =>
                db.Database.ExecuteSqlAsync($"UPDATE audit_logs SET \"DataJson\" = 'forjado' WHERE \"Id\" = {id}")));
            // Nem o dono do banco sem desligar o trigger.
            Assert.Throws<Npgsql.PostgresException>(() => PostgresTestDb.Exec(pg.OwnerConnectionString, tamper + id));
            // Atacante com acesso de DBA desliga o trigger e adultera: a verificação da cadeia precisa detectar.
            PostgresTestDb.Exec(pg.OwnerConnectionString,
                $"ALTER TABLE audit_logs DISABLE TRIGGER trg_audit_logs_immutable; {tamper}{id}; ALTER TABLE audit_logs ENABLE TRIGGER trg_audit_logs_immutable;");
        }
        else
        {
            await f.WithDb(db => db.Database.ExecuteSqlAsync($"UPDATE audit_logs SET \"DataJson\" = 'forjado' WHERE \"Id\" = {id}"));
        }
        var after = await Api.Expect(await api.Get("/admin/v1/audit/verify", admin), HttpStatusCode.OK);
        Assert.False(after.GetProperty("ok").GetBoolean());
    }
}

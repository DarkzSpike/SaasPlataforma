using System.Net;
using Comercial.Domain;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Tests;

/// <summary>Permissões, escalada de privilégio, licença e módulos validados no servidor.</summary>
public class AuthorizationTests(ApiFactory f) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly Api api = new(f);
    private TenantInfo T = null!;

    public async Task InitializeAsync() => T = await api.CreateTenant("authz-" + Guid.NewGuid().ToString("N")[..6]);
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Operador_nao_cria_usuario_nem_ajusta_estoque_nem_altera_produto()
    {
        var (_, op, _) = await api.CreateUser(T, "operator");
        var product = await api.CreateProduct(T.OwnerToken, "P1");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post("/api/v1/users", op, new { email = "x@y.z", displayName = "x", password = "Senha-Func-123", roles = new[] { "operator" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post("/api/v1/stock/adjustments", op, new { storeId = T.StoreId, productId = product, quantity = 5, reason = "teste de ajuste" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Put($"/api/v1/products/{product}", op, new { sku = "P1", name = "Hack", unit = "UN", priceCents = 1, costCents = 1, minStock = 0 })).StatusCode);
    }

    [Fact]
    public async Task Gerente_nao_concede_papel_de_dono_nem_permissao_que_nao_tem()
    {
        var (_, manager, _) = await api.CreateUser(T, "manager");
        var res = await api.Post("/api/v1/users", manager, new { email = "novo-dono@x.test", displayName = "x", password = "Senha-Func-123", roles = new[] { "owner" } });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("privilege_escalation", await Api.Code(res));

        var (opId, _, _) = await api.CreateUser(T, "operator");
        // reports.financial é exclusiva de owner/finance.
        var grant = await api.Put($"/api/v1/users/{opId}/permissions", manager, new[] { new { permission = "reports.financial", granted = true } });
        Assert.Equal(HttpStatusCode.Forbidden, grant.StatusCode);
    }

    [Fact]
    public async Task Gerente_nao_gerencia_usuario_de_nivel_superior()
    {
        var (_, manager, _) = await api.CreateUser(T, "manager");
        var ownerId = await f.WithDb(db => db.Users.IgnoreQueryFilters().Where(u => u.TenantId == T.Id && u.Email == T.OwnerEmail).Select(u => u.Id).FirstAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post($"/api/v1/users/{ownerId}/block", manager)).StatusCode);
    }

    [Fact]
    public async Task Relatorio_financeiro_somente_com_permissao()
    {
        var (_, op, _) = await api.CreateUser(T, "operator");
        var (_, manager, _) = await api.CreateUser(T, "manager");
        var (_, finance, _) = await api.CreateUser(T, "finance");
        var url = $"/api/v1/reports/sales-summary?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}";
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Get(url, op)).StatusCode);
        var m = await Api.Expect(await api.Get(url, manager), HttpStatusCode.OK);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, m.GetProperty("grossProfitCents").ValueKind);
        var fi = await Api.Expect(await api.Get(url, finance), HttpStatusCode.OK);
        Assert.Equal(System.Text.Json.JsonValueKind.Number, fi.GetProperty("grossProfitCents").ValueKind);
    }

    [Fact]
    public async Task Modulo_nao_contratado_e_bloqueado_no_servidor_mesmo_com_permissao()
    {
        // Plano básico não inclui fiscal nem app do proprietário.
        var basic = await api.CreateTenant("basic-" + Guid.NewGuid().ToString("N")[..6], "basico");
        var fiscal = await api.Get("/api/v1/fiscal/documents", basic.OwnerToken);
        Assert.Equal(HttpStatusCode.Forbidden, fiscal.StatusCode);
        Assert.Equal("module_not_enabled", await Api.Code(fiscal));
        Assert.Equal("module_not_enabled", await Api.Code(await api.Get("/api/v1/mobile/dashboard", basic.OwnerToken)));
        Assert.True(await f.WithDb(db => db.AuditLogs.AnyAsync(a => a.Action == "security.feature_denied" && a.TenantId == basic.Id)));
    }

    [Fact]
    public async Task Licenca_vencida_apos_tolerancia_bloqueia_escrita_mas_preserva_consulta()
    {
        var t = await api.CreateTenant("lic-" + Guid.NewGuid().ToString("N")[..6]);
        var product = await api.CreateProduct(t.OwnerToken, "L1");
        await f.WithDb(async db =>
        {
            var sub = await db.Subscriptions.IgnoreQueryFilters().FirstAsync(s => s.TenantId == t.Id);
            sub.CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(-(sub.GraceDays + 1));
            await db.SaveChangesAsync();
        });
        var write = await api.Post("/api/v1/products", t.OwnerToken, new { sku = "L2", name = "x", unit = "UN", priceCents = 1, costCents = 1, minStock = 0 });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal("license_restricted", await Api.Code(write));
        Assert.Equal(HttpStatusCode.OK, (await api.Get($"/api/v1/products/{product}", t.OwnerToken)).StatusCode);
        var me = await Api.Expect(await api.Get("/api/v1/me", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal("restricted", me.GetProperty("license").GetString());
        // Dados nunca são apagados por inadimplência.
        Assert.True(await f.WithDb(db => db.Products.IgnoreQueryFilters().AnyAsync(p => p.Id == product)));
    }

    [Fact]
    public async Task Atraso_dentro_da_tolerancia_continua_operando()
    {
        var t = await api.CreateTenant("grace-" + Guid.NewGuid().ToString("N")[..6]);
        await f.WithDb(async db =>
        {
            var sub = await db.Subscriptions.IgnoreQueryFilters().FirstAsync(s => s.TenantId == t.Id);
            sub.CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(-2);
            await db.SaveChangesAsync();
        });
        var me = await Api.Expect(await api.Get("/api/v1/me", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal("grace", me.GetProperty("license").GetString());
        await api.CreateProduct(t.OwnerToken, "G1");
    }

    [Fact]
    public void Calculo_de_estado_de_licenca_separa_tolerancia_e_suspensao()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant();
        var sub = new Subscription { CurrentPeriodEnd = now.AddDays(30), GraceDays = 7 };
        Assert.Equal("active", Comercial.Api.Services.LicenseService.ComputeState(tenant, sub, now));
        sub.CurrentPeriodEnd = now.AddDays(2);
        Assert.Equal("expiring", Comercial.Api.Services.LicenseService.ComputeState(tenant, sub, now));
        sub.CurrentPeriodEnd = now.AddDays(-3);
        Assert.Equal("grace", Comercial.Api.Services.LicenseService.ComputeState(tenant, sub, now));
        sub.CurrentPeriodEnd = now.AddDays(-8);
        Assert.Equal("restricted", Comercial.Api.Services.LicenseService.ComputeState(tenant, sub, now));
        tenant.Status = TenantStatus.Suspended;
        Assert.Equal("suspended", Comercial.Api.Services.LicenseService.ComputeState(tenant, sub, now));
    }

    [Fact]
    public void Resolucao_de_modulos_trata_dependencia_compartilhada_ciclo_e_planejados()
    {
        var catalog = Comercial.Domain.Modules.ModuleCatalogSeed.Build().ToDictionary(m => m.Key);
        // Regressão: pdv -> estoque -> produtos e pdv -> produtos (dependência em "diamante") não é ciclo.
        var ok = Comercial.Api.Services.LicenseService.Resolve(["pdv", "produtos", "estoque", "caixa"], catalog);
        Assert.Contains("pdv", ok);
        // Faltando dependência: pdv não fica efetivo.
        Assert.DoesNotContain("pdv", Comercial.Api.Services.LicenseService.Resolve(["pdv", "produtos", "caixa"], catalog));
        // Planejado nunca fica efetivo, mesmo habilitado no banco.
        Assert.DoesNotContain("crm", Comercial.Api.Services.LicenseService.Resolve(["crm"], catalog));
        // Ciclo artificial é recusado.
        var a = new Comercial.Domain.Modules.ModuleDefinition { Key = "a", Status = Comercial.Domain.Modules.ModuleStatus.Released, Dependencies = [new() { ModuleKey = "a", DependsOnKey = "b" }] };
        var b = new Comercial.Domain.Modules.ModuleDefinition { Key = "b", Status = Comercial.Domain.Modules.ModuleStatus.Released, Dependencies = [new() { ModuleKey = "b", DependsOnKey = "a" }] };
        Assert.DoesNotContain("a", Comercial.Api.Services.LicenseService.Resolve(["a", "b"], new Dictionary<string, Comercial.Domain.Modules.ModuleDefinition> { ["a"] = a, ["b"] = b }));
    }

    [Fact]
    public async Task Empresa_suspensa_nao_registra_novas_operacoes()
    {
        var t = await api.CreateTenant("susp-" + Guid.NewGuid().ToString("N")[..6]);
        var admin = await api.AdminToken();
        await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/suspend", admin, new { confirmTenantCode = t.Code, reason = "abuso confirmado" }, api.Reauth()), HttpStatusCode.NoContent);
        var write = await api.Post("/api/v1/products", t.OwnerToken, new { sku = "S1", name = "x", unit = "UN", priceCents = 1, costCents = 1, minStock = 0 });
        Assert.Equal("license_restricted", await Api.Code(write));
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/products", t.OwnerToken)).StatusCode);
    }

    [Fact]
    public async Task Usuario_restrito_a_loja_nao_opera_em_outra_loja()
    {
        var otherStore = await f.WithDb(async db =>
        {
            var s = new Store { TenantId = T.Id, Code = "filial-" + Guid.NewGuid().ToString("N")[..4], Name = "Filial" };
            db.Stores.Add(s);
            await db.SaveChangesAsync();
            return s.Id;
        });
        var email = $"restrito-{Guid.NewGuid():N}@x.test";
        await Api.Expect(await api.Post("/api/v1/users", T.OwnerToken, new { email, displayName = "r", password = "Senha-Func-123", roles = new[] { "operator" }, storeId = T.StoreId }), HttpStatusCode.Created);
        var token = await api.Login(T.Code, email, "Senha-Func-123");
        var res = await api.Post("/api/v1/cash/sessions", token, new { storeId = otherStore, openingCents = 0 });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("store_scope", await Api.Code(res));
    }
}

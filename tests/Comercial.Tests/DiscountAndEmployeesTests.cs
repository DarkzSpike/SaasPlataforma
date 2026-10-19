using System.Net;
using Comercial.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Tests;

/// <summary>Desconto no total com autorização do proprietário e gestão de funcionários pelo dono.</summary>
public class DiscountAndEmployeesTests(ApiFactory f) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly Api api = new(f);
    private TenantInfo T = null!;
    private Guid product;

    public async Task InitializeAsync()
    {
        T = await api.CreateTenant("dsc-" + Guid.NewGuid().ToString("N")[..6]);
        product = await api.CreateProduct(T.OwnerToken, "DSC1", price: 10000);
        await api.StockEntry(T.OwnerToken, T.StoreId, product, 100);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> Authorize(string token, string email, string password, long discount = 1000, long subtotal = 10000) =>
        api.Post("/api/v1/sales/discount-authorizations", token, new { approverEmail = email, approverPassword = password, discountCents = discount, subtotalCents = subtotal, reason = "cliente fiel" });

    private static CreateSaleRequest SaleWith(Guid store, Guid cash, Guid product, long discount, Guid? auth) =>
        Api.Sale(store, cash, product, pay: 10000 - discount, discount: discount) with { DiscountAuthorizationId = auth };

    [Fact]
    public async Task Desconto_no_total_so_com_senha_do_proprietario_e_uso_unico()
    {
        var (_, op, opEmail) = await api.CreateUser(T, "operator");
        var cash = await api.OpenCash(op, T.StoreId);
        Assert.Equal("discount_not_authorized", await Api.Code(await api.Post("/api/v1/sales", op, SaleWith(T.StoreId, cash, product, 1000, null))));

        Assert.Equal("invalid_credentials", await Api.Code(await Authorize(op, T.OwnerEmail, "senha-errada-123")));
        Assert.Equal("approver_not_allowed", await Api.Code(await Authorize(op, opEmail, "Senha-Func-123")));   // operador não autoriza a si mesmo
        var auth = await Api.Expect(await Authorize(op, T.OwnerEmail, T.OwnerPassword), HttpStatusCode.OK);
        var authId = auth.GetProperty("id").GetGuid();

        Assert.Equal("discount_not_authorized", await Api.Code(await api.Post("/api/v1/sales", op, SaleWith(T.StoreId, cash, product, 1500, authId)))); // acima do autorizado
        var sale = await Api.Expect(await api.Post("/api/v1/sales", op, SaleWith(T.StoreId, cash, product, 1000, authId)), HttpStatusCode.Created);
        Assert.Equal(9000, sale.GetProperty("totalCents").GetInt64());
        Assert.Equal("discount_not_authorized", await Api.Code(await api.Post("/api/v1/sales", op, SaleWith(T.StoreId, cash, product, 1000, authId)))); // não reaproveita

        var saved = await f.WithDb(db => db.Sales.IgnoreQueryFilters().SingleAsync(s => s.Id == sale.GetProperty("id").GetGuid()));
        var ownerId = await f.WithDb(db => db.Users.IgnoreQueryFilters().Where(u => u.Email == T.OwnerEmail).Select(u => u.Id).SingleAsync());
        Assert.Equal(ownerId, saved.DiscountAuthorizedByUserId);
        Assert.True(await f.WithDb(db => db.AuditLogs.AnyAsync(a => a.TenantId == T.Id && a.Action == "sale.discount_authorized")));

        // Outra empresa não usa a autorização desta.
        var other = await api.CreateTenant("dsx-" + Guid.NewGuid().ToString("N")[..6]);
        var op2 = await api.CreateProduct(other.OwnerToken, "X1", price: 10000);
        await api.StockEntry(other.OwnerToken, other.StoreId, op2, 10);
        var (_, otherOp, _) = await api.CreateUser(other, "operator");
        var otherCash = await api.OpenCash(otherOp, other.StoreId);
        var a2 = (await Api.Expect(await Authorize(op, T.OwnerEmail, T.OwnerPassword), HttpStatusCode.OK)).GetProperty("id").GetGuid();
        Assert.Equal("discount_not_authorized", await Api.Code(await api.Post("/api/v1/sales", otherOp, SaleWith(other.StoreId, otherCash, op2, 1000, a2))));
    }

    [Fact]
    public async Task Dono_concede_ao_gerente_o_poder_de_autorizar_desconto()
    {
        var (managerId, manager, managerEmail) = await api.CreateUser(T, "manager");
        var (_, op, _) = await api.CreateUser(T, "operator");
        Assert.Equal("approver_not_allowed", await Api.Code(await Authorize(op, managerEmail, "Senha-Func-123")));
        await Api.Expect(await api.Put($"/api/v1/users/{managerId}/permissions", T.OwnerToken, new[] { new { permission = "sales.discount.authorize", granted = true } }), HttpStatusCode.NoContent);
        await Api.Expect(await Authorize(op, managerEmail, "Senha-Func-123"), HttpStatusCode.OK);
        // O gerente não consegue repassar o que não recebeu de forma própria? Recebeu — mas não pode dar papel de dono.
        Assert.Equal("privilege_escalation", await Api.Code(await api.Post("/api/v1/users", manager, new { email = "x" + Guid.NewGuid().ToString("N")[..6] + "@t.test", displayName = "X", password = "Senha-Func-123", roles = new[] { "owner" } })));
    }

    [Fact]
    public async Task Dono_cria_funcionario_define_senha_e_bloqueia()
    {
        var cat = await Api.Expect(await api.Get("/api/v1/users/permission-catalog", T.OwnerToken), HttpStatusCode.OK);
        Assert.Contains(cat.GetProperty("permissions").EnumerateArray(), p => p.GetProperty("key").GetString() == "sales.discount.authorize" && p.GetProperty("name").GetString()!.Contains("desconto"));
        Assert.Contains(cat.GetProperty("roles").EnumerateArray(), r => r.GetProperty("key").GetString() == "operator");

        var email = $"caixa-{Guid.NewGuid():N}"[..20] + "@loja.test";
        var created = await Api.Expect(await api.Post("/api/v1/users", T.OwnerToken, new { email, displayName = "Caixa Novo", password = "Senha-Inicial-1", roles = new[] { "operator" } }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await api.Login(T.Code, email, "Senha-Inicial-1");

        await Api.Expect(await api.Post($"/api/v1/users/{id}/password", T.OwnerToken, new { newPassword = "Senha-Trocada-2" }), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Post("/api/v1/auth/login", null, new LoginRequest(T.Code, email, "Senha-Inicial-1", null))).StatusCode);
        var token = await api.Login(T.Code, email, "Senha-Trocada-2");
        Assert.Equal("weak_password", await Api.Code(await api.Post($"/api/v1/users/{id}/password", T.OwnerToken, new { newPassword = "123" })));

        // Operador não gerencia funcionários nem troca senha do dono.
        var ownerId = await f.WithDb(db => db.Users.IgnoreQueryFilters().Where(u => u.Email == T.OwnerEmail).Select(u => u.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post($"/api/v1/users/{ownerId}/password", token, new { newPassword = "Senha-Hacker-99" })).StatusCode);

        await Api.Expect(await api.Post($"/api/v1/users/{id}/block", T.OwnerToken), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Get("/api/v1/me", token)).StatusCode);
        Assert.True(await f.WithDb(db => db.AuditLogs.AnyAsync(a => a.TenantId == T.Id && a.Action == "user.password_reset")));
    }
}

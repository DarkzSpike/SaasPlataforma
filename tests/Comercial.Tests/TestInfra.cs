using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Comercial.Api.Security;
using Comercial.Infrastructure.Data;
using Comercial.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Comercial.Tests;

public class ApiFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "root@saas.test";
    public const string AdminPassword = "Senha-Admin-123";
    public const string TotpSecret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    public static readonly string TenantJwtKey = Convert.ToBase64String("chave-de-teste-tenant-0123456789abcdef"u8.ToArray());
    public const string WebhookSecret = "segredo-webhook-teste";

    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"comercial-test-{Guid.NewGuid():N}.db");
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), $"comercial-storage-{Guid.NewGuid():N}");
    protected virtual int AuthRateLimit => 100_000;

    /// <summary>Token admin compartilhado: o TOTP não pode ser reutilizado, então o login acontece uma vez por fábrica.</summary>
    public string? AdminTokenCache { get; set; }
    public SemaphoreSlim AdminLock { get; } = new(1, 1);

    /// <summary>Preenchido quando COMERCIAL_TEST_PG está definida: a suíte inteira roda contra PostgreSQL + RLS.</summary>
    public PostgresTestDb? Postgres { get; } = PostgresTestDb.Enabled ? new PostgresTestDb() : null;

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseEnvironment("Testing");
        if (Postgres is not null)
        {
            b.UseSetting("Database:Provider", "Postgres");
            b.UseSetting("ConnectionStrings:Main", Postgres.AppConnectionString);
        }
        else
        {
            b.UseSetting("Database:Provider", "Sqlite");
            b.UseSetting("ConnectionStrings:Main", $"Data Source={DbPath};Default Timeout=60");
        }
        b.UseSetting("Security:Argon2MemoryKb", "1024");
        b.UseSetting("Security:Argon2Iterations", "1");
        b.UseSetting("RateLimit:AuthPerMinute", AuthRateLimit.ToString());
        b.UseSetting("RateLimit:GlobalPerMinute", "100000");
        b.UseSetting("RateLimit:WebhookPerMinute", "100000");
        b.UseSetting("Billing:AutoRun", "false"); // os testes disparam o faturamento explicitamente
        b.UseSetting("Email:AutoRun", "false");
        b.UseSetting("Fiscal:AutoRun", "false");
        b.UseSetting("Jwt:TenantSigningKey", TenantJwtKey);
        b.UseSetting("Storage:Root", StorageRoot);
        b.UseSetting("Payments:generic:WebhookSecret", WebhookSecret);
        b.UseSetting("Bootstrap:AdminEmail", AdminEmail);
        b.UseSetting("Bootstrap:AdminPassword", AdminPassword);
        b.UseSetting("Bootstrap:AdminTotpSecret", TotpSecret);
    }

    public async Task WithDb(Func<AppDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task WithScope(Func<IServiceProvider, Task> action)
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
        await action(scope.ServiceProvider);
    }

    public async Task<T> WithScopeResult<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
        return await action(scope.ServiceProvider);
    }

    public async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Cleanup();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Cleanup();
    }

    private int _cleaned;
    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) == 1) return;
        Postgres?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(DbPath); Directory.Delete(StorageRoot, true); } catch { /* limpeza best-effort */ }
    }
}

public sealed record TenantInfo(Guid Id, string Code, Guid StoreId, string OwnerEmail, string OwnerPassword, string OwnerToken);

public sealed class Api(ApiFactory factory)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public HttpClient Http { get; } = factory.CreateClient();
    public ApiFactory Factory => factory;
    public static string TotpNow() => Totp.Code(ApiFactory.TotpSecret, Totp.CurrentStep(DateTimeOffset.UtcNow));

    public async Task<string> AdminToken()
    {
        if (factory.AdminTokenCache is { } cached) return cached;
        await factory.AdminLock.WaitAsync();
        try
        {
            if (factory.AdminTokenCache is { } c2) return c2;
            var res = await Send(HttpMethod.Post, "/admin/v1/auth/login", null, new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword, totpCode = TotpNow() });
            await Expect(res, HttpStatusCode.OK);
            factory.AdminTokenCache = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
            return factory.AdminTokenCache!;
        }
        finally { factory.AdminLock.Release(); }
    }

    public Dictionary<string, string> Reauth() => new() { ["X-Reauth-Totp"] = TotpNow() };

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token, object? body = null, Dictionary<string, string>? headers = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        foreach (var (k, v) in headers ?? []) req.Headers.TryAddWithoutValidation(k, v);
        return await Http.SendAsync(req);
    }

    public Task<HttpResponseMessage> Get(string path, string? token, Dictionary<string, string>? h = null) => Send(HttpMethod.Get, path, token, null, h);
    public Task<HttpResponseMessage> Post(string path, string? token, object? body = null, Dictionary<string, string>? h = null) => Send(HttpMethod.Post, path, token, body ?? new { }, h);
    public Task<HttpResponseMessage> Put(string path, string? token, object body, Dictionary<string, string>? h = null) => Send(HttpMethod.Put, path, token, body, h);
    public Task<HttpResponseMessage> Delete(string path, string? token) => Send(HttpMethod.Delete, path, token, null, null);

    public static async Task<JsonElement> Expect(HttpResponseMessage res, HttpStatusCode status)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == status, $"Esperado {(int)status}, obtido {(int)res.StatusCode}: {text}");
        return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);
    }

    public static async Task<string> Code(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    public async Task<TenantInfo> CreateTenant(string code, string plan = "profissional")
    {
        var pwd = "Senha-Dono-123";
        var email = $"dono@{code}.test";
        var res = await Post("/admin/v1/tenants", await AdminToken(), new
        {
            code, legalName = $"Empresa {code} LTDA", tradeName = $"Loja {code}", planCode = plan, storeName = "Matriz",
            owner = new { email, displayName = "Dono " + code, password = pwd },
        });
        var body = await Expect(res, HttpStatusCode.Created);
        var id = body.GetProperty("id").GetGuid();
        var token = await Login(code, email, pwd);
        var stores = await Expect(await Get("/api/v1/stores", token), HttpStatusCode.OK);
        return new TenantInfo(id, code, stores[0].GetProperty("id").GetGuid(), email, pwd, token);
    }

    public async Task<string> Login(string code, string email, string password, Guid? deviceId = null) =>
        (await LoginFull(code, email, password, deviceId)).AccessToken;

    public async Task<TokenResponse> LoginFull(string code, string email, string password, Guid? deviceId = null)
    {
        var res = await Post("/api/v1/auth/login", null, new LoginRequest(code, email, password, deviceId));
        await Expect(res, HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
    }

    public async Task<(Guid Id, string Token, string Email)> CreateUser(TenantInfo t, string role, string? creatorToken = null)
    {
        var email = $"{role}-{Guid.NewGuid():N}@{t.Code}.test";
        var res = await Post("/api/v1/users", creatorToken ?? t.OwnerToken, new { email, displayName = role, password = "Senha-Func-123", roles = new[] { role } });
        var body = await Expect(res, HttpStatusCode.Created);
        return (body.GetProperty("id").GetGuid(), await Login(t.Code, email, "Senha-Func-123"), email);
    }

    public async Task<Guid> CreateProduct(string token, string sku, long price = 1000, long cost = 600, decimal minStock = 0)
    {
        var res = await Post("/api/v1/products", token, new { sku, barcode = "789" + sku, name = "Produto " + sku, unit = "UN", priceCents = price, costCents = cost, minStock });
        return (await Expect(res, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    public async Task StockEntry(string token, Guid store, Guid product, decimal qty) =>
        await Expect(await Post("/api/v1/stock/entries", token, new { storeId = store, productId = product, quantity = qty, unitCostCents = 600, reason = "carga inicial" }), HttpStatusCode.NoContent);

    /// <summary>Abre a loja (regra: caixa só abre com a loja aberta) e depois o caixa.</summary>
    public async Task<Guid> OpenCash(string token, Guid store, long opening = 10000)
    {
        await OpenStore(token, store);
        return (await Expect(await Post("/api/v1/cash/sessions", token, new { storeId = store, openingCents = opening }), HttpStatusCode.OK)).GetProperty("id").GetGuid();
    }

    public async Task OpenStore(string token, Guid store) =>
        await Expect(await Post("/api/v1/operating/events", token, new { idempotencyKey = "open-" + Guid.NewGuid().ToString("N"), storeId = store, type = 1, localTime = DateTimeOffset.UtcNow }), HttpStatusCode.OK);

    public static CreateSaleRequest Sale(Guid store, Guid cash, Guid product, decimal qty = 1, long pay = 1000, int method = 1, string? key = null, long discount = 0, long? clientPrice = null) =>
        new(key ?? Guid.NewGuid().ToString("N"), store, cash, DateTimeOffset.UtcNow, [new SaleItemRequest(product, qty, 0, clientPrice)], [new SalePaymentRequest(method, pay)], discount);

    public async Task<decimal> Balance(string token, Guid store, Guid product)
    {
        var rows = await Expect(await Get($"/api/v1/stock/balances?storeId={store}", token), HttpStatusCode.OK);
        return rows.EnumerateArray().Where(r => r.GetProperty("productId").GetGuid() == product).Select(r => r.GetProperty("quantity").GetDecimal()).FirstOrDefault();
    }

    public async Task<Guid> RegisterDevice(string token, string name = "Caixa 1") =>
        (await Expect(await Post("/api/v1/devices", token, new { name, platform = "windows" }), HttpStatusCode.OK)).GetProperty("id").GetGuid();
}

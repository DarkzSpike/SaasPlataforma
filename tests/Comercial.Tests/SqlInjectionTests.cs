using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Comercial.Api;
using Comercial.Desktop.Core;
using Comercial.Domain;
using Comercial.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Comercial.Tests;

/// <summary>
/// SQL Injection: entradas maliciosas precisam ser tratadas como DADO, nunca como parte da consulta.
/// Roda no SQLite (padrão) e no PostgreSQL com RLS (dev/test-pg.ps1).
/// </summary>
public class SqlInjectionTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private readonly Api api = new(f);

    public static readonly TheoryData<string> Payloads = new()
    {
        "' OR '1'='1",
        "' OR 1=1 --",
        "\" OR \"\"=\"",
        "x'; DROP TABLE products; --",
        "x\"; DELETE FROM users; --",
        "1; UPDATE stock_balances SET \"Quantity\" = 99999; --",
        "' UNION SELECT \"PasswordHash\" FROM users --",
        "%", "_", "%' OR '%'='",
        "\\'; SELECT pg_sleep(5); --",
        "admin'/*",
    };

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task Login_com_entrada_maliciosa_nao_entra_e_nao_quebra(string payload)
    {
        var t = await api.CreateTenant("li-" + Guid.NewGuid().ToString("N")[..6]);
        // Em cada campo do login: resposta 401 normal (nunca 200 nem 500).
        foreach (var body in new[]
        {
            new LoginRequest(payload, t.OwnerEmail, t.OwnerPassword, null),
            new LoginRequest(t.Code, payload, t.OwnerPassword, null),
            new LoginRequest(t.Code, t.OwnerEmail, payload, null),
            new LoginRequest(t.Code, t.OwnerEmail + payload, payload, null),
        })
        {
            var res = await api.Post("/api/v1/auth/login", null, body);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.Equal("invalid_credentials", await Api.Code(res));
        }
        // E o login legítimo continua funcionando (nada foi alterado no banco).
        Assert.False(string.IsNullOrEmpty(await api.Login(t.Code, t.OwnerEmail, t.OwnerPassword)));
    }

    [Fact]
    public async Task Texto_com_aspas_e_comandos_sql_e_gravado_e_lido_literalmente()
    {
        var t = await api.CreateTenant("lt-" + Guid.NewGuid().ToString("N")[..6]);
        var nasty = "Copo d'água \"Premium\" ; DROP TABLE products; -- 😀 ção";
        var p = await Api.Expect(await api.Post("/api/v1/products", t.OwnerToken,
            new { sku = "INJ-1", barcode = "7890000000024", name = nasty, unit = "UN", priceCents = 990, costCents = 100, minStock = 0 }), HttpStatusCode.Created);
        var id = p.GetProperty("id").GetGuid();
        var back = await Api.Expect(await api.Get($"/api/v1/products/{id}", t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(nasty, back.GetProperty("name").GetString());                      // exatamente o que foi enviado

        // A busca encontra pelo trecho com apóstrofo (caractere legítimo não é bloqueado)...
        var found = await Api.Expect(await api.Get("/api/v1/products?q=" + Uri.EscapeDataString("d'água"), t.OwnerToken), HttpStatusCode.OK);
        Assert.Single(found.EnumerateArray());
        // ...e a tabela continua existindo: listagem e estoque funcionam.
        Assert.Equal(HttpStatusCode.OK, (await api.Get($"/api/v1/stock/balances?storeId={t.StoreId}", t.OwnerToken)).StatusCode);

        // Cliente com apóstrofo no nome (ex.: D'Ávila) também é aceito e encontrado (módulo de clientes ligado para o teste).
        var admin = await api.AdminToken();
        await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/modules/crm/enable", admin, new { confirmTenantCode = t.Code, reason = "teste de injeção" }, api.Reauth()), HttpStatusCode.OK);
        await Api.Expect(await api.Post("/api/v1/customers", t.OwnerToken, new { name = "Ana D'Ávila", phone = "11999990000", marketingConsent = false }), HttpStatusCode.Created);
        var cus = await Api.Expect(await api.Get("/api/v1/customers?q=" + Uri.EscapeDataString("D'Ávila"), t.OwnerToken), HttpStatusCode.OK);
        Assert.Equal("Ana D'Ávila", Assert.Single(cus.EnumerateArray()).GetProperty("name").GetString());
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task Busca_maliciosa_nao_traz_dados_a_mais_nem_de_outra_empresa(string payload)
    {
        var a = await api.CreateTenant("sa-" + Guid.NewGuid().ToString("N")[..6]);
        var b = await api.CreateTenant("sb-" + Guid.NewGuid().ToString("N")[..6]);
        await api.CreateProduct(a.OwnerToken, "AAA1");
        await api.CreateProduct(b.OwnerToken, "BBB1");
        await api.CreateProduct(b.OwnerToken, "BBB2");

        foreach (var path in new[] { "/api/v1/products?q=", "/api/v1/customers?q=", "/api/v1/suppliers?q=" })
        {
            var res = await api.Get(path + Uri.EscapeDataString(payload), b.OwnerToken);
            if (res.StatusCode == HttpStatusCode.Forbidden) { Assert.Equal("module_not_enabled", await Api.Code(res)); continue; } // módulo fora do plano
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var rows = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
            // Nenhum produto tem esse texto no nome: "' OR 1=1" e "%" não podem virar "traz tudo".
            Assert.Empty(rows);
        }
        // Sem filtro, cada empresa só enxerga o que é dela.
        var mine = await Api.Expect(await api.Get("/api/v1/products", b.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(2, mine.GetArrayLength());
    }

    [Fact]
    public async Task Paginacao_e_busca_invalidas_viram_erro_400_e_nao_500()
    {
        var t = await api.CreateTenant("pg-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal("invalid_paging", await Api.Code(await api.Get("/api/v1/products?skip=-5", t.OwnerToken)));
        Assert.Equal("invalid_query", await Api.Code(await api.Get("/api/v1/products?q=" + new string('a', 101), t.OwnerToken)));
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/products?q=" + new string('a', 100), t.OwnerToken)).StatusCode);
        // Tipos errados em parâmetros numéricos/ids não chegam à consulta.
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Get("/api/v1/products?skip=abc", t.OwnerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get("/api/v1/products/1%20OR%201=1", t.OwnerToken)).StatusCode);
        // Busca do painel admin com entrada maliciosa.
        var admin = await api.AdminToken();
        var res = await api.Get("/admin/v1/tenants?q=" + Uri.EscapeDataString("' OR '1'='1"), admin);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty((await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task Gravacao_maliciosa_nao_altera_saldo_de_outros_produtos()
    {
        var t = await api.CreateTenant("gw-" + Guid.NewGuid().ToString("N")[..6]);
        var p1 = await api.CreateProduct(t.OwnerToken, "GW1");
        var p2 = await api.CreateProduct(t.OwnerToken, "GW2");
        await api.StockEntry(t.OwnerToken, t.StoreId, p1, 10);
        await api.StockEntry(t.OwnerToken, t.StoreId, p2, 10);
        // Motivo com SQL: vira texto do histórico; os saldos mudam só pela quantidade informada.
        await Api.Expect(await api.Post("/api/v1/stock/entries", t.OwnerToken, new { storeId = t.StoreId, productId = p1, quantity = 1, unitCostCents = 100,
            reason = "x'; UPDATE stock_balances SET \"Quantity\"=99999; --" }), HttpStatusCode.NoContent);
        Assert.Equal(11, await api.Balance(t.OwnerToken, t.StoreId, p1));
        Assert.Equal(10, await api.Balance(t.OwnerToken, t.StoreId, p2));
    }

    [Fact]
    public async Task Erro_de_banco_nao_vaza_detalhes_para_o_usuario()
    {
        // Simula uma falha do driver com SQL, nome de tabela e senha na mensagem.
        var mw = new ErrorHandlingMiddleware(_ => throw new InvalidOperationException(
            "42P01: relation \"users\" does not exist. SQL: SELECT \"PasswordHash\" FROM users WHERE email='a'; Password=Segredo123"), NullLogger<ErrorHandlingMiddleware>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        await mw.InvokeAsync(ctx);
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Contains("internal_error", body);
        Assert.Contains("traceId", body);                 // identificador para achar o erro no log
        foreach (var leak in new[] { "SELECT", "users", "PasswordHash", "Segredo123", "42P01" }) Assert.DoesNotContain(leak, body);
    }

    [Fact]
    public async Task Varias_alteracoes_em_transacao_com_texto_malicioso_ficam_consistentes()
    {
        var t = await api.CreateTenant("tx-" + Guid.NewGuid().ToString("N")[..6]);
        var p = await api.CreateProduct(t.OwnerToken, "TX1", price: 1000);
        await api.StockEntry(t.OwnerToken, t.StoreId, p, 5);
        var cash = await api.OpenCash(t.OwnerToken, t.StoreId);
        // Venda = venda + itens + pagamento + baixa de estoque na mesma transação; a chave de idempotência maliciosa é só um texto.
        var sale = Api.Sale(t.StoreId, cash, p, qty: 2, pay: 2000, key: "k'; DROP TABLE sales; --");
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, sale), HttpStatusCode.Created);
        var dup = await api.Post("/api/v1/sales", t.OwnerToken, sale);       // repetida: não duplica
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        Assert.Equal(3, await api.Balance(t.OwnerToken, t.StoreId, p));
        Assert.Equal(1, await f.WithDb(db => db.Sales.IgnoreQueryFilters().CountAsync(s => s.TenantId == t.Id)));
    }
}

/// <summary>Banco local do caixa (SQLite/SQLCipher): mesmas garantias.</summary>
public class LocalDatabaseInjectionTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "sqli-" + Guid.NewGuid().ToString("N"));
    public LocalDatabaseInjectionTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Theory]
    [MemberData(nameof(SqlInjectionTests.Payloads), MemberType = typeof(SqlInjectionTests))]
    public void Busca_e_chaves_do_caixa_tratam_entrada_como_dado(string payload)
    {
        var db = new LocalDatabase(Path.Combine(dir, Guid.NewGuid().ToString("N") + ".db"), "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF");
        db.ReplaceProducts([(Guid.NewGuid(), "SKU1", "789001", "Caderno", 1000, "UN"), (Guid.NewGuid(), "SKU2", "789002", "Caneta", 300, "UN")]);
        Assert.Empty(db.SearchProducts(payload, 10));                    // nada de "traz tudo"
        db.Set(payload, payload);                                        // chave/valor com aspas e comandos
        Assert.Equal(payload, db.Get(payload));
        Assert.True(db.Enqueue(payload, SyncTypes.SaleCreate, "{\"x\":\"" + payload.Replace("\"", "\\\"") + "\"}", DateTimeOffset.UtcNow));
        Assert.Equal(payload, Assert.Single(db.Pending(10)).IdempotencyKey);
        Assert.Equal(2, db.SearchProducts("Ca", 10).Count);               // tabelas intactas
    }
}

/// <summary>
/// Trava contra regressão: falha se alguém escrever SQL montado com texto (interpolação/concatenação) no código.
/// SQL manual só é aceito nos pontos revisados abaixo (texto fixo + parâmetros).
/// </summary>
public class SqlSourceGuardTests
{
    private static readonly Regex Dangerous = new(
        @"(FromSqlRaw|ExecuteSqlRaw|ExecuteSqlRawAsync|SqlQueryRaw)\s*\(\s*(\$|@\$|[^""\)]*\+)" +       // EF raw com interpolação/concatenação
        @"|CommandText\s*=\s*(\$""|@\$""|[^;""]*\+\s*[A-Za-z_])" +                                          // ADO.NET montado com variáveis
        @"|CommandText\s*=\s*string\.(Format|Concat)" +
        @"|FromSqlInterpolated|ExecuteSqlInterpolated",                                                     // seguros, mas exigem revisão: proibidos aqui
        RegexOptions.Compiled);

    /// <summary>Únicos usos de SQL manual (revisados): texto fixo, valores sempre por parâmetro.</summary>
    private static readonly string[] Reviewed =
    [
        "RlsConnectionInterceptor.cs",   // set_config com @t/@p
        "AppDbContext.cs",               // pg_advisory_xact_lock(727274) (constante)
        "LocalDatabase.cs",              // SQLite do caixa: textos fixos com $parâmetros
    ];

    [Fact]
    public void Codigo_nao_monta_sql_com_texto_do_usuario()
    {
        var src = Path.Combine(PostgresTestDb.RepoRoot(), "src");
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains("Migrations")))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (Dangerous.IsMatch(lines[i])) problems.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(problems.Count == 0, "SQL montado com texto (use parâmetros):\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Sql_manual_so_existe_nos_pontos_revisados()
    {
        var src = Path.Combine(PostgresTestDb.RepoRoot(), "src");
        var raw = new Regex(@"\bCommandText\s*=|ExecuteSqlRaw|FromSqlRaw|SqlQueryRaw|SqlQuery<|\.FromSql\(|\.ExecuteSql(Async)?\(");
        var found = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains("Migrations"))
            .Where(p => raw.IsMatch(File.ReadAllText(p))).Select(Path.GetFileName).Distinct().Order().ToList();
        var unexpected = found.Except(Reviewed).ToList();
        Assert.True(unexpected.Count == 0, "SQL manual em arquivo não revisado (revise e inclua na lista, ou use o EF): " + string.Join(", ", unexpected));
    }

    [Fact]
    public void Scripts_nao_colam_senha_no_texto_do_sql()
    {
        var root = PostgresTestDb.RepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "dev"), "*.ps1")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "deploy"), "*.*", SearchOption.AllDirectories).Where(p => p.EndsWith(".ps1") || p.EndsWith(".yml") || p.EndsWith(".sh")));
        var bad = new Regex(@"PASSWORD\s+'\$", RegexOptions.IgnoreCase);
        var hits = files.SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, l, i))).Where(x => bad.IsMatch(x.l))
            .Select(x => $"{Path.GetRelativePath(root, x.f)}:{x.i + 1}").ToList();
        Assert.True(hits.Count == 0, "Senha concatenada em SQL de script (use psql -v pw=... e :'pw'): " + string.Join(", ", hits));
    }
}

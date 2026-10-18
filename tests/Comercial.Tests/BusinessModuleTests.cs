using System.Net;
using System.Text;
using System.Text.Json;
using Comercial.Domain;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Tests;

/// <summary>Compras, clientes/fidelidade, financeiro, devoluções, alertas, CSV e conciliação.</summary>
public class BusinessModuleTests(ApiFactory f) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly Api api = new(f);
    private TenantInfo T = null!;
    private static readonly string[] AllNew =
        ["fornecedores", "compras", "crm", "fidelidade", "financeiro", "contas", "despesas", "trocas", "relatorios_premium", "notificacoes", "lucratividade"];

    public async Task InitializeAsync()
    {
        T = await api.CreateTenant("biz-" + Guid.NewGuid().ToString("N")[..6]);
        await Enable(T, AllNew);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task Enable(TenantInfo t, params string[] keys)
    {
        var admin = await api.AdminToken();
        foreach (var k in keys)
            await Api.Expect(await api.Post($"/admin/v1/tenants/{t.Id}/modules/{k}/enable", admin, new { confirmTenantCode = t.Code, reason = "teste de módulos" }, api.Reauth()), HttpStatusCode.OK);
    }

    private static string Q(DateTimeOffset d) => Uri.EscapeDataString(d.ToString("O"));
    private string Period => $"from={Q(DateTimeOffset.UtcNow.AddDays(-1))}&to={Q(DateTimeOffset.UtcNow.AddDays(1))}";

    // ======================= Compras =======================

    [Fact]
    public async Task Compra_do_pedido_ao_recebimento_com_custo_medio_divergencia_e_conta_a_pagar()
    {
        var p1 = await api.CreateProduct(T.OwnerToken, "PC1", price: 1000, cost: 0);
        var p2 = await api.CreateProduct(T.OwnerToken, "PC2", price: 2000, cost: 0);
        var sup = await Api.Expect(await api.Post("/api/v1/suppliers", T.OwnerToken, new { name = "Distribuidora Alfa", document = "12345678000199", leadTimeDays = 3 }), HttpStatusCode.Created);
        var supplierId = sup.GetProperty("id").GetGuid();
        var body = new { supplierId, storeId = T.StoreId, items = new[] { new { productId = p1, quantity = 10m, unitCostCents = 500L }, new { productId = p2, quantity = 5m, unitCostCents = 1200L } } };

        var (_, op, _) = await api.CreateUser(T, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post("/api/v1/purchases", op, body)).StatusCode);
        var (_, stockist, _) = await api.CreateUser(T, "stockist");
        var po = await Api.Expect(await api.Post("/api/v1/purchases", stockist, body), HttpStatusCode.Created);
        var poId = po.GetProperty("id").GetGuid();
        Assert.Equal(1, po.GetProperty("number").GetInt32());
        Assert.Equal(2, (await Api.Expect(await api.Post("/api/v1/purchases", stockist, body), HttpStatusCode.Created)).GetProperty("number").GetInt32());

        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post($"/api/v1/purchases/{poId}/approve", stockist)).StatusCode); // estoquista não aprova
        var items = (await Api.Expect(await api.Get($"/api/v1/purchases/{poId}", stockist), HttpStatusCode.OK)).GetProperty("po").GetProperty("items");
        var i1 = items.EnumerateArray().First(i => i.GetProperty("productId").GetGuid() == p1).GetProperty("id").GetGuid();
        var i2 = items.EnumerateArray().First(i => i.GetProperty("productId").GetGuid() == p2).GetProperty("id").GetGuid();
        Assert.Equal("invalid_status", await Api.Code(await api.Post($"/api/v1/purchases/{poId}/receive", stockist, new { items = new[] { new { purchaseItemId = i1, quantity = 1m } } })));
        await Api.Expect(await api.Post($"/api/v1/purchases/{poId}/approve", T.OwnerToken), HttpStatusCode.OK);

        // Recebimento parcial: 6 de 10 a R$ 5,00.
        await Api.Expect(await api.Post($"/api/v1/purchases/{poId}/receive", stockist, new { items = new[] { new { purchaseItemId = i1, quantity = 6m } }, invoiceNumber = "123" }), HttpStatusCode.OK);
        Assert.Equal(6m, await api.Balance(T.OwnerToken, T.StoreId, p1));
        Assert.Equal("PartiallyReceived", (await Api.Expect(await api.Get($"/api/v1/purchases/{poId}", T.OwnerToken), HttpStatusCode.OK)).GetProperty("status").GetString());
        // Excesso exige confirmação explícita.
        Assert.Equal("excess_quantity", await Api.Code(await api.Post($"/api/v1/purchases/{poId}/receive", stockist, new { items = new[] { new { purchaseItemId = i1, quantity = 9m } } })));
        // Restante a R$ 7,00 (divergência de custo) + conta a pagar em 30 dias.
        var rec = await Api.Expect(await api.Post($"/api/v1/purchases/{poId}/receive", stockist, new
        {
            items = new[] { new { purchaseItemId = i1, quantity = 4m, unitCostCents = (long?)700 }, new { purchaseItemId = i2, quantity = 5m, unitCostCents = (long?)null } },
            payableDueInDays = 30,
        }), HttpStatusCode.OK);
        Assert.Contains("custo", rec.GetProperty("divergences").GetString());
        Assert.Equal("Received", (await Api.Expect(await api.Get($"/api/v1/purchases/{poId}", T.OwnerToken), HttpStatusCode.OK)).GetProperty("status").GetString());
        // Custo médio ponderado: (6×500 + 4×700) / 10 = 580.
        Assert.Equal(580, (await Api.Expect(await api.Get($"/api/v1/products/{p1}", T.OwnerToken), HttpStatusCode.OK)).GetProperty("costCents").GetInt64());
        var payables = await Api.Expect(await api.Get("/api/v1/finance/titles?kind=1", T.OwnerToken), HttpStatusCode.OK);
        Assert.Contains(payables.EnumerateArray(), t => t.GetProperty("amountCents").GetInt64() == 2800 + 6000 && t.GetProperty("category").GetString() == "Compra de mercadoria");
        Assert.Equal("invalid_status", await Api.Code(await api.Post($"/api/v1/purchases/{poId}/cancel", T.OwnerToken, new { reason = "não pode mais" })));
    }

    [Fact]
    public async Task Sugestao_de_reposicao_lista_produtos_no_minimo()
    {
        var p = await api.CreateProduct(T.OwnerToken, "SUG", minStock: 10);
        await api.StockEntry(T.OwnerToken, T.StoreId, p, 3);
        var s = await Api.Expect(await api.Get($"/api/v1/purchases/suggestions?storeId={T.StoreId}", T.OwnerToken), HttpStatusCode.OK);
        var row = s.EnumerateArray().Single(x => x.GetProperty("productId").GetGuid() == p);
        Assert.Equal(17m, row.GetProperty("suggested").GetDecimal());
    }

    [Fact]
    public async Task Modulos_novos_bloqueados_sem_contratacao()
    {
        var t = await api.CreateTenant("nomod-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var path in new[] { "/api/v1/suppliers", "/api/v1/customers", "/api/v1/finance/titles", $"/api/v1/reports/export/sales.csv?{Period}" })
            Assert.Equal("module_not_enabled", await Api.Code(await api.Get(path, t.OwnerToken)));
        var admin = await api.AdminToken();
        var blocked = await api.Post($"/admin/v1/tenants/{t.Id}/modules/compras/enable", admin, new { confirmTenantCode = t.Code, reason = "dependência faltando" }, api.Reauth());
        Assert.Equal("module_change_blocked", await Api.Code(blocked)); // compras depende de fornecedores
    }

    // ======================= Clientes e fidelidade =======================

    private async Task<Guid> Customer(string token, string name, string? cpf = null) =>
        (await Api.Expect(await api.Post("/api/v1/customers", token, new { name, document = cpf, phone = "11999990000", marketingConsent = true }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    [Fact]
    public async Task Cliente_com_CPF_unico_validado_e_isolado_por_empresa()
    {
        var c = await Customer(T.OwnerToken, "Maria", "123.456.789-01");
        Assert.Equal("document_taken", await Api.Code(await api.Post("/api/v1/customers", T.OwnerToken, new { name = "Outra", document = "12345678901", marketingConsent = false })));
        Assert.Equal("invalid_document", await Api.Code(await api.Post("/api/v1/customers", T.OwnerToken, new { name = "X", document = "123", marketingConsent = false })));
        var other = await api.CreateTenant("cli-" + Guid.NewGuid().ToString("N")[..6]);
        await Enable(other, "crm");
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/customers/{c}", other.OwnerToken)).StatusCode);
        await Customer(other.OwnerToken, "Maria da outra loja", "12345678901"); // mesmo CPF em outra empresa é permitido
        // Venda de outra empresa não pode usar este cliente.
        var p = await api.CreateProduct(other.OwnerToken, "CX1");
        var cash = await api.OpenCash(other.OwnerToken, other.StoreId);
        var sale = Api.Sale(other.StoreId, cash, p) with { CustomerId = c };
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post("/api/v1/sales", other.OwnerToken, sale)).StatusCode);
    }

    [Fact]
    public async Task Fidelidade_credita_resgata_valida_saldo_e_estorna_no_cancelamento()
    {
        var c = await Customer(T.OwnerToken, "João", "98765432100");
        var p = await api.CreateProduct(T.OwnerToken, "FID", price: 10000, cost: 4000);
        await api.StockEntry(T.OwnerToken, T.StoreId, p, 50);
        var (_, op, _) = await api.CreateUser(T, "operator");
        var cash = await api.OpenCash(op, T.StoreId);

        var s1 = await Api.Expect(await api.Post("/api/v1/sales", op, Api.Sale(T.StoreId, cash, p, qty: 2, pay: 20000) with { CustomerId = c }), HttpStatusCode.Created);
        Assert.Equal(200, s1.GetProperty("pointsEarned").GetInt32()); // R$ 200 × 1 ponto/real
        Assert.Equal("points_below_minimum", await Api.Code(await api.Post("/api/v1/sales", op, Api.Sale(T.StoreId, cash, p, pay: 10000) with { CustomerId = c, RedeemPoints = 50 })));
        Assert.Equal("insufficient_points", await Api.Code(await api.Post("/api/v1/sales", op, Api.Sale(T.StoreId, cash, p, pay: 10000) with { CustomerId = c, RedeemPoints = 300 })));

        // Resgate de 200 pontos = R$ 10,00, sem bloquear pelo limite de desconto do operador (é benefício do programa).
        var s2 = await Api.Expect(await api.Post("/api/v1/sales", op, Api.Sale(T.StoreId, cash, p, pay: 9000) with { CustomerId = c, RedeemPoints = 200 }), HttpStatusCode.Created);
        Assert.Equal(9000, s2.GetProperty("totalCents").GetInt64());
        Assert.Equal(1000, s2.GetProperty("loyaltyDiscountCents").GetInt64());
        var bal = await Api.Expect(await api.Get($"/api/v1/customers/{c}/loyalty", T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(90, bal.GetProperty("balance").GetProperty("available").GetInt32()); // 200 - 200 + 90

        // Cancelar a venda do resgate: devolve 200 pontos e retira os 90 ganhos.
        await Api.Expect(await api.Post($"/api/v1/sales/{s2.GetProperty("id").GetGuid()}/cancel", T.OwnerToken, new { reason = "cliente desistiu" }), HttpStatusCode.OK);
        bal = await Api.Expect(await api.Get($"/api/v1/customers/{c}/loyalty", T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(200, bal.GetProperty("balance").GetProperty("available").GetInt32());

        // Validade: pontos vencidos deixam de valer.
        // O extrato é imutável para a aplicação (no PostgreSQL até o UPDATE é negado), então o teste "envelhece"
        // os pontos diretamente no banco com privilégio de dono.
        if (f.Postgres is { } pg)
            PostgresTestDb.Exec(pg.OwnerConnectionString, $"UPDATE loyalty_transactions SET \"ExpiresAt\" = now() - interval '1 day' WHERE \"CustomerId\" = '{c}' AND \"Points\" > 0");
        else
            await f.WithDb(db => db.Database.ExecuteSqlAsync(
                $"UPDATE loyalty_transactions SET \"ExpiresAt\" = {(DateTimeOffset.UtcNow.AddDays(-1).UtcTicks / 1000) << 11} WHERE \"CustomerId\" = {c.ToString().ToUpperInvariant()} AND \"Points\" > 0"));
        bal = await Api.Expect(await api.Get($"/api/v1/customers/{c}/loyalty", T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(0, bal.GetProperty("balance").GetProperty("available").GetInt32());
        Assert.True(bal.GetProperty("balance").GetProperty("expired").GetInt32() > 0);
    }

    [Fact]
    public void Saldo_de_pontos_consome_lotes_mais_antigos_primeiro()
    {
        var now = DateTimeOffset.UtcNow;
        var c = Guid.NewGuid();
        var txs = new List<LoyaltyTransaction>
        {
            new() { CustomerId = c, Type = LoyaltyType.Earn, Points = 100, CreatedAt = now.AddDays(-40), ExpiresAt = now.AddDays(-1) },
            new() { CustomerId = c, Type = LoyaltyType.Earn, Points = 100, CreatedAt = now.AddDays(-10), ExpiresAt = now.AddDays(300) },
            new() { CustomerId = c, Type = LoyaltyType.Redeem, Points = -80, CreatedAt = now.AddDays(-5) },
        };
        var b = Comercial.Api.Services.LoyaltyService.Compute(txs, now);
        // O resgate consumiu o lote antigo (80 de 100); os 20 restantes venceram. Lote novo intacto.
        Assert.Equal(100, b.Available);
        Assert.Equal(20, b.Expired);
    }

    [Fact]
    public async Task LGPD_exporta_e_anonimiza_somente_com_permissao_do_dono()
    {
        var c = await Customer(T.OwnerToken, "Ana Privacidade", "11122233344");
        var (_, manager, _) = await api.CreateUser(T, "manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Get($"/api/v1/customers/{c}/export", manager)).StatusCode);
        var export = await Api.Expect(await api.Get($"/api/v1/customers/{c}/export", T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal("11122233344", export.GetProperty("customer").GetProperty("document").GetString());
        await Api.Expect(await api.Post($"/api/v1/customers/{c}/anonymize", T.OwnerToken, new { reason = "pedido do titular" }), HttpStatusCode.NoContent);
        var after = await f.WithDb(db => db.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == c));
        Assert.Null(after.Document); Assert.Null(after.Phone); Assert.Equal("Cliente anonimizado", after.Name);
        Assert.False(await f.WithDb(db => db.AuditLogs.AnyAsync(a => a.DataJson!.Contains("11122233344")))); // auditoria sem dado pessoal
    }

    // ======================= Financeiro =======================

    [Fact]
    public async Task Contas_parceladas_baixa_estorno_e_fluxo_de_caixa()
    {
        var due = DateOnly.FromDateTime(DateTime.Today);
        var created = await Api.Expect(await api.Post("/api/v1/finance/titles", T.OwnerToken,
            new { kind = 1, description = "Aluguel do equipamento", category = "Aluguel", firstDueDate = due, totalCents = 1000, installments = 3 }), HttpStatusCode.OK);
        Assert.Equal([333L, 333L, 334L], created.EnumerateArray().Select(t => t.GetProperty("amountCents").GetInt64()).ToArray());
        var first = created[0].GetProperty("id").GetGuid();
        await Api.Expect(await api.Post($"/api/v1/finance/titles/{first}/pay", T.OwnerToken, new { method = "pix" }), HttpStatusCode.OK);
        Assert.Equal("invalid_status", await Api.Code(await api.Post($"/api/v1/finance/titles/{first}/pay", T.OwnerToken, new { })));
        await Api.Expect(await api.Post("/api/v1/finance/titles", T.OwnerToken,
            new { kind = 2, description = "Venda a prazo", category = "Vendas", firstDueDate = due.AddDays(-3), totalCents = 5000 }), HttpStatusCode.OK);

        var sum = await Api.Expect(await api.Get($"/api/v1/finance/summary?from={due.AddDays(-10):yyyy-MM-dd}&to={due.AddMonths(3):yyyy-MM-dd}", T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(333, sum.GetProperty("paidCents").GetInt64());
        Assert.Equal(667, sum.GetProperty("openPayableCents").GetInt64());
        Assert.Equal(5000, sum.GetProperty("overdueReceivableCents").GetInt64());

        await Api.Expect(await api.Post($"/api/v1/finance/titles/{first}/reopen", T.OwnerToken, new { reason = "pago em duplicidade" }), HttpStatusCode.OK);
        var (_, manager, _) = await api.CreateUser(T, "manager");
        Assert.Equal(HttpStatusCode.OK, (await api.Get("/api/v1/finance/titles", manager)).StatusCode); // gerente consulta
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post($"/api/v1/finance/titles/{first}/pay", manager, new { })).StatusCode); // mas não baixa
    }

    [Fact]
    public async Task Despesas_entram_no_resultado_liquido_estimado()
    {
        var t = await api.CreateTenant("liq-" + Guid.NewGuid().ToString("N")[..6]);
        await Enable(t, "lucratividade", "financeiro", "despesas");
        var p = await api.CreateProduct(t.OwnerToken, "L1", price: 1000, cost: 400);
        var cash = await api.OpenCash(t.OwnerToken, t.StoreId);
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, cash, p, qty: 3, pay: 3000)), HttpStatusCode.Created);
        await Api.Expect(await api.Post("/api/v1/finance/expenses", t.OwnerToken,
            new { description = "Conta de luz", category = "Energia", amountCents = 500, paidOn = DateOnly.FromDateTime(DateTime.Today), method = "boleto" }), HttpStatusCode.OK);
        var r = await Api.Expect(await api.Get($"/api/v1/reports/profitability?{Period}", t.OwnerToken), HttpStatusCode.OK);
        var totals = r.GetProperty("totals");
        Assert.Equal(1800, totals.GetProperty("grossProfitCents").GetInt64());
        Assert.Equal(500, totals.GetProperty("expensesCents").GetInt64());
        Assert.Equal(1300, totals.GetProperty("netResultCents").GetInt64());
    }

    // ======================= Devoluções =======================

    [Fact]
    public async Task Devolucao_parcial_reembolsa_proporcional_volta_estoque_e_bloqueia_excesso()
    {
        var p = await api.CreateProduct(T.OwnerToken, "DEV", price: 1000, cost: 400);
        await api.StockEntry(T.OwnerToken, T.StoreId, p, 10);
        var cash = await api.OpenCash(T.OwnerToken, T.StoreId, 10000);
        var sale = await Api.Expect(await api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, p, qty: 3, pay: 2700, discount: 300)), HttpStatusCode.Created);
        var saleId = sale.GetProperty("id").GetGuid();
        var itemId = (await Api.Expect(await api.Get($"/api/v1/sales/{saleId}", T.OwnerToken), HttpStatusCode.OK)).GetProperty("items")[0].GetProperty("id").GetGuid();

        var (_, op, _) = await api.CreateUser(T, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post($"/api/v1/sales/{saleId}/returns", op, new { items = new[] { new { saleItemId = itemId, quantity = 1m } }, reason = "defeito" })).StatusCode);

        var before = (await Api.Expect(await api.Get("/api/v1/cash/sessions/current", T.OwnerToken), HttpStatusCode.OK)).GetProperty("expectedCashCents").GetInt64();
        var r1 = await Api.Expect(await api.Post($"/api/v1/sales/{saleId}/returns", T.OwnerToken, new { items = new[] { new { saleItemId = itemId, quantity = 1m } }, reason = "produto com defeito" }), HttpStatusCode.OK);
        Assert.Equal(900, r1.GetProperty("refundCents").GetInt64()); // 1/3 de R$ 27,00 (desconto rateado)
        Assert.Equal(8m, await api.Balance(T.OwnerToken, T.StoreId, p));
        var after = (await Api.Expect(await api.Get("/api/v1/cash/sessions/current", T.OwnerToken), HttpStatusCode.OK)).GetProperty("expectedCashCents").GetInt64();
        Assert.Equal(before - 900, after);

        Assert.Equal("invalid_quantity", await Api.Code(await api.Post($"/api/v1/sales/{saleId}/returns", T.OwnerToken, new { items = new[] { new { saleItemId = itemId, quantity = 3m } }, reason = "devolver tudo" })));
        Assert.Equal("has_returns", await Api.Code(await api.Post($"/api/v1/sales/{saleId}/cancel", T.OwnerToken, new { reason = "cancelar tudo" })));
        await Api.Expect(await api.Post($"/api/v1/sales/{saleId}/returns", T.OwnerToken, new { items = new[] { new { saleItemId = itemId, quantity = 2m } }, reason = "trocou de ideia", restock = false }), HttpStatusCode.OK);
        Assert.Equal(8m, await api.Balance(T.OwnerToken, T.StoreId, p)); // sem retorno ao estoque (avariado)

        var prof = await Api.Expect(await api.Get($"/api/v1/reports/profitability?{Period}", T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal(2700, prof.GetProperty("totals").GetProperty("returnsCents").GetInt64());
    }

    // ======================= Alertas, CSV e conciliação =======================

    [Fact]
    public async Task Estoque_baixo_gera_um_aviso_por_produto_por_dia()
    {
        var p = await api.CreateProduct(T.OwnerToken, "LOW", price: 100, cost: 50, minStock: 5);
        await api.StockEntry(T.OwnerToken, T.StoreId, p, 6);
        var cash = await api.OpenCash(T.OwnerToken, T.StoreId);
        await Api.Expect(await api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, p, qty: 2, pay: 200)), HttpStatusCode.Created);
        await Api.Expect(await api.Post("/api/v1/sales", T.OwnerToken, Api.Sale(T.StoreId, cash, p, qty: 1, pay: 100)), HttpStatusCode.Created);
        var n = await Api.Expect(await api.Get("/api/v1/notifications", T.OwnerToken), HttpStatusCode.OK);
        Assert.Single(n.EnumerateArray(), x => x.GetProperty("type").GetString() == "stock.low" && x.GetProperty("title").GetString()!.Contains("LOW"));
    }

    [Fact]
    public async Task Exportacao_CSV_com_BOM_protecao_de_formula_e_auditoria()
    {
        await Api.Expect(await api.Post("/api/v1/products", T.OwnerToken, new { sku = "CSV1", name = "=HYPERLINK(\"http://x\")", unit = "UN", priceCents = 100, costCents = 50, minStock = 0 }), HttpStatusCode.Created);
        var res = await api.Get($"/api/v1/reports/export/stock.csv?storeId={T.StoreId}", T.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType!.MediaType);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("sku;codigo_barras;produto", text);
        Assert.Contains("'=HYPERLINK", text);
        Assert.Equal(HttpStatusCode.OK, (await api.Get($"/api/v1/reports/export/sales.csv?{Period}", T.OwnerToken)).StatusCode);
        var (_, op, _) = await api.CreateUser(T, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Get($"/api/v1/reports/export/sales.csv?{Period}", op)).StatusCode);
        Assert.True(await f.WithDb(db => db.AuditLogs.AnyAsync(a => a.Action == "report.exported" && a.TenantId == T.Id)));
    }

    [Fact]
    public async Task Descartar_venda_recusada_exige_gerente_motivo_e_fica_auditado()
    {
        var body = new { idempotencyKey = "sale-abc", reason = "venda duplicada no caixa 2", totalCents = 1500, error = "price_mismatch" };
        var (_, op, _) = await api.CreateUser(T, "operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post("/api/v1/sync/discard", op, body)).StatusCode);
        Assert.Equal("reason_required", await Api.Code(await api.Post("/api/v1/sync/discard", T.OwnerToken, body with { reason = "curto" })));
        await Api.Expect(await api.Post("/api/v1/sync/discard", T.OwnerToken, body), HttpStatusCode.NoContent);
        Assert.True(await f.WithDb(db => db.AuditLogs.AnyAsync(a => a.Action == "sync.rejected_discarded" && a.EntityId == "sale-abc")));
    }
}

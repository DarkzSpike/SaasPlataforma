using System.Globalization;
using System.Text;
using Comercial.Api.Security;
using Comercial.Api.Services;
using Comercial.Domain;
using Comercial.Domain.Modules;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Endpoints;

public sealed record DiscardRejectedRequest(string IdempotencyKey, string Reason, long TotalCents, string? Error);
public sealed record ReasonRequest(string Reason);

/// <summary>Compras, clientes/fidelidade, financeiro, devoluções, conciliação e exportações.</summary>
public static class BusinessEndpoints
{
    public static void MapBusinessApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").AddEndpointFilter(async (ctx, next) =>
        {
            var a = ctx.HttpContext.RequestServices.GetRequiredService<RequestActor>();
            return a.IsTenantUser || a.IsSupport ? await next(ctx) : Results.Json(new { status = 401, code = "unauthorized" }, statusCode: 401);
        });

        // ---------------- Fornecedores ----------------
        var sup = api.MapGroup("/suppliers");
        sup.MapGet("/", async (AppDbContext db, string? q, bool? all) =>
        {
            var query = db.Suppliers.AsNoTracking();
            if (all != true) query = query.Where(s => s.IsActive);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(s => s.Name.Contains(q) || s.Document == q);
            return Results.Ok(await query.OrderBy(s => s.Name).Take(500).ToListAsync());
        }).RequireFeature(ModuleKeys.Suppliers, Permissions.SuppliersManage);
        sup.MapPost("/", async (SupplierInput r, AppDbContext db, AuditService audit, RequestActor a) =>
        {
            PurchasingService.Validate(r);
            var s = new Supplier { Name = r.Name.Trim(), Document = r.Document, ContactName = r.ContactName, Email = r.Email, Phone = r.Phone, LeadTimeDays = r.LeadTimeDays, Notes = r.Notes };
            db.Suppliers.Add(s);
            audit.Add("supplier.created", a.TenantId, nameof(Supplier), s.Id, new { s.Name });
            await db.SaveChangesAsync();
            return Results.Created($"/api/v1/suppliers/{s.Id}", s);
        }).RequireFeature(ModuleKeys.Suppliers, Permissions.SuppliersManage, write: true);
        sup.MapPut("/{id:guid}", async (Guid id, SupplierInput r, AppDbContext db, ScopedLookup l, AuditService audit, RequestActor a) =>
        {
            PurchasingService.Validate(r);
            var s = await l.GetAsync<Supplier>(id);
            s.Name = r.Name.Trim(); s.Document = r.Document; s.ContactName = r.ContactName; s.Email = r.Email; s.Phone = r.Phone; s.LeadTimeDays = r.LeadTimeDays; s.Notes = r.Notes;
            audit.Add("supplier.updated", a.TenantId, nameof(Supplier), s.Id);
            await db.SaveChangesAsync();
            return Results.Ok(s);
        }).RequireFeature(ModuleKeys.Suppliers, Permissions.SuppliersManage, write: true);
        sup.MapGet("/{id:guid}/history", async (Guid id, AppDbContext db, ScopedLookup l) =>
        {
            await l.GetAsync<Supplier>(id);
            var orders = await db.PurchaseOrders.AsNoTracking().Where(p => p.SupplierId == id).OrderByDescending(p => p.CreatedAt).Take(100)
                .Select(p => new { p.Id, p.Number, p.Status, p.CreatedAt, p.ClosedAt, totalCents = p.Items.Sum(i => (long)(i.QuantityOrdered * i.UnitCostCents)) }).ToListAsync();
            return Results.Ok(orders);
        }).RequireFeature(ModuleKeys.Suppliers, Permissions.SuppliersManage);

        // ---------------- Compras ----------------
        var pur = api.MapGroup("/purchases");
        pur.MapGet("/", async (AppDbContext db, int? status) =>
        {
            var q = db.PurchaseOrders.AsNoTracking();
            if (status is { } st) q = q.Where(p => p.Status == (PurchaseStatus)st);
            var list = await q.OrderByDescending(p => p.Number).Take(300)
                .Select(p => new { p.Id, p.Number, p.SupplierId, p.Status, p.CreatedAt, p.ApprovedAt, p.ExpectedAt, items = p.Items.Count,
                                   totalCents = p.Items.Sum(i => (long)(i.QuantityOrdered * i.UnitCostCents)) }).ToListAsync();
            var names = await db.Suppliers.AsNoTracking().Select(s => new { s.Id, s.Name }).ToDictionaryAsync(s => s.Id, s => s.Name);
            return Results.Ok(list.Select(p => new { p.Id, p.Number, p.SupplierId, supplierName = names.GetValueOrDefault(p.SupplierId, "—"), status = p.Status.ToString(), p.CreatedAt, p.ApprovedAt, p.ExpectedAt, p.items, p.totalCents }));
        }).RequireFeature(ModuleKeys.Purchases, Permissions.PurchasesManage);
        pur.MapGet("/{id:guid}", async (Guid id, AppDbContext db, ScopedLookup l) =>
        {
            var po = await l.GetAsync<PurchaseOrder>(id, q => q.Include(p => p.Items));
            var receipts = await db.PurchaseReceipts.AsNoTracking().Where(r => r.PurchaseOrderId == id).OrderBy(r => r.CreatedAt).ToListAsync();
            return Results.Ok(new { po, status = po.Status.ToString(), receipts });
        }).RequireFeature(ModuleKeys.Purchases, Permissions.PurchasesManage);
        pur.MapGet("/suggestions", async (Guid storeId, PurchasingService s) => Results.Ok(await s.SuggestionsAsync(storeId)))
            .RequireFeature(ModuleKeys.Purchases, Permissions.PurchasesManage);
        pur.MapPost("/", async (CreatePurchaseRequest r, PurchasingService s) => { var po = await s.CreateAsync(r); return Results.Created($"/api/v1/purchases/{po.Id}", new { po.Id, po.Number }); })
            .RequireFeature(ModuleKeys.Purchases, Permissions.PurchasesManage, write: true);
        pur.MapPost("/{id:guid}/approve", async (Guid id, PurchasingService s) => { var po = await s.ApproveAsync(id); return Results.Ok(new { po.Id, status = po.Status.ToString() }); })
            .RequireFeature(ModuleKeys.Purchases, Permissions.PurchasesApprove, write: true);
        pur.MapPost("/{id:guid}/cancel", async (Guid id, CancelRequest r, PurchasingService s) => { var po = await s.CancelAsync(id, r); return Results.Ok(new { po.Id, status = po.Status.ToString() }); })
            .RequireFeature(ModuleKeys.Purchases, Permissions.PurchasesManage);
        pur.MapPost("/{id:guid}/receive", async (Guid id, ReceivePurchaseRequest r, PurchasingService s) =>
        {
            var rec = await s.ReceiveAsync(id, r);
            return Results.Ok(new { rec.Id, rec.TotalCents, divergences = rec.DivergenceJson });
        }).RequireFeature(ModuleKeys.Purchases, Permissions.PurchasesReceive, write: true);

        // ---------------- Clientes (CRM) ----------------
        var cus = api.MapGroup("/customers");
        cus.MapGet("/", async (AppDbContext db, string? q) =>
        {
            var query = db.Customers.AsNoTracking().Where(c => c.IsActive);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var digits = new string(q.Where(char.IsDigit).ToArray());
                query = query.Where(c => c.Name.Contains(q) || (digits.Length >= 4 && (c.Document!.Contains(digits) || c.Phone!.Contains(digits))));
            }
            return Results.Ok(await query.OrderBy(c => c.Name).Take(100)
                .Select(c => new { c.Id, c.Name, c.Document, c.Phone, c.Email, c.MarketingConsent }).ToListAsync());
        }).RequireFeature(ModuleKeys.Crm, Permissions.CustomersRead);
        cus.MapGet("/{id:guid}", async (Guid id, AppDbContext db, ScopedLookup l, LoyaltyService loyalty, FeatureAuthorization f) =>
        {
            var c = await l.GetAsync<Customer>(id);
            var stats = await db.Sales.AsNoTracking().Where(s => s.CustomerId == id && s.Status == SaleStatus.Completed)
                .GroupBy(_ => 1).Select(g => new { count = g.Count(), totalCents = g.Sum(s => s.TotalCents - s.ReturnedCents) }).FirstOrDefaultAsync();
            var recent = await db.Sales.AsNoTracking().Where(s => s.CustomerId == id).OrderByDescending(s => s.ReceivedAt).Take(10)
                .Select(s => new { s.Id, s.ReceivedAt, s.TotalCents, status = s.Status.ToString() }).ToListAsync();
            var balance = (await f.ModulesAsync()).Contains(ModuleKeys.Loyalty) ? await loyalty.BalanceAsync(id) : null;
            return Results.Ok(new { customer = c, purchases = stats?.count ?? 0, totalSpentCents = stats?.totalCents ?? 0, recent, loyalty = balance });
        }).RequireFeature(ModuleKeys.Crm, Permissions.CustomersRead);
        cus.MapPost("/", async (CustomerInput r, CustomerService s) => { var c = await s.SaveAsync(null, r); return Results.Created($"/api/v1/customers/{c.Id}", c); })
            .RequireFeature(ModuleKeys.Crm, Permissions.CustomersManage, write: true);
        cus.MapPut("/{id:guid}", async (Guid id, CustomerInput r, CustomerService s) => Results.Ok(await s.SaveAsync(id, r)))
            .RequireFeature(ModuleKeys.Crm, Permissions.CustomersManage, write: true);
        cus.MapGet("/{id:guid}/export", async (Guid id, CustomerService s) => Results.Ok(await s.ExportAsync(id)))
            .RequireFeature(ModuleKeys.Crm, Permissions.CustomersPrivacy);
        cus.MapPost("/{id:guid}/anonymize", async (Guid id, ReasonRequest r, CustomerService s) => { await s.AnonymizeAsync(id, r.Reason); return Results.NoContent(); })
            .RequireFeature(ModuleKeys.Crm, Permissions.CustomersPrivacy);
        cus.MapGet("/{id:guid}/loyalty", async (Guid id, AppDbContext db, ScopedLookup l, LoyaltyService loyalty) =>
        {
            await l.GetAsync<Customer>(id);
            var statement = await db.LoyaltyTransactions.AsNoTracking().Where(t => t.CustomerId == id).OrderByDescending(t => t.CreatedAt).Take(200)
                .Select(t => new { t.Id, type = t.Type.ToString(), t.Points, t.CreatedAt, t.ExpiresAt, t.Reason, t.SaleId }).ToListAsync();
            return Results.Ok(new { balance = await loyalty.BalanceAsync(id), statement });
        }).RequireFeature(ModuleKeys.Loyalty, Permissions.CustomersRead);

        api.MapGet("/loyalty/program", async (LoyaltyService loyalty, RequestActor a) => Results.Ok(await loyalty.ProgramAsync(a.TenantId)))
            .RequireFeature(ModuleKeys.Loyalty, Permissions.CustomersRead);
        api.MapPut("/loyalty/program", async (LoyaltyProgramInput r, AppDbContext db, AuditService audit, RequestActor a) =>
        {
            if (r.PointsPerReal is < 0 or > 100 || r.CentsPerPoint is < 1 or > 1000 || r.MinPointsToRedeem is < 0 or > 1_000_000
                || r.ExpirationDays is < 0 or > 3650 || r.MaxRedeemPercent is < 1 or > 100)
                throw new DomainException("invalid_program", "Regras do programa fora dos limites.");
            var p = await db.LoyaltyPrograms.FirstOrDefaultAsync();
            if (p is null) { p = new LoyaltyProgram(); db.LoyaltyPrograms.Add(p); }
            p.Enabled = r.Enabled; p.PointsPerReal = r.PointsPerReal; p.CentsPerPoint = r.CentsPerPoint; p.MinPointsToRedeem = r.MinPointsToRedeem;
            p.ExpirationDays = r.ExpirationDays; p.MaxRedeemPercent = r.MaxRedeemPercent; p.UpdatedAt = DateTimeOffset.UtcNow;
            audit.Add("loyalty.program_updated", a.TenantId, nameof(LoyaltyProgram), a.TenantId, r);
            await db.SaveChangesAsync();
            return Results.Ok(p);
        }).RequireFeature(ModuleKeys.Loyalty, Permissions.LoyaltyManage, write: true);

        // ---------------- Financeiro ----------------
        var fin = api.MapGroup("/finance");
        fin.MapGet("/categories", () => Results.Ok(new { expense = FinanceCategories.Expense, merchandise = FinanceCategories.Merchandise }))
            .RequireFeature(ModuleKeys.Finance, Permissions.FinanceRead);
        fin.MapGet("/titles", async (AppDbContext db, int? kind, int? status, DateOnly? from, DateOnly? to) =>
        {
            var q = db.FinancialTitles.AsNoTracking();
            if (kind is { } k) q = q.Where(t => t.Kind == (TitleKind)k);
            if (status is { } s) q = q.Where(t => t.Status == (TitleStatus)s);
            if (from is { } f) q = q.Where(t => t.DueDate >= f);
            if (to is { } t2) q = q.Where(t => t.DueDate <= t2);
            return Results.Ok(await q.OrderBy(t => t.DueDate).Take(1000).ToListAsync());
        }).RequireFeature(ModuleKeys.PayablesReceivables, Permissions.FinanceRead);
        fin.MapPost("/titles", async (TitleInput r, FinanceService s) => Results.Ok(await s.CreateAsync(r)))
            .RequireFeature(ModuleKeys.PayablesReceivables, Permissions.FinanceManage, write: true);
        fin.MapPost("/titles/{id:guid}/pay", async (Guid id, PayTitleRequest r, FinanceService s) => Results.Ok(await s.PayAsync(id, r)))
            .RequireFeature(ModuleKeys.PayablesReceivables, Permissions.FinanceManage, write: true);
        fin.MapPost("/titles/{id:guid}/cancel", async (Guid id, ReasonRequest r, FinanceService s) => Results.Ok(await s.CancelAsync(id, r.Reason)))
            .RequireFeature(ModuleKeys.PayablesReceivables, Permissions.FinanceManage);
        fin.MapPost("/titles/{id:guid}/reopen", async (Guid id, ReasonRequest r, FinanceService s) => Results.Ok(await s.ReopenAsync(id, r.Reason)))
            .RequireFeature(ModuleKeys.PayablesReceivables, Permissions.FinanceManage);
        fin.MapPost("/expenses", async (ExpenseInput r, FinanceService s) => Results.Ok(await s.ExpenseAsync(r)))
            .RequireFeature(ModuleKeys.Expenses, Permissions.FinanceManage, write: true);
        fin.MapGet("/summary", async (FinanceService s, DateOnly from, DateOnly to) => Results.Ok(await s.SummaryAsync(from, to)))
            .RequireFeature(ModuleKeys.Finance, Permissions.FinanceRead);

        // ---------------- Trocas e devoluções ----------------
        api.MapPost("/sales/{id:guid}/returns", async (Guid id, CreateReturnRequest r, ReturnService s) =>
        {
            var ret = await s.CreateAsync(id, r);
            return Results.Ok(new { ret.Id, ret.RefundCents, ret.Restock, ret.RefundMethod, items = ret.Items.Count });
        }).RequireFeature(ModuleKeys.Returns, Permissions.SalesReturn, write: true);
        api.MapGet("/sales/{id:guid}/returns", async (Guid id, AppDbContext db, ScopedLookup l) =>
        {
            await l.GetAsync<Sale>(id);
            return Results.Ok(await db.SaleReturns.AsNoTracking().Include(r => r.Items).Where(r => r.SaleId == id).OrderBy(r => r.CreatedAt).ToListAsync());
        }).RequireFeature(ModuleKeys.Pdv, Permissions.SalesRead);

        // ---------------- Conciliação de vendas offline recusadas ----------------
        // Descartar uma venda recusada é decisão de gerente e fica na auditoria do servidor (o terminal guarda o original).
        api.MapPost("/sync/discard", async (DiscardRejectedRequest r, AuditService audit, AppDbContext db, RequestActor a) =>
        {
            if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 10) throw new DomainException("reason_required", "Explique o motivo do descarte (mínimo 10 caracteres).");
            if (string.IsNullOrWhiteSpace(r.IdempotencyKey) || r.IdempotencyKey.Length > 80) throw new DomainException("invalid_key", "Operação inválida.");
            audit.Add("sync.rejected_discarded", a.TenantId, "SyncOperation", r.IdempotencyKey, new { r.Reason, r.TotalCents, r.Error, device = a.DeviceId });
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireFeature(ModuleKeys.Pdv, Permissions.SyncReconcile);

        // ---------------- Exportações CSV (relatórios premium) ----------------
        var exp = api.MapGroup("/reports/export");
        exp.MapGet("/sales.csv", async (AppDbContext db, RequestActor a, AuditService audit, DateTimeOffset from, DateTimeOffset to) =>
        {
            CheckPeriod(from, to);
            var rows = await db.Sales.AsNoTracking().Where(s => s.ReceivedAt >= from && s.ReceivedAt < to).OrderBy(s => s.ReceivedAt)
                .Select(s => new { s.ReceivedAt, s.Status, s.SubtotalCents, s.DiscountCents, s.TotalCents, s.ReturnedCents, s.CreatedOffline, items = s.Items.Count }).ToListAsync();
            var csv = Csv(["data_hora", "situacao", "subtotal", "desconto", "total", "devolvido", "offline", "itens"],
                rows.Select(r => new object[] { r.ReceivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), r.Status, Money(r.SubtotalCents), Money(r.DiscountCents), Money(r.TotalCents), Money(r.ReturnedCents), r.CreatedOffline ? "sim" : "não", r.items }));
            return await Export(audit, db, a, "vendas", csv);
        }).RequireFeature(ModuleKeys.ReportsPremium, Permissions.ReportsExport);
        exp.MapGet("/stock.csv", async (AppDbContext db, RequestActor a, AuditService audit, StockService st, Guid storeId) =>
        {
            await st.EnsureStoreAccessAsync(storeId);
            var rows = await (from p in db.Products.AsNoTracking()
                              where p.IsActive
                              join b in db.StockBalances.AsNoTracking().Where(b => b.StoreId == storeId) on p.Id equals b.ProductId into bs
                              from b in bs.DefaultIfEmpty()
                              orderby p.Name
                              select new { p.Sku, p.Barcode, p.Name, Quantity = b == null ? 0m : b.Quantity, p.MinStock, p.PriceCents, p.CostCents }).ToListAsync();
            var csv = Csv(["sku", "codigo_barras", "produto", "estoque", "minimo", "preco", "custo"],
                rows.Select(r => new object[] { r.Sku, r.Barcode ?? "", r.Name, r.Quantity, r.MinStock, Money(r.PriceCents), Money(r.CostCents) }));
            return await Export(audit, db, a, "estoque", csv);
        }).RequireFeature(ModuleKeys.ReportsPremium, Permissions.ReportsExport);
        exp.MapGet("/profitability.csv", async (AppDbContext db, RequestActor a, AuditService audit, DateTimeOffset from, DateTimeOffset to) =>
        {
            CheckPeriod(from, to);
            if (!a.Permissions.Contains(Permissions.ReportsFinancial)) throw new DomainException("permission_denied", "Sem permissão para dados financeiros.", 403);
            var ids = await db.Sales.AsNoTracking().Where(s => s.ReceivedAt >= from && s.ReceivedAt < to && s.Status == SaleStatus.Completed).Select(s => s.Id).ToListAsync();
            var items = await db.SaleItems.AsNoTracking().Where(i => ids.Contains(i.SaleId)).Select(i => new { i.ProductName, i.Quantity, i.TotalCents, i.UnitCostCents }).ToListAsync();
            var rows = items.GroupBy(i => i.ProductName).Select(g =>
            {
                var rev = g.Sum(i => i.TotalCents); var cost = g.Sum(i => (long)Math.Round(i.UnitCostCents * i.Quantity));
                return new object[] { g.Key, g.Sum(i => i.Quantity), Money(rev), Money(cost), Money(rev - cost), rev == 0 ? "0" : Math.Round((rev - cost) * 100m / rev, 1).ToString(CultureInfo.InvariantCulture) };
            });
            return await Export(audit, db, a, "lucratividade", Csv(["produto", "quantidade", "vendas", "custo", "lucro_bruto", "margem_pct"], rows));
        }).RequireFeature(ModuleKeys.ReportsPremium, Permissions.ReportsExport);
    }

    private static void CheckPeriod(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from || to - from > TimeSpan.FromDays(400)) throw new DomainException("invalid_period", "Período inválido (máx. 400 dias).");
    }

    private static string Money(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>CSV com separador ";" (padrão do Excel em português) e proteção contra injeção de fórmula.</summary>
    public static string Csv(string[] header, IEnumerable<object[]> rows)
    {
        static string Cell(object v)
        {
            var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            if (s.Length > 0 && "=+-@\t\r".Contains(s[0]) && !decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) s = "'" + s;
            return s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', header));
        foreach (var r in rows) sb.AppendLine(string.Join(';', r.Select(Cell)));
        return sb.ToString();
    }

    private static async Task<IResult> Export(AuditService audit, AppDbContext db, RequestActor a, string name, string csv)
    {
        audit.Add("report.exported", a.TenantId, "report", name, new { bytes = csv.Length });
        await db.SaveChangesAsync();
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", $"{name}-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }
}

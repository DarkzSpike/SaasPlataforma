using Comercial.Api.Security;
using Comercial.Api.Services;
using Comercial.Domain;
using Comercial.Domain.Modules;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Comercial.Shared.Contracts;
using Comercial.Shared.Licensing;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Endpoints;

public sealed record ProductInput(string Sku, string? Barcode, string Name, string? Description, string Unit, long PriceCents, long CostCents, decimal MinStock, Guid? CategoryId, int? ExpectedVersion,
    string? Ncm = null, string? Cest = null, string? Cfop = null, int FiscalOrigin = 0, string? IcmsCst = null, string? IbsCbsCst = null, string? IbsCbsClass = null);
public sealed record StockEntryRequest(Guid StoreId, Guid ProductId, decimal Quantity, long UnitCostCents, string? Reason, string? BatchCode = null, DateOnly? ExpiresOn = null);
public sealed record StockAdjustRequest(Guid StoreId, Guid ProductId, decimal Quantity, string Reason);
public sealed record InventoryCount(Guid ProductId, decimal CountedQuantity);
public sealed record InventoryRequest(Guid StoreId, string Reason, List<InventoryCount> Counts);
public sealed record InventoryResultLine(Guid ProductId, string Name, decimal Previous, decimal Counted, decimal Difference);
public sealed record PushTokenRequest(string Token, string Platform);

public static class TenantEndpoints
{
    public static void MapTenantApi(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth").RequireRateLimiting("auth");
        auth.MapPost("/login", async (LoginRequest r, AuthService s) => Results.Ok(await s.LoginAsync(r)));
        auth.MapPost("/refresh", async (RefreshRequest r, AuthService s) => Results.Ok(await s.RefreshAsync(r.RefreshToken)));
        // "Esqueci minha senha": resposta sempre igual (não revela se o e-mail existe).
        // Cadastro com teste grátis (público, com limite por IP).
        var pub = app.MapGroup("/api/v1/public").RequireRateLimiting("auth");
        pub.MapGet("/desktop/latest", async (DesktopReleaseService s) => await s.LatestAsync() is { } r ? Results.Ok(r) : Results.NoContent());
        pub.MapPost("/signup", async (SignupRequest r, SignupService s) => Results.Ok(await s.StartAsync(r)));
        pub.MapPost("/signup/verify", async (SignupVerifyRequest r, SignupService s) => Results.Ok(await s.VerifyAsync(r)));
        auth.MapPost("/forgot", async (ForgotPasswordRequest r, PasswordResetService s) => { await s.RequestAsync(r); return Results.Accepted(); });
        auth.MapPost("/reset", async (ResetPasswordWithCodeRequest r, PasswordResetService s) => { await s.ResetAsync(r); return Results.NoContent(); });

        // Todas as rotas abaixo exigem ator de loja (ou suporte com sessão válida).
        var api = app.MapGroup("/api/v1").AddEndpointFilter(async (ctx, next) =>
        {
            var a = ctx.HttpContext.RequestServices.GetRequiredService<RequestActor>();
            return a.IsTenantUser || a.IsSupport ? await next(ctx) : Results.Json(new { status = 401, code = "unauthorized" }, statusCode: 401);
        });

        api.MapPost("/auth/logout", async (AuthService s) => { await s.LogoutAsync(); return Results.NoContent(); });
        api.MapGet("/me/2fa", async (UserTwoFactorService s) => Results.Ok(await s.StatusAsync())).RequireFeature(ModuleKeys.Core, null);
        api.MapPost("/me/2fa/setup", async (UserTwoFactorService s) => Results.Ok(await s.SetupAsync())).RequireFeature(ModuleKeys.Core, null);
        api.MapPost("/me/2fa/enable", async (TotpCodeRequest r, UserTwoFactorService s) => { await s.EnableAsync(r); return Results.NoContent(); }).RequireFeature(ModuleKeys.Core, null);
        api.MapPost("/me/2fa/disable", async (TotpCodeRequest r, UserTwoFactorService s) => { await s.DisableAsync(r); return Results.NoContent(); }).RequireFeature(ModuleKeys.Core, null);

        api.MapGet("/me", async (RequestActor a, FeatureAuthorization f, AppDbContext db) =>
        {
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == a.TenantId);
            return Results.Ok(new
            {
                user = a.UserId, support = a.Support?.Id, tenant = new { tenant.Id, tenant.Code, tenant.TradeName },
                roles = a.Roles, permissions = a.Permissions.Order(), modules = (await f.ModulesAsync()).Order(), license = await f.LicenseStateAsync(),
            });
        });

        // ---------- Licença e identidade visual ----------
        api.MapGet("/license/grant", async (RequestActor a, LicenseService l) =>
            a.DeviceId is { } d ? Results.Ok(new { grant = await l.IssueGrantAsync(a.TenantId, d) })
                                : Results.Json(new { status = 400, code = "device_required", title = "Faça login com um dispositivo ativado." }, statusCode: 400));
        api.MapGet("/license/public-key", (KeyMaterial k) => Results.Ok(new { keyId = k.LicenseKeyId, pem = k.LicensePublicKeyPem }));

        api.MapGet("/branding", async (HttpContext http, RequestActor a, BrandingService b) =>
        {
            var p = await b.GetPublishedAsync(a.TenantId);
            var etag = $"\"{p.CacheKey}\"";
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "private, no-cache";
            http.Response.Headers.Vary = "Authorization";
            return http.Request.Headers.IfNoneMatch == etag ? Results.StatusCode(304) : Results.Ok(p);
        });
        api.MapGet("/branding/assets/{id:guid}", async (Guid id, HttpContext http, RequestActor a, BrandingService b) =>
        {
            var r = await b.ReadAssetAsync(a.TenantId, id);
            if (r is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=3600";
            http.Response.Headers.ContentDisposition = "inline; filename=asset";
            return Results.File(r.Value.Bytes, r.Value.Asset.ContentType);
        });

        // ---------- Produtos ----------
        var products = api.MapGroup("/products");
        products.MapGet("/", async (AppDbContext db, string? q, int? skip) =>
        {
            var query = db.Products.AsNoTracking().Where(p => p.IsActive);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(p => p.Name.Contains(q) || p.Sku == q || p.Barcode == q);
            return Results.Ok(await query.OrderBy(p => p.Name).Skip(skip ?? 0).Take(200).ToListAsync());
        }).RequireFeature(ModuleKeys.Products, Permissions.ProductsRead);
        products.MapGet("/{id:guid}", async (Guid id, ScopedLookup l) => Results.Ok(await l.GetAsync<Product>(id)))
            .RequireFeature(ModuleKeys.Products, Permissions.ProductsRead);
        products.MapPost("/", async (ProductInput r, AppDbContext db, AuditService audit, RequestActor a) =>
        {
            Validate(r);
            if (await db.Products.AnyAsync(p => p.Sku == r.Sku)) throw new DomainException("sku_taken", "SKU já cadastrado.", 409);
            var p = new Product();
            Apply(p, r);
            db.Products.Add(p);
            audit.Add("product.created", a.TenantId, nameof(Product), p.Id, r);
            await db.SaveChangesAsync();
            return Results.Created($"/api/v1/products/{p.Id}", p);
        }).RequireFeature(ModuleKeys.Products, Permissions.ProductsWrite, write: true);
        products.MapPut("/{id:guid}", async (Guid id, ProductInput r, AppDbContext db, ScopedLookup l, AuditService audit, RequestActor a) =>
        {
            Validate(r);
            var p = await l.GetAsync<Product>(id);
            if (r.ExpectedVersion is { } ev && ev != p.Version) throw new DomainException("version_conflict", "Produto alterado por outro usuário. Recarregue.", 409);
            var before = new { p.PriceCents, p.CostCents, p.Name };
            Apply(p, r);
            p.Version++;
            p.UpdatedAt = DateTimeOffset.UtcNow;
            audit.Add("product.updated", a.TenantId, nameof(Product), p.Id, new { before, after = new { p.PriceCents, p.CostCents, p.Name } });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { throw new DomainException("version_conflict", "Produto alterado por outro usuário. Recarregue.", 409); }
            return Results.Ok(p);
        }).RequireFeature(ModuleKeys.Products, Permissions.ProductsWrite, write: true);
        products.MapPost("/{id:guid}/deactivate", async (Guid id, AppDbContext db, ScopedLookup l, AuditService audit, RequestActor a) =>
        {
            var p = await l.GetAsync<Product>(id);
            p.IsActive = false; p.Version++;
            audit.Add("product.deactivated", a.TenantId, nameof(Product), p.Id);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireFeature(ModuleKeys.Products, Permissions.ProductsWrite, write: true);

        // Foto do produto: conteúdo validado (PNG/JPEG reais), guardada no armazenamento da empresa; leitura só pela API.
        products.MapPost("/{id:guid}/image", async (Guid id, IFormFile file, ProductImageService s, CancellationToken ct) =>
        {
            var p = await s.UploadAsync(id, file, ct);
            return Results.Ok(new { p.Id, p.ImageContentType, p.ImageUpdatedAt, p.Version });
        }).RequireFeature(ModuleKeys.Products, Permissions.ProductsWrite, write: true)
          .DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(6 * 1024 * 1024));
        products.MapGet("/{id:guid}/image", async (Guid id, ProductImageService s, HttpContext http, CancellationToken ct) =>
        {
            var img = await s.ReadAsync(id, ct);
            if (img is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=300";
            return Results.File(img.Value.Bytes, img.Value.ContentType);
        }).RequireFeature(ModuleKeys.Products, Permissions.ProductsRead);

        // ---------- Estoque ----------
        var stock = api.MapGroup("/stock");
        stock.MapGet("/balances", async (AppDbContext db, Guid storeId, StockService s) =>
        {
            await s.EnsureStoreAccessAsync(storeId);
            // Todos os produtos ativos, inclusive os ainda sem movimentação (saldo zero).
            var rows = await (from p in db.Products.AsNoTracking()
                              where p.IsActive
                              join b in db.StockBalances.AsNoTracking().Where(b => b.StoreId == storeId) on p.Id equals b.ProductId into bs
                              from b in bs.DefaultIfEmpty()
                              select new { ProductId = p.Id, p.Name, p.Sku, p.Barcode, p.PriceCents, p.CostCents, Quantity = b == null ? 0m : b.Quantity, p.MinStock, HasImage = p.ImageKey != null,
                                  p.Unit, p.Ncm, p.Cest, p.Cfop, p.FiscalOrigin, p.IcmsCst, p.IbsCbsCst, p.IbsCbsClass })
                             .ToListAsync();
            return Results.Ok(rows.OrderBy(r => r.Name).Select(r => new
            {
                r.ProductId, r.Name, r.Sku, r.Barcode, r.PriceCents, r.CostCents, r.Quantity, r.MinStock, r.HasImage, low = r.Quantity <= r.MinStock,
                r.Unit, r.Ncm, r.Cest, r.Cfop, r.FiscalOrigin, r.IcmsCst, r.IbsCbsCst, r.IbsCbsClass,
            }));
        }).RequireFeature(ModuleKeys.Stock, Permissions.StockRead);

        // Inventário (auditoria por contagem): ajusta o saldo para o valor contado, preservando o histórico.
        stock.MapPost("/inventory", async (InventoryRequest r, AppDbContext db, StockService s, AuditService audit, RequestActor a, StockAlertService alerts) =>
        {
            if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5) throw new DomainException("reason_required", "Informe o motivo do inventário (mínimo 5 caracteres).");
            if (r.Counts is null || r.Counts.Count == 0 || r.Counts.Count > 5000) throw new DomainException("invalid_counts", "Informe as contagens.");
            if (r.Counts.Any(c => c.CountedQuantity < 0 || c.CountedQuantity > 1_000_000)) throw new DomainException("invalid_quantity", "Quantidade contada inválida.");
            if (r.Counts.Select(c => c.ProductId).Distinct().Count() != r.Counts.Count) throw new DomainException("duplicate_product", "Produto repetido na contagem.");
            await s.EnsureStoreAccessAsync(r.StoreId);
            var ids = r.Counts.Select(c => c.ProductId).ToList();
            var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
            if (products.Count != ids.Count) throw new DomainException("product_not_found", "Produto inexistente na contagem.", 404);
            var balances = await db.StockBalances.AsNoTracking().Where(b => b.StoreId == r.StoreId && ids.Contains(b.ProductId)).ToDictionaryAsync(b => b.ProductId, b => b.Quantity);

            var result = new List<InventoryResultLine>();
            await using var tx = await db.Database.BeginTransactionAsync();
            var reason = "Inventário: " + r.Reason.Trim();
            foreach (var c in r.Counts)
            {
                var before = balances.GetValueOrDefault(c.ProductId);
                var diff = c.CountedQuantity - before;
                if (diff != 0)
                    await s.MoveAsync(r.StoreId, c.ProductId, diff, StockMovementType.Adjustment, reason, null, products[c.ProductId].CostCents);
                result.Add(new InventoryResultLine(c.ProductId, products[c.ProductId].Name, before, c.CountedQuantity, diff));
            }
            audit.Add("stock.inventory", a.TenantId, "Store", r.StoreId, new { r.Reason, items = r.Counts.Count, adjusted = result.Count(x => x.Difference != 0) });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            await alerts.CheckAsync(r.StoreId, result.Where(x => x.Difference < 0).Select(x => x.ProductId));
            return Results.Ok(result);
        }).RequireFeature(ModuleKeys.Stock, Permissions.StockAdjust, write: true);
        stock.MapGet("/movements", async (AppDbContext db, Guid productId) =>
            Results.Ok(await db.StockMovements.AsNoTracking().Where(m => m.ProductId == productId).OrderByDescending(m => m.CreatedAt).Take(500).ToListAsync()))
            .RequireFeature(ModuleKeys.Stock, Permissions.StockRead);
        stock.MapPost("/entries", async (StockEntryRequest r, AppDbContext db, StockService s, ScopedLookup l, AuditService audit, RequestActor a, BatchService batches) =>
        {
            if (r.Quantity <= 0 || r.Quantity > 1_000_000 || r.UnitCostCents < 0) throw new DomainException("invalid_quantity", "Quantidade ou custo inválido.");
            if (r.BatchCode is { Length: > 40 }) throw new DomainException("invalid_batch", "Código do lote muito longo.");
            await s.EnsureStoreAccessAsync(r.StoreId);
            await l.GetAsync<Product>(r.ProductId);
            await using var tx = await db.Database.BeginTransactionAsync();
            audit.Add("stock.entry", a.TenantId, nameof(Product), r.ProductId, r);
            await s.MoveAsync(r.StoreId, r.ProductId, r.Quantity, StockMovementType.Entry, r.Reason, null, r.UnitCostCents);
            if (!string.IsNullOrWhiteSpace(r.BatchCode) || r.ExpiresOn is not null)
            {
                batches.Add(r.ProductId, r.StoreId, string.IsNullOrWhiteSpace(r.BatchCode) ? "S/L" : r.BatchCode.Trim(), r.ExpiresOn, r.Quantity);
                await db.SaveChangesAsync();
            }
            await tx.CommitAsync();
            return Results.NoContent();
        }).RequireFeature(ModuleKeys.Stock, Permissions.StockEntry, write: true);
        stock.MapPost("/adjustments", async (StockAdjustRequest r, AppDbContext db, StockService s, ScopedLookup l, AuditService audit, RequestActor a, StockAlertService alerts) =>
        {
            if (r.Quantity == 0 || Math.Abs(r.Quantity) > 1_000_000) throw new DomainException("invalid_quantity", "Quantidade inválida.");
            if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length < 5) throw new DomainException("reason_required", "Ajuste exige motivo (mínimo 5 caracteres).");
            await s.EnsureStoreAccessAsync(r.StoreId);
            var p = await l.GetAsync<Product>(r.ProductId);
            await using var tx = await db.Database.BeginTransactionAsync();
            audit.Add("stock.adjustment", a.TenantId, nameof(Product), r.ProductId, r);
            await s.MoveAsync(r.StoreId, r.ProductId, r.Quantity, StockMovementType.Adjustment, r.Reason, null, p.CostCents);
            await tx.CommitAsync();
            if (r.Quantity < 0) await alerts.CheckAsync(r.StoreId, [r.ProductId]);
            return Results.NoContent();
        }).RequireFeature(ModuleKeys.Stock, Permissions.StockAdjust, write: true);

        // ---------- Caixa ----------
        var cash = api.MapGroup("/cash");
        cash.MapPost("/sessions", async (OpenCashRequest r, CashService s) => Results.Ok(await s.OpenAsync(r)))
            .RequireFeature(ModuleKeys.Cash, Permissions.CashOperate, write: true);
        cash.MapGet("/sessions/current", async (AppDbContext db, RequestActor a, CashService s) =>
        {
            var c = await db.CashSessions.AsNoTracking().FirstOrDefaultAsync(x => x.OperatorUserId == a.UserId && x.Status == CashSessionStatus.Open);
            return c is null ? Results.NoContent() : Results.Ok(new { session = c, expectedCashCents = await s.ExpectedCashAsync(c.Id) });
        }).RequireFeature(ModuleKeys.Cash, Permissions.CashOperate);
        cash.MapPost("/sessions/{id:guid}/supply", async (Guid id, CashMovementRequest r, CashService s) => Results.Ok(await s.MoveAsync(id, CashMovementType.Supply, r)))
            .RequireFeature(ModuleKeys.Cash, Permissions.CashOperate, write: true);
        cash.MapPost("/sessions/{id:guid}/withdrawal", async (Guid id, CashMovementRequest r, CashService s) => Results.Ok(await s.MoveAsync(id, CashMovementType.Withdrawal, r)))
            .RequireFeature(ModuleKeys.Cash, Permissions.CashOperate, write: true);
        // Fechar caixa continua permitido mesmo com licença restrita (regularização).
        cash.MapPost("/sessions/{id:guid}/close", async (Guid id, CloseCashRequest r, CashService s) => Results.Ok(await s.CloseAsync(id, r)))
            .RequireFeature(ModuleKeys.Cash, Permissions.CashOperate);
        cash.MapPost("/sessions/{id:guid}/review", async (Guid id, CashService s) => Results.Ok(await s.ReviewAsync(id)))
            .RequireFeature(ModuleKeys.Cash, Permissions.CashReview);
        cash.MapGet("/sessions", async (AppDbContext db, DateTimeOffset? from, DateTimeOffset? to) =>
        {
            var f = from ?? DateTimeOffset.UtcNow.AddDays(-30); var t = to ?? DateTimeOffset.UtcNow.AddDays(1);
            return Results.Ok(await db.CashSessions.AsNoTracking().Where(c => c.OpenedAt >= f && c.OpenedAt < t).OrderByDescending(c => c.OpenedAt).Take(500).ToListAsync());
        }).RequireFeature(ModuleKeys.Cash, Permissions.CashReview);

        // ---------- Vendas ----------
        var sales = api.MapGroup("/sales");
        // Desconto no total: o proprietário digita a senha dele no PDV do operador (limite de tentativas como no login).
        sales.MapPost("/discount-authorizations", async (DiscountAuthorizationRequest r, DiscountAuthorizationService s) => Results.Ok(await s.AuthorizeAsync(r)))
            .RequireFeature(ModuleKeys.Pdv, Permissions.SalesCreate, write: true).RequireRateLimiting("auth");
        sales.MapPost("/", async (CreateSaleRequest r, SalesService s) =>
        {
            var res = await s.CreateAsync(r);
            return res.Duplicate ? Results.Ok(res) : Results.Created($"/api/v1/sales/{res.Id}", res);
        }).RequireFeature(ModuleKeys.Pdv, Permissions.SalesCreate, write: true);
        sales.MapGet("/{id:guid}", async (Guid id, ScopedLookup l) => Results.Ok(await l.GetAsync<Sale>(id, q => q.Include(s => s.Items).Include(s => s.Payments))))
            .RequireFeature(ModuleKeys.Pdv, Permissions.SalesRead);
        // Histórico de vendas: resumo por venda (itens, formas de pagamento, operador). Detalhe completo em /sales/{id}.
        sales.MapGet("/", async (AppDbContext db, RequestActor a, DateTimeOffset? from, DateTimeOffset? to, int? status) =>
        {
            var tz = await StoreClock.ZoneAsync(db, a);
            var f = from ?? StoreClock.StartOfDay(DateTimeOffset.UtcNow, tz);
            var t = to ?? DateTimeOffset.UtcNow.AddMinutes(1);
            if (t <= f || t - f > TimeSpan.FromDays(400)) throw new DomainException("invalid_period", "Período inválido (máx. 400 dias).");
            var q = db.Sales.AsNoTracking().Where(s => s.ReceivedAt >= f && s.ReceivedAt < t);
            if (a.UserStoreId is { } st) q = q.Where(s => s.StoreId == st);
            if (status is { } stt) q = q.Where(s => s.Status == (SaleStatus)stt);
            var list = await q.OrderByDescending(s => s.ReceivedAt).Take(500)
                .Select(s => new
                {
                    s.Id, s.ReceivedAt, s.LocalCreatedAt, s.Status, s.SubtotalCents, s.DiscountCents, s.TotalCents, s.ChangeCents,
                    s.CreatedOffline, s.OperatorUserId, s.CancelReason, s.CancelledAt,
                    itemsCount = s.Items.Count, quantity = s.Items.Sum(i => i.Quantity),
                    payments = s.Payments.Select(p => new { p.Method, p.AmountCents }).ToList(),
                }).ToListAsync();
            var operators = await db.Users.AsNoTracking().Select(u => new { u.Id, u.DisplayName }).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
            return Results.Ok(list.Select(s => new
            {
                s.Id, s.ReceivedAt, s.LocalCreatedAt, status = s.Status.ToString(), s.SubtotalCents, s.DiscountCents, s.TotalCents, s.ChangeCents,
                s.CreatedOffline, operatorName = operators.GetValueOrDefault(s.OperatorUserId, "—"), s.CancelReason, s.CancelledAt,
                s.itemsCount, s.quantity, s.payments,
            }));
        }).RequireFeature(ModuleKeys.Pdv, Permissions.SalesRead);
        sales.MapPost("/{id:guid}/cancel", async (Guid id, CancelSaleRequest r, SalesService s) =>
        {
            var sale = await s.CancelAsync(id, r);
            return Results.Ok(SalesService.ToResponse(sale, false));
        }).RequireFeature(ModuleKeys.Pdv, Permissions.SalesCancel);

        // ---------- Horários operacionais ----------
        var op = api.MapGroup("/operating");
        op.MapPost("/events", async (OperatingEventRequest r, OperatingService s) =>
        {
            var (e, dup) = await s.RecordAsync(r);
            return Results.Ok(new { e.Id, e.Type, e.LocalTime, e.ReceivedAt, e.ClockSuspect, duplicate = dup });
        }).RequireFeature(ModuleKeys.StoreHours, Permissions.OperatingOpenClose, write: true);
        op.MapPost("/events/{id:guid}/correct", async (Guid id, OperatingCorrectionRequest r, OperatingService s) => Results.Ok(await s.CorrectAsync(id, r)))
            .RequireFeature(ModuleKeys.StoreHours, Permissions.OperatingCorrect, write: true);
        // Quem abre/fecha a loja (inclusive o operador) precisa saber se ela está aberta.
        op.MapGet("/status", async (Guid storeId, OperatingService s) => Results.Ok(await s.StatusAsync(storeId)))
            .RequireFeature(ModuleKeys.StoreHours, Permissions.OperatingOpenClose);
        op.MapGet("/events", async (Guid storeId, AppDbContext db) =>
            Results.Ok(await db.OperatingEvents.AsNoTracking().Where(e => e.StoreId == storeId).OrderByDescending(e => e.ReceivedAt).Take(500).ToListAsync()))
            .RequireFeature(ModuleKeys.StoreHours, Permissions.ReportsRead);

        // ---------- Fiscal ----------
        api.MapGet("/fiscal/documents", async (FiscalService s, int? status) => Results.Ok(await s.ListAsync(status is { } st ? (FiscalStatus)st : null)))
            .RequireFeature(ModuleKeys.Fiscal, Permissions.FiscalRead);
        api.MapPost("/fiscal/documents/{id:guid}/transmit", async (Guid id, FiscalService s, CancellationToken ct) => Results.Ok(await s.TransmitAsync(id, ct)))
            .RequireFeature(ModuleKeys.Fiscal, Permissions.FiscalEmit, write: true);
        api.MapPost("/fiscal/documents/{id:guid}/cancel", async (Guid id, CancelFiscalRequest r, FiscalService s, CancellationToken ct) => Results.Ok(await s.CancelAsync(id, r, ct)))
            .RequireFeature(ModuleKeys.Fiscal, Permissions.FiscalEmit);
        api.MapPost("/fiscal/void-numbers", async (VoidNumbersRequest r, FiscalService s, CancellationToken ct) => Results.Ok(await s.VoidNumbersAsync(r, ct)))
            .RequireFeature(ModuleKeys.Fiscal, Permissions.FiscalEmit);
        api.MapGet("/fiscal/profile", async (FiscalService s) => Results.Ok(await s.GetProfileAsync()))
            .RequireFeature(ModuleKeys.Fiscal, Permissions.FiscalRead);
        // Dados do emitente e credencial do provedor: só o dono.
        api.MapPut("/fiscal/profile", async (FiscalProfileRequest r, FiscalService s) => Results.Ok(await s.SaveProfileAsync(r)))
            .RequireFeature(ModuleKeys.Fiscal, Permissions.PaymentsConfigure);

        // ---------- Relatórios ----------
        api.MapGet("/reports/sales-summary", async (AppDbContext db, RequestActor a, DateTimeOffset from, DateTimeOffset to, Guid? storeId) =>
        {
            if (to <= from || to - from > TimeSpan.FromDays(400)) throw new DomainException("invalid_period", "Período inválido (máx. 400 dias).");
            var st = a.UserStoreId ?? storeId;
            var q = db.Sales.AsNoTracking().Where(s => s.ReceivedAt >= from && s.ReceivedAt < to);
            if (st is { } s1) q = q.Where(s => s.StoreId == s1);
            var completed = await q.Where(s => s.Status == SaleStatus.Completed).Select(s => new { s.Id, s.TotalCents, s.DiscountCents }).ToListAsync();
            var cancelled = await q.Where(s => s.Status == SaleStatus.Cancelled).SumAsync(s => (long?)s.TotalCents) ?? 0;
            var ids = completed.Select(c => c.Id).ToList();
            var items = await db.SaleItems.AsNoTracking().Where(i => ids.Contains(i.SaleId)).Select(i => new { i.TotalCents, i.UnitCostCents, i.Quantity }).ToListAsync();
            var revenue = completed.Sum(c => c.TotalCents);
            var showFinancial = a.Permissions.Contains(Permissions.ReportsFinancial);
            return Results.Ok(new
            {
                from, to, storeId = st,
                salesCount = completed.Count,
                revenueCents = revenue, // faturamento (vendas concluídas)
                receivedCents = revenue, // PDV atual só registra vendas pagas à vista; a prazo exige módulo Contas
                discountCents = completed.Sum(c => c.DiscountCents),
                cancelledCents = cancelled,
                averageTicketCents = completed.Count == 0 ? 0 : revenue / completed.Count,
                grossProfitCents = showFinancial ? revenue - items.Sum(i => (long)Math.Round(i.UnitCostCents * i.Quantity)) : (long?)null,
                note = "Lucro bruto = vendas - custo histórico dos itens. Não inclui despesas nem impostos; não é resultado contábil.",
            });
        }).RequireFeature(ModuleKeys.Pdv, Permissions.ReportsRead);

        // ---------- Lucratividade (módulo 12) ----------
        api.MapGet("/reports/profitability", async (AppDbContext db, RequestActor a, FeatureAuthorization f, FinanceService finance, DateTimeOffset from, DateTimeOffset to, Guid? storeId) =>
        {
            if (to <= from || to - from > TimeSpan.FromDays(400)) throw new DomainException("invalid_period", "Período inválido (máx. 400 dias).");
            var tz = await StoreClock.ZoneAsync(db, a);
            var st = a.UserStoreId ?? storeId;
            var q = db.Sales.AsNoTracking().Where(s => s.ReceivedAt >= from && s.ReceivedAt < to && s.Status == SaleStatus.Completed);
            if (st is { } s1) q = q.Where(s => s.StoreId == s1);
            var sales = await q.Select(s => new { s.Id, s.ReceivedAt, s.TotalCents, s.SubtotalCents, s.DiscountCents }).ToListAsync();
            var ids = sales.Select(s => s.Id).ToList();
            var items = await db.SaleItems.AsNoTracking().Where(i => ids.Contains(i.SaleId))
                .Select(i => new { i.SaleId, i.ProductId, i.ProductName, i.Quantity, i.TotalCents, i.UnitCostCents }).ToListAsync();
            static long Cost(long unit, decimal qty) => (long)Math.Round(unit * qty);

            // Desconto aplicado no total da venda (não atribuído a um item) é mostrado separadamente.
            var itemDiscounts = items.GroupBy(i => i.SaleId).ToDictionary(g => g.Key, g => g.Sum(i => i.TotalCents));
            var saleLevelDiscount = sales.Sum(s => itemDiscounts.GetValueOrDefault(s.Id) - s.TotalCents);
            var revenue = sales.Sum(s => s.TotalCents);
            var cost = items.Sum(i => Cost(i.UnitCostCents, i.Quantity));
            var products = items.GroupBy(i => i.ProductId).Select(g =>
            {
                var rev = g.Sum(i => i.TotalCents);
                var c = g.Sum(i => Cost(i.UnitCostCents, i.Quantity));
                return new
                {
                    productId = g.Key, name = g.First().ProductName, quantity = g.Sum(i => i.Quantity),
                    revenueCents = rev, costCents = c, grossProfitCents = rev - c,
                    marginPercent = rev == 0 ? 0 : Math.Round((rev - c) * 100m / rev, 1),
                };
            }).OrderByDescending(p => p.grossProfitCents).ToList();
            var costBySale = items.GroupBy(i => i.SaleId).ToDictionary(g => g.Key, g => g.Sum(i => Cost(i.UnitCostCents, i.Quantity)));
            var daily = sales.GroupBy(s => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.ReceivedAt, tz).DateTime)).OrderBy(g => g.Key)
                .Select(g => new
                {
                    date = g.Key, salesCount = g.Count(), revenueCents = g.Sum(s => s.TotalCents),
                    costCents = g.Sum(s => costBySale.GetValueOrDefault(s.Id)),
                    grossProfitCents = g.Sum(s => s.TotalCents - costBySale.GetValueOrDefault(s.Id)),
                }).ToList();
            // Devoluções no período: reduzem a receita; o custo só volta se o produto retornou ao estoque.
            var returns = await db.SaleReturns.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to)
                .Select(x => new { x.RefundCents, x.CostCents, x.Restock }).ToListAsync();
            var returnsCents = returns.Sum(x => x.RefundCents);
            var restockedCost = returns.Where(x => x.Restock).Sum(x => x.CostCents);
            var netRevenue = revenue - returnsCents;
            var netCost = cost - restockedCost;
            long? expenses = null;
            if ((await f.ModulesAsync()).Contains(ModuleKeys.Expenses))
                expenses = await finance.OperatingExpensesAsync(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, tz).DateTime), DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, tz).DateTime));
            var gross = netRevenue - netCost;
            return Results.Ok(new
            {
                from, to, storeId = st, timeZone = tz.Id,
                totals = new
                {
                    salesCount = sales.Count, revenueCents = revenue, returnsCents, netRevenueCents = netRevenue, costCents = netCost, grossProfitCents = gross,
                    marginPercent = netRevenue == 0 ? 0 : Math.Round(gross * 100m / netRevenue, 1),
                    saleLevelDiscountCents = saleLevelDiscount,
                    expensesCents = expenses,
                    netResultCents = expenses is { } e ? gross - e : (long?)null,
                },
                products, daily,
                note = "Lucro bruto estimado = vendas − devoluções − custo histórico dos itens. Resultado líquido estimado = lucro bruto − despesas lançadas (sem compra de mercadoria). Impostos e taxas só entram se lançados como despesa; não é resultado contábil.",
            });
        }).RequireFeature(ModuleKeys.Profitability, Permissions.ReportsFinancial);

        // ---------- Dashboard do proprietário (app móvel) ----------
        api.MapGet("/mobile/dashboard", async (AppDbContext db, RequestActor a, FeatureAuthorization f) =>
        {
            var now = DateTimeOffset.UtcNow;
            var tz = await StoreClock.ZoneAsync(db, a);
            var today = StoreClock.StartOfDay(now, tz);
            var month = StoreClock.StartOfMonth(now, tz);
            var monthSales = await db.Sales.AsNoTracking().Where(s => s.ReceivedAt >= month && s.Status == SaleStatus.Completed).Select(s => new { s.Id, s.TotalCents, s.ReceivedAt }).ToListAsync();
            long? todayProfit = null, monthProfit = null;
            if (a.Permissions.Contains(Permissions.ReportsFinancial))
            {
                var ids = monthSales.Select(s => s.Id).ToList();
                var costs = (await db.SaleItems.AsNoTracking().Where(i => ids.Contains(i.SaleId)).Select(i => new { i.SaleId, i.UnitCostCents, i.Quantity }).ToListAsync())
                    .GroupBy(i => i.SaleId).ToDictionary(g => g.Key, g => g.Sum(i => (long)Math.Round(i.UnitCostCents * i.Quantity)));
                monthProfit = monthSales.Sum(s => s.TotalCents - costs.GetValueOrDefault(s.Id));
                todayProfit = monthSales.Where(s => s.ReceivedAt >= today).Sum(s => s.TotalCents - costs.GetValueOrDefault(s.Id));
            }
            var lowStock = await (from b in db.StockBalances join p in db.Products on b.ProductId equals p.Id where b.Quantity <= p.MinStock && p.IsActive select b.ProductId).CountAsync();
            var devices = await db.Devices.AsNoTracking().Where(d => d.Status == DeviceStatus.Active).Select(d => new { d.Id, d.Name, d.LastSyncAt, d.AppVersion }).ToListAsync();
            var openCash = await db.CashSessions.AsNoTracking().CountAsync(c => c.Status == CashSessionStatus.Open);
            // Quebra de caixa: fechamentos do mês em que o dinheiro contado ficou abaixo do esperado.
            var shortages = await db.CashSessions.AsNoTracking()
                .Where(c => c.Status != CashSessionStatus.Open && c.ClosedAt >= month && c.DifferenceCents < 0)
                .Select(c => new { c.ClosedAt, c.DifferenceCents }).ToListAsync();
            return Results.Ok(new
            {
                generatedAt = now,
                todayRevenueCents = monthSales.Where(s => s.ReceivedAt >= today).Sum(s => s.TotalCents),
                todaySalesCount = monthSales.Count(s => s.ReceivedAt >= today),
                monthRevenueCents = monthSales.Sum(s => s.TotalCents),
                monthSalesCount = monthSales.Count,
                todayGrossProfitCents = todayProfit,
                monthGrossProfitCents = monthProfit,
                timeZone = tz.Id,
                openCashSessions = openCash,
                todayCashShortageCents = shortages.Where(c => c.ClosedAt >= today).Sum(c => -c.DifferenceCents!.Value),
                todayCashShortageCount = shortages.Count(c => c.ClosedAt >= today),
                monthCashShortageCents = shortages.Sum(c => -c.DifferenceCents!.Value),
                monthCashShortageCount = shortages.Count,
                lowStockProducts = lowStock,
                license = await f.LicenseStateAsync(),
                devices,
                lastSyncAt = devices.Max(d => d.LastSyncAt),
                note = "Valores consideram apenas dados já sincronizados com o servidor.",
            });
        }).RequireFeature(ModuleKeys.OwnerApp, Permissions.ReportsRead);

        // ---------- Usuários e dispositivos ----------
        var users = api.MapGroup("/users");
        users.MapGet("/permission-catalog", (RequestActor a) => Results.Ok(new
        {
            roles = Roles.All.Where(r => Roles.Level(r) <= a.MaxRoleLevel && (r != Roles.Owner || a.Roles.Contains(Roles.Owner)))
                .Select(r => new { key = r, permissions = Permissions.ForRole(r) }),
            permissions = Permissions.All.Select(p => new { key = p, name = Permissions.Labels.GetValueOrDefault(p, p), canGrant = a.Permissions.Contains(p) }),
        })).RequireFeature(ModuleKeys.Core, Permissions.UsersManage);
        users.MapGet("/grants", async (AppDbContext db) =>
            Results.Ok(await db.UserPermissionGrants.AsNoTracking().Select(g => new { g.UserId, g.Permission, g.Granted }).ToListAsync()))
            .RequireFeature(ModuleKeys.Core, Permissions.UsersManage);
        users.MapPost("/{id:guid}/2fa/reset", async (Guid id, UserTwoFactorService s, UserService us) => { await s.ResetForUserAsync(id, us); return Results.NoContent(); })
            .RequireFeature(ModuleKeys.Core, Permissions.UsersManage);
        users.MapPost("/{id:guid}/password", async (Guid id, ResetPasswordRequest r, UserService s) => { await s.ResetPasswordAsync(id, r); return Results.NoContent(); })
            .RequireFeature(ModuleKeys.Core, Permissions.UsersManage);
        users.MapGet("/", async (AppDbContext db) => Results.Ok(await db.Users.AsNoTracking().Include(u => u.Roles)
                .Select(u => new { u.Id, u.Email, u.DisplayName, u.IsActive, u.StoreId, u.LastLoginAt, u.CommissionPercent, roles = u.Roles.Select(r => r.Role) }).ToListAsync()))
            .RequireFeature(ModuleKeys.Core, Permissions.UsersManage);
        users.MapPost("/", async (CreateUserRequest r, UserService s) => { var u = await s.CreateAsync(r); return Results.Created($"/api/v1/users/{u.Id}", new { u.Id, u.Email }); })
            .RequireFeature(ModuleKeys.Core, Permissions.UsersManage, write: true);
        users.MapPut("/{id:guid}/roles", async (Guid id, SetRolesRequest r, UserService s) => { await s.SetRolesAsync(id, r); return Results.NoContent(); })
            .RequireFeature(ModuleKeys.Core, Permissions.UsersManage);
        users.MapPut("/{id:guid}/permissions", async (Guid id, List<PermissionGrantInput> r, UserService s) => { await s.SetPermissionsAsync(id, r); return Results.NoContent(); })
            .RequireFeature(ModuleKeys.UsersAdvanced, Permissions.UsersManage);
        users.MapPost("/{id:guid}/block", async (Guid id, UserService s) => { await s.SetActiveAsync(id, false); return Results.NoContent(); })
            .RequireFeature(ModuleKeys.Core, Permissions.UsersManage);
        users.MapPost("/{id:guid}/unblock", async (Guid id, UserService s) => { await s.SetActiveAsync(id, true); return Results.NoContent(); })
            .RequireFeature(ModuleKeys.Core, Permissions.UsersManage);
        users.MapPost("/{id:guid}/revoke-sessions", async (Guid id, UserService s) => Results.Ok(new { revoked = await s.RevokeSessionsAsync(id) }))
            .RequireFeature(ModuleKeys.Core, Permissions.UsersManage);

        var devices = api.MapGroup("/devices");
        devices.MapGet("/", async (AppDbContext db) => Results.Ok(await db.Devices.AsNoTracking().ToListAsync()))
            .RequireFeature(ModuleKeys.Core, Permissions.DevicesManage);
        devices.MapPost("/", async (CreateDeviceRequest r, UserService s) => Results.Ok(await s.ActivateDeviceAsync(r)))
            .RequireFeature(ModuleKeys.Core, Permissions.DevicesManage, write: true);
        devices.MapPost("/{id:guid}/revoke", async (Guid id, UserService s) => { await s.RevokeDeviceAsync(id); return Results.NoContent(); })
            .RequireFeature(ModuleKeys.Core, Permissions.DevicesManage);
        api.MapGet("/stores", async (AppDbContext db) => Results.Ok(await db.Stores.AsNoTracking().ToListAsync()))
            .RequireFeature(ModuleKeys.Core, null);

        // ---------- Avisos e push (app do dono) ----------
        api.MapPost("/push/tokens", async (PushTokenRequest r, AppDbContext db, RequestActor a) =>
        {
            if (!a.IsTenantUser) return Results.Json(new { status = 403, code = "user_required" }, statusCode: 403);
            if (string.IsNullOrWhiteSpace(r.Token) || r.Token.Length > 512) throw new DomainException("invalid_token", "Token de push inválido.");
            if (r.Platform is not ("android" or "ios")) throw new DomainException("invalid_platform", "Plataforma inválida.");
            var t = await db.PushTokens.FirstOrDefaultAsync(x => x.Token == r.Token);
            if (t is null) db.PushTokens.Add(new PushToken { UserId = a.UserId!.Value, Token = r.Token, Platform = r.Platform });
            else { t.UserId = a.UserId!.Value; t.Platform = r.Platform; t.LastSeenAt = DateTimeOffset.UtcNow; }
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireFeature(ModuleKeys.Core, null);
        api.MapPost("/push/tokens/remove", async (PushTokenRequest r, AppDbContext db, RequestActor a) =>
        {
            var t = await db.PushTokens.FirstOrDefaultAsync(x => x.Token == r.Token && x.UserId == a.UserId);
            if (t is not null) { db.PushTokens.Remove(t); await db.SaveChangesAsync(); }
            return Results.NoContent();
        }).RequireFeature(ModuleKeys.Core, null);
        api.MapGet("/notifications", async (AppDbContext db, RequestActor a, bool? unread) =>
        {
            var roles = a.Roles.ToList();
            var q = db.Notifications.AsNoTracking().Where(n => roles.Contains(n.TargetRole));
            if (unread == true) q = q.Where(n => n.ReadAt == null);
            return Results.Ok(await q.OrderByDescending(n => n.CreatedAt).Take(100)
                .Select(n => new { n.Id, n.Type, n.Title, n.Body, n.CreatedAt, n.ReadAt, n.PushStatus }).ToListAsync());
        }).RequireFeature(ModuleKeys.Core, null);
        api.MapPost("/notifications/{id:guid}/read", async (Guid id, AppDbContext db, RequestActor a) =>
        {
            var roles = a.Roles.ToList();
            var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && roles.Contains(x.TargetRole));
            if (n is null) return Results.NotFound();
            n.ReadAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireFeature(ModuleKeys.Core, null);

        // ---------- Sincronização offline ----------
        api.MapPost("/sync/batch", async (SyncBatchRequest r, SyncService s) => Results.Ok(await s.ProcessAsync(r)))
            .RequireFeature(ModuleKeys.Core, null);

        // ---------- Transparência do suporte para a empresa ----------
        api.MapGet("/support/sessions", async (AppDbContext db, RequestActor a) =>
            Results.Ok(await db.SupportSessions.AsNoTracking().Where(s => s.TenantId == a.TenantId).OrderByDescending(s => s.StartedAt).Take(100).ToListAsync()))
            .RequireFeature(ModuleKeys.Core, Permissions.SupportApprove);
        api.MapGet("/support/elevation-requests", async (AppDbContext db, RequestActor a) =>
            Results.Ok(await db.SupportElevationRequests.AsNoTracking().Where(s => s.TenantId == a.TenantId && s.DecidedAt == null).ToListAsync()))
            .RequireFeature(ModuleKeys.Core, Permissions.SupportApprove);
        // O suporte nunca aprova a própria elevação: support.approve não é liberável ao suporte.
        api.MapPost("/support/elevation-requests/{id:guid}/approve", async (Guid id, SupportService s) => Results.Ok(await s.DecideElevationAsync(id, true)))
            .RequireFeature(ModuleKeys.Core, Permissions.SupportApprove);
        api.MapPost("/support/elevation-requests/{id:guid}/deny", async (Guid id, SupportService s) => Results.Ok(await s.DecideElevationAsync(id, false)))
            .RequireFeature(ModuleKeys.Core, Permissions.SupportApprove);
        api.MapPost("/support/sessions/{id:guid}/revoke", async (Guid id, SupportService s) => { await s.EndAsync(id, "revogada pela empresa", byTenant: true); return Results.NoContent(); })
            .RequireFeature(ModuleKeys.Core, Permissions.SupportApprove);
        api.MapGet("/support/preauthorization", async (AppDbContext db, RequestActor a) =>
            Results.Ok(SupportService.GetPreAuthorization(await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == a.TenantId))))
            .RequireFeature(ModuleKeys.Core, Permissions.SupportApprove);
        api.MapPut("/support/preauthorization", async (SupportPreAuthorizationRequest r, SupportService s) => Results.Ok(await s.SetPreAuthorizationAsync(r)))
            .RequireFeature(ModuleKeys.Core, Permissions.SupportApprove);

        // ---------- Recebimentos da loja: PIX dinâmico no caixa (Mercado Pago da própria loja) ----------
        api.MapGet("/payments/settings", async (StorePixService s) => Results.Ok(await s.GetAsync()))
            .RequireFeature(ModuleKeys.Pdv, Permissions.SalesCreate);
        api.MapPut("/payments/settings", async (StorePaymentSettingsRequest r, StorePixService s, CancellationToken ct) => Results.Ok(await s.SaveAsync(r, ct)))
            .RequireFeature(ModuleKeys.Pdv, Permissions.PaymentsConfigure);
        var pix = api.MapGroup("/pos/pix-charges");
        pix.MapPost("/", async (CreatePixChargeRequest r, StorePixService s, CancellationToken ct) => Results.Ok(await s.CreateAsync(r, ct)))
            .RequireFeature(ModuleKeys.Pdv, Permissions.SalesCreate, write: true);
        pix.MapGet("/{id:guid}", async (Guid id, StorePixService s, CancellationToken ct) => Results.Ok(await s.StatusAsync(id, ct)))
            .RequireFeature(ModuleKeys.Pdv, Permissions.SalesCreate);
        pix.MapPost("/{id:guid}/cancel", async (Guid id, StorePixService s, CancellationToken ct) => { await s.CancelAsync(id, ct); return Results.NoContent(); })
            .RequireFeature(ModuleKeys.Pdv, Permissions.SalesCreate);

        // ---------- Faturas do sistema (Mercado Pago) ----------
        // Leitura e pagamento continuam liberados com licença restrita: é assim que o cliente regulariza.
        var billing = api.MapGroup("/billing");
        billing.MapGet("/invoices", async (AppDbContext db, BillingService b) =>
            Results.Ok((await db.Invoices.AsNoTracking().OrderByDescending(i => i.DueAt).Take(60).ToListAsync()).Select(b.View)))
            .RequireFeature(ModuleKeys.Core, Permissions.BillingManage);
        billing.MapPost("/invoices/{id:guid}/pix", async (Guid id, BillingService b, CancellationToken ct) => Results.Ok(await b.PixAsync(id, ct)))
            .RequireFeature(ModuleKeys.Core, Permissions.BillingManage);
        billing.MapPost("/invoices/{id:guid}/checkout", async (Guid id, BillingService b, CancellationToken ct) => Results.Ok(await b.CheckoutAsync(id, ct)))
            .RequireFeature(ModuleKeys.Core, Permissions.BillingManage);
        billing.MapPost("/invoices/{id:guid}/refresh", async (Guid id, BillingService b, CancellationToken ct) => Results.Ok(await b.RefreshAsync(id, ct)))
            .RequireFeature(ModuleKeys.Core, Permissions.BillingManage);
    }

    private static void Validate(ProductInput r)
    {
        if (string.IsNullOrWhiteSpace(r.Sku) || r.Sku.Length > 60) throw new DomainException("invalid_sku", "SKU inválido.");
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) throw new DomainException("invalid_name", "Nome inválido.");
        if (r.Barcode is { Length: > 32 }) throw new DomainException("invalid_barcode", "Código de barras inválido.");
        if (r.PriceCents < 0 || r.PriceCents > 100_000_000 || r.CostCents < 0 || r.CostCents > 100_000_000) throw new DomainException("invalid_price", "Preço ou custo inválido.");
        if (r.Unit is not ("UN" or "KG" or "G" or "L" or "ML" or "M" or "CX" or "PC")) throw new DomainException("invalid_unit", "Unidade inválida.");
        if (r.MinStock < 0) throw new DomainException("invalid_min_stock", "Estoque mínimo inválido.");
        if (FiscalRules.ValidateProduct(r.Ncm, r.Cest, r.Cfop, r.FiscalOrigin, r.IcmsCst, r.IbsCbsCst, r.IbsCbsClass) is { } fe) throw new DomainException("invalid_fiscal", fe);
    }

    private static void Apply(Product p, ProductInput r)
    {
        p.Sku = r.Sku.Trim(); p.Barcode = r.Barcode?.Trim(); p.Name = r.Name.Trim(); p.Description = r.Description;
        p.Unit = r.Unit; p.PriceCents = r.PriceCents; p.CostCents = r.CostCents; p.MinStock = r.MinStock; p.CategoryId = r.CategoryId;
        static string? D(string? s) => string.IsNullOrWhiteSpace(s) ? null : FiscalRules.Digits(s);
        p.Ncm = D(r.Ncm); p.Cest = D(r.Cest); p.Cfop = D(r.Cfop); p.FiscalOrigin = r.FiscalOrigin; p.IcmsCst = D(r.IcmsCst);
        p.IbsCbsCst = D(r.IbsCbsCst); p.IbsCbsClass = D(r.IbsCbsClass);
    }
}

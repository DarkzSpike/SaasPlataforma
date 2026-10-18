using Comercial.Api.Security;
using Comercial.Api.Services;
using Comercial.Domain;
using Comercial.Domain.Modules;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Comercial.Shared.Branding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Endpoints;

public sealed record SuspendRequest(string ConfirmTenantCode, string Reason);
public sealed record ConfirmRequest(string ConfirmTenantCode);
public sealed record CreateInvoiceRequest(int? DueInDays);

public static class AdminEndpoints
{
    public const string ReauthHeader = "X-Reauth-Totp";

    private static RouteHandlerBuilder AdminOnly(this RouteHandlerBuilder b) => b.AddEndpointFilter(async (ctx, next) =>
    {
        var a = ctx.HttpContext.RequestServices.GetRequiredService<RequestActor>();
        return a.AdminRole == AdminRoles.SaasAdmin ? await next(ctx) : Results.Json(new { status = 403, code = "admin_role_required" }, statusCode: 403);
    });

    /// <summary>Operação crítica: exige código TOTP atual no cabeçalho (reautenticação).</summary>
    private static RouteHandlerBuilder Reauth(this RouteHandlerBuilder b) => b.AddEndpointFilter(async (ctx, next) =>
    {
        var auth = ctx.HttpContext.RequestServices.GetRequiredService<AuthService>();
        await auth.RequireAdminReauthAsync(ctx.HttpContext.Request.Headers[ReauthHeader]);
        return await next(ctx);
    });

    public static void MapAdminApi(this WebApplication app)
    {
        app.MapPost("/admin/v1/auth/login", async (AdminLoginRequest r, AuthService s) => Results.Ok(new { accessToken = await s.AdminLoginAsync(r) }))
            .RequireRateLimiting("auth");

        var g = app.MapGroup("/admin/v1").AddEndpointFilter(async (ctx, next) =>
        {
            var a = ctx.HttpContext.RequestServices.GetRequiredService<RequestActor>();
            return a.IsAdmin ? await next(ctx) : Results.Json(new { status = 401, code = "unauthorized" }, statusCode: 401);
        });

        g.MapGet("/me", async (RequestActor a, AppDbContext db) =>
            Results.Ok(await db.AdminUsers.AsNoTracking().Where(x => x.Id == a.AdminId).Select(x => new { x.Id, x.Email, x.DisplayName, x.Role }).FirstAsync()));

        // ---------- Empresas ----------
        g.MapGet("/tenants", async (AppDbContext db, string? q) =>
        {
            var query = db.Tenants.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(t => t.Code.Contains(q) || t.TradeName.Contains(q) || t.LegalName.Contains(q) || t.Document == q);
            var tenants = await query.OrderBy(t => t.TradeName).Take(200).ToListAsync();
            var ids = tenants.Select(t => t.Id).ToList();
            var subs = await db.Subscriptions.IgnoreQueryFilters().AsNoTracking().Where(s => ids.Contains(s.TenantId)).ToListAsync();
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(tenants.Select(t =>
            {
                var sub = subs.Where(s => s.TenantId == t.Id).OrderByDescending(s => s.StartedAt).FirstOrDefault();
                return new { t.Id, t.Code, t.TradeName, t.LegalName, status = t.Status.ToString(), plan = sub?.PlanCode, license = LicenseService.ComputeState(t, sub, now), periodEnd = sub?.CurrentPeriodEnd };
            }));
        });
        g.MapPost("/tenants", async (CreateTenantRequest r, TenantProvisioningService s) =>
        {
            var t = await s.CreateAsync(r);
            return Results.Created($"/admin/v1/tenants/{t.Id}", new { t.Id, t.Code });
        }).AdminOnly();
        g.MapGet("/tenants/{id:guid}", async (Guid id, AppDbContext db, LicenseService l) =>
        {
            var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Results.NotFound();
            var (_, sub, state) = await l.GetStateAsync(id);
            var modules = await db.TenantModules.IgnoreQueryFilters().AsNoTracking().Where(m => m.TenantId == id).ToListAsync();
            var devices = await db.Devices.IgnoreQueryFilters().AsNoTracking().Where(d => d.TenantId == id)
                .Select(d => new { d.Id, d.Name, d.Platform, status = d.Status.ToString(), d.AppVersion, d.LastSeenAt, d.LastSyncAt, d.LastError }).ToListAsync();
            var users = await db.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId == id && u.IsActive);
            var stores = await db.Stores.IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantId == id).Select(s => new { s.Id, s.Code, s.Name, s.IsActive }).ToListAsync();
            var invoices = await db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(i => i.TenantId == id).OrderByDescending(i => i.DueAt).Take(12).ToListAsync();
            return Results.Ok(new
            {
                tenant = t, subscription = sub, license = state, effectiveModules = (await l.GetEffectiveModulesAsync(id)).Order(),
                modules, devices, activeUsers = users, stores, invoices,
            });
        });
        g.MapPost("/tenants/{id:guid}/suspend", async (Guid id, SuspendRequest r, AppDbContext db, AuditService audit) =>
        {
            var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == id) ?? throw new DomainException("not_found", "Empresa não encontrada.", 404);
            if (r.ConfirmTenantCode != t.Code) throw new DomainException("confirmation_mismatch", "Código de confirmação não confere.", 409);
            if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length < 5) throw new DomainException("reason_required", "Informe o motivo.");
            t.Status = TenantStatus.Suspended; t.SuspensionReason = r.Reason;
            audit.Add("tenant.suspended", id, nameof(Tenant), id, new { r.Reason });
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).AdminOnly().Reauth();
        g.MapPost("/tenants/{id:guid}/reactivate", async (Guid id, ConfirmRequest r, AppDbContext db, AuditService audit) =>
        {
            var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == id) ?? throw new DomainException("not_found", "Empresa não encontrada.", 404);
            if (r.ConfirmTenantCode != t.Code) throw new DomainException("confirmation_mismatch", "Código de confirmação não confere.", 409);
            t.Status = TenantStatus.Active; t.SuspensionReason = null;
            audit.Add("tenant.reactivated", id, nameof(Tenant), id);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).AdminOnly().Reauth();
        g.MapPost("/tenants/{id:guid}/invoices", async (Guid id, CreateInvoiceRequest r, AppDbContext db, AuditService audit) =>
        {
            var sub = await db.Subscriptions.IgnoreQueryFilters().Where(s => s.TenantId == id && s.Status != SubscriptionStatus.Cancelled).OrderByDescending(s => s.StartedAt).FirstOrDefaultAsync()
                      ?? throw new DomainException("no_subscription", "Empresa sem assinatura.", 404);
            var inv = new Invoice
            {
                TenantId = id, SubscriptionId = sub.Id, AmountCents = sub.PriceCents, DueAt = DateTimeOffset.UtcNow.AddDays(r.DueInDays ?? 5),
                PeriodStart = sub.CurrentPeriodEnd, PeriodEnd = sub.CurrentPeriodEnd.AddMonths((int)sub.Period),
            };
            db.Invoices.Add(inv);
            audit.Add("invoice.created", id, nameof(Invoice), inv.Id, new { inv.AmountCents, inv.DueAt });
            await db.SaveChangesAsync();
            return Results.Ok(inv);
        }).AdminOnly();
        g.MapGet("/tenants/{id:guid}/audit", async (Guid id, AppDbContext db, int? take) =>
            Results.Ok(await db.AuditLogs.AsNoTracking().Where(a => a.TenantId == id).OrderByDescending(a => a.Id).Take(Math.Clamp(take ?? 200, 1, 1000)).ToListAsync()));

        // ---------- Módulos ----------
        g.MapGet("/modules", async (AppDbContext db) => Results.Ok(await db.ModuleCatalog.AsNoTracking().Include(m => m.Dependencies).OrderBy(m => m.Category).ThenBy(m => m.Name).ToListAsync()));
        g.MapGet("/tenants/{id:guid}/modules/{key}/impact", async (Guid id, string key, bool enable, ModuleAdminService s) => Results.Ok(await s.ImpactAsync(id, key, enable)));
        g.MapPost("/tenants/{id:guid}/modules/{key}/enable", async (Guid id, string key, ModuleChangeRequest r, ModuleAdminService s) => Results.Ok(await s.SetAsync(id, key, true, r)))
            .AdminOnly().Reauth();
        g.MapPost("/tenants/{id:guid}/modules/{key}/disable", async (Guid id, string key, ModuleChangeRequest r, ModuleAdminService s) => Results.Ok(await s.SetAsync(id, key, false, r)))
            .AdminOnly().Reauth();

        // ---------- Planos ----------
        g.MapGet("/plans", async (AppDbContext db) => Results.Ok(await db.Plans.AsNoTracking().Include(p => p.Modules).OrderBy(p => p.PriceCents).ToListAsync()));
        g.MapPut("/plans", async (PlanInput r, TenantProvisioningService s) => Results.Ok(await s.UpsertPlanAsync(r))).AdminOnly().Reauth();

        // ---------- Suporte ----------
        g.MapPost("/tenants/{id:guid}/support-sessions", async (Guid id, StartSupportRequest r, SupportService s) => Results.Ok(await s.StartAsync(id, r)));
        g.MapPost("/support-sessions/{id:guid}/elevation-requests", async (Guid id, ElevationRequestInput r, SupportService s) => Results.Ok(await s.RequestElevationAsync(id, r)));
        g.MapPost("/support-sessions/{id:guid}/end", async (Guid id, SupportService s) => { await s.EndAsync(id, "encerrada pelo suporte", byTenant: false); return Results.NoContent(); });
        g.MapGet("/support-sessions/{id:guid}/diagnostics", async (Guid id, AppDbContext db, RequestActor a, LicenseService l, AuditService audit) =>
        {
            var s = await db.SupportSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.AdminUserId == a.AdminId);
            if (s is null || !s.IsActive(DateTimeOffset.UtcNow)) return Results.Json(new { status = 403, code = "support_session_required" }, statusCode: 403);
            var t = s.TenantId;
            var data = new
            {
                session = new { s.Id, s.Mode, s.ExpiresAt, s.ElevatedUntil, s.AllowedOperations },
                license = (await l.GetStateAsync(t)).State,
                modules = (await l.GetEffectiveModulesAsync(t)).Order(),
                devices = await db.Devices.IgnoreQueryFilters().AsNoTracking().Where(d => d.TenantId == t)
                    .Select(d => new { d.Id, d.Name, d.Platform, status = d.Status.ToString(), d.AppVersion, d.LastSyncAt, d.LastError }).ToListAsync(),
                syncRejected = await db.SyncEvents.IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == t && e.Status == "rejected")
                    .OrderByDescending(e => e.ReceivedAt).Take(50).Select(e => new { e.Type, e.IdempotencyKey, e.ResultJson, e.ReceivedAt }).ToListAsync(),
                fiscalPending = await db.FiscalDocuments.IgnoreQueryFilters().CountAsync(f => f.TenantId == t && f.Status == FiscalStatus.PendingTransmission),
            };
            audit.Add("support.diagnostics_viewed", t, nameof(SupportSession), s.Id);
            await db.SaveChangesAsync();
            return Results.Ok(data);
        });

        // ---------- Identidade visual ----------
        var br = g.MapGroup("/tenants/{id:guid}/branding");
        br.MapGet("/draft", async (Guid id, BrandingService s) => Results.Ok(await s.GetDraftAsync(id)));
        br.MapPut("/draft", async (Guid id, BrandingConfig cfg, BrandingService s) => Results.Ok(await s.SaveDraftAsync(id, cfg))).AdminOnly();
        br.MapPost("/preview", async (Guid id, BrandingConfig cfg, BrandingService s) =>
        {
            // Pré-visualização: só valida e devolve; nada é gravado.
            await s.ValidateAsync(id, cfg);
            return Results.Ok(new { valid = true, config = cfg, contrast = BrandingRules.ContrastRatio(cfg.TextColor, cfg.BackgroundColor) });
        });
        br.MapGet("/presets", () => Results.Ok(BrandingRules.Themes.Select(BrandingRules.Preset)));
        br.MapPost("/publish", async (Guid id, PublishBrandingRequest r, BrandingService s) => Results.Ok(await s.PublishAsync(id, r))).AdminOnly();
        br.MapPost("/versions/{version:int}/restore", async (Guid id, int version, PublishBrandingRequest r, BrandingService s) => Results.Ok(await s.PublishAsync(id, r, version))).AdminOnly();
        br.MapPost("/reset", async (Guid id, ConfirmRequest r, BrandingService s) => { await s.ResetToDefaultAsync(id, r.ConfirmTenantCode); return Results.NoContent(); }).AdminOnly();
        br.MapGet("/versions", async (Guid id, BrandingService s) => Results.Ok(await s.VersionsAsync(id)));
        br.MapGet("/published", async (Guid id, BrandingService s) => Results.Ok(await s.GetPublishedAsync(id)));
        br.MapPost("/assets", async (Guid id, [FromForm] string kind, IFormFile file, BrandingService s) =>
        {
            var a = await s.UploadAsync(id, kind, file);
            return Results.Ok(new { a.Id, a.Kind, a.ContentType, a.Width, a.Height, a.SizeBytes });
        }).AdminOnly().DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(3 * 1024 * 1024));
        br.MapGet("/assets/{assetId:guid}", async (Guid id, Guid assetId, BrandingService s) =>
        {
            var r = await s.ReadAssetAsync(id, assetId);
            return r is null ? Results.NotFound() : Results.File(r.Value.Bytes, r.Value.Asset.ContentType);
        });

        // ---------- Diagnóstico e auditoria ----------
        g.MapGet("/diagnostics", async (AppDbContext db) =>
        {
            var since = DateTimeOffset.UtcNow.AddDays(-1);
            var staleSync = DateTimeOffset.UtcNow.AddHours(-24);
            return Results.Ok(new
            {
                database = await db.Database.CanConnectAsync() ? "ok" : "fail",
                tenants = await db.Tenants.CountAsync(),
                activeDevices = await db.Devices.IgnoreQueryFilters().CountAsync(d => d.Status == DeviceStatus.Active),
                devicesWithoutSync24h = await db.Devices.IgnoreQueryFilters().CountAsync(d => d.Status == DeviceStatus.Active && (d.LastSyncAt == null || d.LastSyncAt < staleSync)),
                versions = await db.Devices.IgnoreQueryFilters().Where(d => d.Status == DeviceStatus.Active).GroupBy(d => d.AppVersion).Select(x => new { version = x.Key, count = x.Count() }).ToListAsync(),
                syncRejected24h = await db.SyncEvents.IgnoreQueryFilters().CountAsync(e => e.Status == "rejected" && e.ReceivedAt >= since),
                fiscalPending = await db.FiscalDocuments.IgnoreQueryFilters().CountAsync(f => f.Status == FiscalStatus.PendingTransmission),
                securityEvents24h = await db.AuditLogs.Where(a => a.Action.StartsWith("security.") && a.OccurredAt >= since)
                    .GroupBy(a => a.Action).Select(x => new { action = x.Key, count = x.Count() }).ToListAsync(),
                backups = "Monitorado fora da aplicação (ver docs/OPERACAO.md). Status não integrado nesta versão.",
            });
        });
        g.MapGet("/audit/verify", async (AppDbContext db) =>
        {
            var (ok, broken, count) = await AuditService.VerifyChainAsync(db);
            return Results.Ok(new { ok, brokenAtId = broken, records = count });
        }).AdminOnly();
        g.MapGet("/audit", async (AppDbContext db, string? action, int? take) =>
        {
            var q = db.AuditLogs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action));
            return Results.Ok(await q.OrderByDescending(a => a.Id).Take(Math.Clamp(take ?? 200, 1, 1000)).ToListAsync());
        }).AdminOnly();
    }
}

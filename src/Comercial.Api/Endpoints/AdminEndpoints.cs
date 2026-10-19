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
public sealed record TestEmailRequest(string To);

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
        // Fatura do período atual (não duplica: se já existir, devolve a mesma).
        g.MapPost("/tenants/{id:guid}/invoices", async (Guid id, CreateInvoiceRequest r, BillingService b) => Results.Ok(await b.EnsureCurrentInvoiceAsync(id, r.DueInDays ?? 5)))
            .AdminOnly();

        // ---------- Faturamento (todas as empresas) ----------
        g.MapGet("/invoices/upcoming", async (BillingService b, int? days) => Results.Ok(await b.UpcomingAsync(days ?? 3)));
        g.MapPost("/billing/run", async (BillingService b) => Results.Ok(new { created = await b.GenerateDueInvoicesAsync(), reminders = await b.SendRemindersAsync() }))
            .AdminOnly();
        g.MapPost("/invoices/{invoiceId:guid}/mark-paid", async (Guid invoiceId, SuspendRequest r, BillingService b) =>
            Results.Ok(await b.MarkPaidManuallyAsync(invoiceId, r.ConfirmTenantCode, r.Reason))).AdminOnly().Reauth();

        // ---------- Versão do caixa (atualização automática) ----------
        g.MapGet("/settings/desktop-release", async (PlatformSettingsService s) => Results.Ok(await s.DesktopReleaseAsync())).AdminOnly();
        g.MapPut("/settings/desktop-release", async (DesktopReleaseRequest r, PlatformSettingsService s, IHostEnvironment env) =>
        {
            await s.SaveDesktopReleaseAsync(r, env.IsDevelopment() || env.EnvironmentName == "Testing");
            return Results.Ok(await s.DesktopReleaseAsync());
        }).AdminOnly().Reauth();

        // ---------- Cadastro com teste grátis ----------
        g.MapGet("/settings/signup", async (PlatformSettingsService s) => Results.Ok(await s.SignupAsync())).AdminOnly();
        g.MapPut("/settings/signup", async (SignupSettingsRequest r, PlatformSettingsService s) => { await s.SaveSignupAsync(r); return Results.Ok(await s.SignupAsync()); }).AdminOnly().Reauth();

        // ---------- E-mail (SMTP da plataforma) ----------
        g.MapGet("/settings/smtp", async (PlatformSettingsService s) => Results.Ok(await s.SmtpViewAsync())).AdminOnly();
        g.MapPut("/settings/smtp", async (SmtpSettingsRequest r, PlatformSettingsService s) => { await s.SaveSmtpAsync(r); return Results.Ok(await s.SmtpViewAsync()); })
            .AdminOnly().Reauth();
        g.MapPost("/settings/smtp/test", async (TestEmailRequest r, EmailService e, EmailSender sender, AppDbContext db) =>
        {
            if (!System.Net.Mail.MailAddress.TryCreate(r.To, out _)) throw new DomainException("invalid_email", "E-mail inválido.");
            e.Simple(null, r.To, "Teste de e-mail do Sistema Comercial", "Teste de e-mail", "Se você recebeu esta mensagem, o envio de e-mails está funcionando.", "test");
            await db.SaveChangesAsync();
            await sender.SendPendingAsync();
            var last = await db.EmailMessages.AsNoTracking().Where(m => m.Kind == "test").OrderByDescending(m => m.CreatedAt).FirstAsync();
            return Results.Ok(new { ok = last.Status == EmailStatus.Sent, message = last.Status == EmailStatus.Sent ? "E-mail enviado." : "Falhou: " + last.LastError });
        }).AdminOnly();
        g.MapGet("/emails", async (AppDbContext db) => Results.Ok(await db.EmailMessages.AsNoTracking().OrderByDescending(m => m.CreatedAt).Take(100)
            .Select(m => new { m.Id, m.To, m.Subject, m.Kind, m.Status, m.Attempts, m.LastError, m.CreatedAt, m.SentAt }).ToListAsync())).AdminOnly();

        // ---------- NFS-e das mensalidades (exige CNPJ) ----------
        g.MapGet("/settings/nfse", async (PlatformSettingsService s) => Results.Ok(await s.NfseViewAsync())).AdminOnly();
        g.MapPut("/settings/nfse", async (NfseSettingsRequest r, PlatformSettingsService s) => { await s.SaveNfseAsync(r); return Results.Ok(await s.NfseViewAsync()); })
            .AdminOnly().Reauth();
        g.MapGet("/nfse", async (AppDbContext db) => Results.Ok(await db.NfseDocuments.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync())).AdminOnly();
        g.MapPost("/nfse/run", async (NfseService s, CancellationToken ct) => Results.Ok(new { processed = await s.ProcessAsync(ct) })).AdminOnly();

        // ---------- Mercado Pago (credenciais da plataforma) ----------
        g.MapGet("/settings/mercadopago", async (PlatformSettingsService s, HttpRequest req) =>
            Results.Ok(await s.MercadoPagoViewAsync($"{req.Scheme}://{req.Host}"))).AdminOnly();
        g.MapPut("/settings/mercadopago", async (MercadoPagoSettingsRequest r, PlatformSettingsService s, HttpRequest req) =>
        {
            await s.SaveMercadoPagoAsync(r);
            return Results.Ok(await s.MercadoPagoViewAsync($"{req.Scheme}://{req.Host}"));
        }).AdminOnly().Reauth();
        g.MapPost("/settings/mercadopago/test", async (PlatformSettingsService s, IMercadoPagoClient mp, CancellationToken ct) =>
        {
            var cfg = await s.MercadoPagoAsync();
            if (cfg is null) return Results.Ok(new { ok = false, message = "Access Token não configurado." });
            var (ok, message) = await mp.CheckAsync(cfg.AccessToken, ct);
            return Results.Ok(new { ok, message });
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

        // ---------- Armazenamento de arquivos por empresa (pasta + API do Google) ----------
        g.MapGet("/tenants/{id:guid}/storage", async (Guid id, TenantStorageService s) => Results.Ok(await s.GetAsync(id)));
        g.MapPut("/tenants/{id:guid}/storage", async (Guid id, StorageSettingsRequest r, TenantStorageService s) => Results.Ok(await s.SaveAsync(id, r)))
            .AdminOnly().Reauth();
        g.MapPost("/tenants/{id:guid}/storage/test", async (Guid id, TenantStorageService s, CancellationToken ct) => Results.Ok(await s.TestAsync(id, ct)))
            .AdminOnly();

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

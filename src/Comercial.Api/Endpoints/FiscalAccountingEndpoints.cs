using System.Text;
using Comercial.Api.Security;
using Comercial.Api.Services;
using Comercial.Domain;
using Comercial.Domain.Modules;
using Comercial.Domain.Security;
using Microsoft.AspNetCore.Mvc;

namespace Comercial.Api.Endpoints;

/// <summary>Fiscal e Contabilidade: visão geral, documentos, arquivo de XMLs, exportação, relatórios, fechamento e contador.</summary>
public static class FiscalAccountingEndpoints
{
    public static void MapFiscalAccountingApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/fiscal").AddEndpointFilter(async (ctx, next) =>
        {
            var a = ctx.HttpContext.RequestServices.GetRequiredService<RequestActor>();
            return a.IsTenantUser || a.IsSupport ? await next(ctx) : Results.Json(new { status = 401, code = "unauthorized" }, statusCode: 401);
        });
        const string F = ModuleKeys.Fiscal;

        // ---------- Certificado digital A1 (emissão direta) ----------
        api.MapPost("/certificate", async ([FromForm] string password, IFormFile file, FiscalService s) =>
        {
            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            return Results.Ok(await s.SaveCertificateAsync(ms.ToArray(), password ?? ""));
        }).RequireFeature(F, Permissions.PaymentsConfigure, write: true).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(200_000));

        api.MapGet("/mode", async (Comercial.Infrastructure.Data.AppDbContext db) =>
        {
            var p = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(db.FiscalProfiles);
            return Results.Ok(new { mode = p is null || p.Provider == "none" ? NfceModes.Never : p.EmissionMode });
        }).RequireFeature(F, Permissions.FiscalEmit);

        // ---------- DANFE NFC-e (o caixa chama logo depois de sincronizar a venda) ----------
        api.MapPost("/nfce/sale/{key}", async (string key, DanfeService s, CancellationToken ct) => Results.Ok(await s.ForSaleAsync(key, emit: true, ct)))
            .RequireFeature(F, Permissions.FiscalEmit, write: true);
        api.MapGet("/nfce/sale/{key}", async (string key, DanfeService s, CancellationToken ct) => Results.Ok(await s.ForSaleAsync(key, emit: false, ct)))
            .RequireFeature(F, Permissions.FiscalEmit);

        // ---------- Visão geral ----------
        api.MapGet("/overview", async (AccountingReportService r, DateOnly from, DateOnly to, Guid? storeId) =>
            Results.Ok(await r.OverviewAsync(await r.PeriodAsync(from, to), storeId))).RequireFeature(F, Permissions.FiscalRead);

        // ---------- Documentos e arquivo de XMLs ----------
        api.MapGet("/docs", async (FiscalArchiveService s, DateOnly? from, DateOnly? to, string? status, int? number, int? series, string? key, int? model, Guid? storeId,
            int? page, int? pageSize, AccountingReportService r) =>
        {
            DateTimeOffset? f = null, t = null;
            if (from is not null || to is not null)
            {
                var p = await r.PeriodAsync(from ?? DateOnly.FromDateTime(DateTime.Today.AddYears(-1)), to ?? DateOnly.FromDateTime(DateTime.Today));
                f = p.From; t = p.To;
            }
            return Results.Ok(await s.ListAsync(new FiscalDocumentFilter(f, t, status, number, series, key, model, storeId, page ?? 1, pageSize ?? 50)));
        }).RequireFeature(F, Permissions.FiscalRead);
        api.MapGet("/docs/{id:guid}", async (Guid id, FiscalArchiveService s) => Results.Ok(await s.DetailAsync(id))).RequireFeature(F, Permissions.FiscalRead);
        api.MapGet("/docs/{id:guid}/xml", async (Guid id, string? kind, FiscalArchiveService s) =>
        {
            var k = kind == FiscalXmlKind.Cancellation ? FiscalXmlKind.Cancellation : FiscalXmlKind.Authorization;
            var (bytes, name) = await s.XmlAsync(id, k);
            return Results.File(bytes, "application/xml", name);
        }).RequireFeature(F, Permissions.FiscalExport);
        api.MapGet("/voids", async (FiscalArchiveService s) => Results.Ok(await s.VoidsAsync())).RequireFeature(F, Permissions.FiscalRead);
        // Busca agora no provedor os XMLs que faltam (sem esperar o serviço automático).
        api.MapPost("/archive/fetch", async (FiscalArchiveService s, RequestActor a, AuditService audit, CancellationToken ct) =>
        {
            var n = await s.FetchPendingAsync(a.TenantId, ct, ignoreWait: true);
            return Results.Ok(new { stored = n });
        }).RequireFeature(F, Permissions.FiscalExport, write: true);

        // ---------- Relatórios ----------
        api.MapGet("/reports/{key}", async (string key, AccountingReportService r, DateOnly from, DateOnly to, Guid? storeId, string? format) =>
        {
            var t = await r.ReportAsync(key, await r.PeriodAsync(from, to), storeId);
            return format == "csv"
                ? Results.File(Encoding.UTF8.GetBytes(AccountingReportService.Csv(t)), "text/csv; charset=utf-8", $"{key}-{from:yyyyMMdd}-{to:yyyyMMdd}.csv")
                : Results.Ok(t);
        }).RequireFeature(F, Permissions.FiscalRead);
        api.MapGet("/reports/caixa/{id:guid}", async (Guid id, AccountingReportService r, string? format) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var t = await r.CashSessionAsync(id, await r.PeriodAsync(today, today));
            return format == "csv" ? Results.File(Encoding.UTF8.GetBytes(AccountingReportService.Csv(t)), "text/csv; charset=utf-8", $"caixa-{id:N}.csv") : Results.Ok(t);
        }).RequireFeature(F, Permissions.FiscalRead);

        // ---------- Exportação ----------
        api.MapGet("/exports", async (FiscalExportService s, int? year, int? month) => Results.Ok(await s.ListAsync(year, month))).RequireFeature(F, Permissions.FiscalRead);
        api.MapPost("/exports", async (CreateExportRequest req, FiscalExportService s, CancellationToken ct) =>
        {
            var (e, reused) = await s.CreateAsync(req, "manual", ct);
            return Results.Ok(new { export = FiscalExportService.View(e), reused });
        }).RequireFeature(F, Permissions.FiscalExport, write: true);
        api.MapGet("/exports/{id:guid}/download", async (Guid id, FiscalExportService s) =>
        {
            var (e, path) = await s.FileAsync(id);
            return Results.File(path, "application/zip", e.FileName);
        }).RequireFeature(F, Permissions.FiscalExport);

        // ---------- Fechamento mensal ----------
        api.MapGet("/closing/{year:int}/{month:int}", async (int year, int month, MonthClosingService s) => Results.Ok(await s.GetAsync(year, month)))
            .RequireFeature(F, Permissions.FiscalRead);
        api.MapPost("/closing/{year:int}/{month:int}/close", async (int year, int month, CloseMonthRequest r, MonthClosingService s) => Results.Ok(await s.CloseAsync(year, month, r)))
            .RequireFeature(F, Permissions.FiscalClose, write: true);
        api.MapPost("/closing/{year:int}/{month:int}/reopen", async (int year, int month, CloseMonthRequest r, MonthClosingService s) => Results.Ok(await s.ReopenAsync(year, month, r)))
            .RequireFeature(F, Permissions.FiscalClose, write: true);

        // ---------- Contador ----------
        api.MapGet("/accountant", async (AccountantService s) => Results.Ok(await s.GetAsync())).RequireFeature(F, Permissions.FiscalRead);
        api.MapPut("/accountant", async (AccountantSettingsRequest r, AccountantService s) => Results.Ok(await s.SaveAsync(r))).RequireFeature(F, Permissions.FiscalSend, write: true);
        api.MapPost("/accountant/send", async ([FromBody] SendExportRequest r, AccountantService s) =>
        {
            var d = await s.SendAsync(r.ExportId, "manual");
            return Results.Ok(new { d.Id, d.Status, d.Trigger, d.Recipients });
        }).RequireFeature(F, Permissions.FiscalSend, write: true);
        api.MapGet("/accountant/deliveries", async (AccountantService s) => Results.Ok(await s.HistoryAsync())).RequireFeature(F, Permissions.FiscalRead);
    }
}

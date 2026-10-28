using System.IO.Compression;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public static class CheckState
{
    public const string Pending = "pendente";
    public const string Review = "em_analise";
    public const string Done = "concluido";
    public const string Divergent = "divergencia";
    public const string NotApplicable = "nao_aplicavel";
}

public sealed record ClosingCheck(string Key, string Title, string State, string Detail, List<string> Items);
public sealed record CloseMonthRequest(string? Notes);
public sealed record CreateExportRequest(int Year, int Month, Guid? StoreId, bool IncludeReports = true);
public sealed record AccountantSettingsRequest(string OfficeName, string? ContactName, string Email, string? ExtraEmails, bool AutoSend, int DayOfMonth, int Hour,
    bool IncludeXml = true, bool IncludeReports = true);
public sealed record SendExportRequest(Guid ExportId);

/// <summary>
/// Fechamento fiscal mensal: roda as verificações e registra a conclusão. Só lê os documentos fiscais — nunca altera, cancela ou apaga.
/// </summary>
public sealed class MonthClosingService(AppDbContext db, AccountingReportService reports, RequestActor actor, AuditService audit, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(AccountingPeriod Period, List<ClosingCheck> Checks)> ChecksAsync(int year, int month)
    {
        var p = await reports.MonthAsync(year, month);
        var fiscalOn = await reports.FiscalEnabledAsync();
        var div = await reports.DivergencesAsync(p, null);
        List<string> Items(params string[] types) => div.Where(d => types.Contains(d.Type)).Take(50).Select(d => $"{d.When} • venda {d.Sale} • {d.Document} • {d.Detail}").ToList();
        int Count(params string[] types) => div.Count(d => types.Contains(d.Type));
        var checks = new List<ClosingCheck>();
        ClosingCheck Fiscal(string key, string title, Func<ClosingCheck> f) => fiscalOn ? f() : new(key, title, CheckState.NotApplicable, "Módulo fiscal não está ativo nesta empresa (NFC-e não emitida).", []);

        var mode = await reports.EmissionModeAsync();
        checks.Add(fiscalOn && mode != NfceModes.Always
            ? new("documentos", "Todas as vendas têm documento fiscal registrado", CheckState.NotApplicable,
                mode == NfceModes.Never ? "A loja não emite NFC-e (só comprovante simples)." : "A loja emite NFC-e só quando o cliente pede.", [])
            : Fiscal("documentos", "Todas as vendas têm documento fiscal registrado", () => Count("venda_sem_documento") is var n and > 0
            ? new("documentos", "Todas as vendas têm documento fiscal registrado", CheckState.Divergent, $"{n} venda(s) sem documento fiscal.", Items("venda_sem_documento"))
            : new("documentos", "Todas as vendas têm documento fiscal registrado", CheckState.Done, "Todas as vendas concluídas têm documento registrado.", [])));
        checks.Add(Fiscal("xml", "Notas autorizadas com XML original guardado", () => Count("xml_ausente") is var n and > 0
            ? new("xml", "Notas autorizadas com XML original guardado", CheckState.Divergent, $"{n} nota(s) sem XML original no arquivo fiscal. Use \"Buscar XMLs no provedor\".", Items("xml_ausente"))
            : new("xml", "Notas autorizadas com XML original guardado", CheckState.Done, "Todos os XMLs autorizados estão arquivados e com hash conferível.", [])));
        checks.Add(Fiscal("eventos", "Cancelamentos com o XML do evento", () => Count("evento_ausente") is var n and > 0
            ? new("eventos", "Cancelamentos com o XML do evento", CheckState.Divergent, $"{n} cancelamento(s) sem o XML do evento.", Items("evento_ausente"))
            : new("eventos", "Cancelamentos com o XML do evento", CheckState.Done, "Eventos de cancelamento arquivados.", [])));
        checks.Add(Fiscal("rejeitados", "Documentos rejeitados ou denegados analisados", () => Count("documento_rejeitado", "documento_denegado") is var n and > 0
            ? new("rejeitados", "Documentos rejeitados ou denegados analisados", CheckState.Review, $"{n} documento(s) para analisar (corrigir e retransmitir, ou orientação do contador).", Items("documento_rejeitado", "documento_denegado"))
            : new("rejeitados", "Documentos rejeitados ou denegados analisados", CheckState.Done, "Nenhum documento rejeitado ou denegado.", [])));
        checks.Add(Fiscal("vendas_documento", "Vendas com o documento fiscal esperado", () =>
        {
            var wrong = Count("venda_cancelada_nota_autorizada", "nota_cancelada_venda_ativa");
            var pending = Count("documento_pendente");
            return wrong > 0
                ? new("vendas_documento", "Vendas com o documento fiscal esperado", CheckState.Divergent, $"{wrong} venda(s) com situação diferente da nota.", Items("venda_cancelada_nota_autorizada", "nota_cancelada_venda_ativa", "documento_pendente"))
                : pending > 0
                    ? new("vendas_documento", "Vendas com o documento fiscal esperado", CheckState.Pending, $"{pending} nota(s) ainda não autorizada(s) (transmissão/contingência).", Items("documento_pendente"))
                    : new("vendas_documento", "Vendas com o documento fiscal esperado", CheckState.Done, "Situação das vendas confere com as notas.", []);
        }));

        // Totais: pagamentos (menos troco) = total da venda; valor da nota (vNF) = total da venda.
        var sales = await db.Sales.AsNoTracking().Where(s => s.ReceivedAt >= p.From && s.ReceivedAt < p.To && s.Status == SaleStatus.Completed)
            .Select(s => new { s.Id, s.TotalCents, s.ChangeCents, s.ReceivedAt, paid = s.Payments.Sum(x => x.AmountCents) }).ToListAsync();
        var badTotals = sales.Where(s => s.paid - s.ChangeCents != s.TotalCents).Select(s => $"{p.Local(s.ReceivedAt)} • venda {s.Id.ToString("N")[..8]} • total {AccountingReportService.M(s.TotalCents)} × pago {AccountingReportService.M(s.paid - s.ChangeCents)}").ToList();
        badTotals.AddRange(Items("valor_divergente"));
        checks.Add(badTotals.Count > 0
            ? new("totais", "Totais financeiros conferem com as vendas", CheckState.Divergent, $"{badTotals.Count} diferença(s) entre total, pagamentos e notas.", badTotals.Take(50).ToList())
            : new("totais", "Totais financeiros conferem com as vendas", CheckState.Done, $"{sales.Count} venda(s): pagamentos e notas conferem com os totais.", []));

        var recon = await reports.ReconciliationAsync(p, null);
        var reconDiv = recon.Rows.Where(r => r[5] != "conciliado" && !r[5].StartsWith("não pago")).ToList();
        checks.Add(reconDiv.Count > 0
            ? new("conciliacao", "Conciliação de pagamentos", CheckState.Divergent, $"{reconDiv.Count} diferença(s) na conciliação PIX. Cartões: conferir no extrato da adquirente.", reconDiv.Take(50).Select(r => $"{r[0]} • {r[1]} • {r[5]}").ToList())
            : new("conciliacao", "Conciliação de pagamentos", CheckState.Done, "PIX do caixa conciliado com o Mercado Pago. Cartões: conferir no extrato da adquirente (sem integração).", []));

        var sessions = await db.CashSessions.AsNoTracking().Where(c => c.OpenedAt >= p.From && c.OpenedAt < p.To).ToListAsync();
        var open = sessions.Where(c => c.Status == CashSessionStatus.Open).ToList();
        var unreviewed = sessions.Where(c => c.Status == CashSessionStatus.Closed && c.DifferenceCents is not (null or 0) && c.ReviewedByUserId == null).ToList();
        checks.Add(open.Count > 0
            ? new("caixas", "Caixas do mês fechados e conferidos", CheckState.Pending, $"{open.Count} caixa(s) ainda aberto(s).", open.Select(c => "aberto em " + p.Local(c.OpenedAt)).ToList())
            : unreviewed.Count > 0
                ? new("caixas", "Caixas do mês fechados e conferidos", CheckState.Review, $"{unreviewed.Count} caixa(s) com diferença sem revisão.", unreviewed.Select(c => $"{p.Local(c.OpenedAt)} • diferença {AccountingReportService.M(c.DifferenceCents ?? 0)}").ToList())
                : new("caixas", "Caixas do mês fechados e conferidos", CheckState.Done, $"{sessions.Count} caixa(s) fechado(s).", []));

        var lastExport = await db.FiscalExports.AsNoTracking().Where(e => e.Year == year && e.Month == month && e.StoreId == null).OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync();
        var lastChange = new[]
        {
            await db.FiscalXmlFiles.AsNoTracking().Where(f => f.ReferenceAt >= p.From && f.ReferenceAt < p.To).MaxAsync(f => (DateTimeOffset?)f.StoredAt),
            await db.Sales.AsNoTracking().Where(s => s.ReceivedAt >= p.From && s.ReceivedAt < p.To).MaxAsync(s => (DateTimeOffset?)(s.CancelledAt ?? s.ReceivedAt)),
        }.Max();
        checks.Add(lastExport is null
            ? new("exportacao", "Arquivos e relatórios prontos para exportação", CheckState.Pending, "Nenhum pacote gerado para o mês. Gere em \"Exportação\".", [])
            : lastChange > lastExport.CreatedAt
                ? new("exportacao", "Arquivos e relatórios prontos para exportação", CheckState.Pending, "Houve mudanças depois do último pacote. Gere um novo.", [])
                : lastExport.MissingCount > 0
                    ? new("exportacao", "Arquivos e relatórios prontos para exportação", CheckState.Divergent, $"O pacote foi gerado com {lastExport.MissingCount} pendência(s).", [])
                    : new("exportacao", "Arquivos e relatórios prontos para exportação", CheckState.Done, $"Pacote de {lastExport.CreatedAt.ToOffset(TimeSpan.FromHours(-3)):dd/MM/yyyy HH:mm} completo.", []));
        return (p, checks);
    }

    public static string Summary(AccountingPeriod p, List<ClosingCheck> checks, string? notes) =>
        $"Fechamento fiscal — {p.Label}\r\n" +
        string.Join("\r\n", checks.Select(c => $"[{c.State}] {c.Title}: {c.Detail}")) +
        (string.IsNullOrWhiteSpace(notes) ? "" : "\r\nObservações: " + notes.Trim()) + "\r\n";

    public async Task<object> GetAsync(int year, int month)
    {
        var (p, checks) = await ChecksAsync(year, month);
        var c = await db.FiscalMonthClosings.AsNoTracking().FirstOrDefaultAsync(x => x.Year == year && x.Month == month);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        return new
        {
            year, month, period = p.Label, status = c?.Status ?? "nao_iniciado", notes = c?.Notes, closedAt = c?.ClosedAt,
            closedBy = c?.ClosedByUserId is { } u ? users.GetValueOrDefault(u) : null, checks, summary = Summary(p, checks, c?.Notes),
            // Na conclusão, o que foi verificado fica registrado (as verificações acima são as atuais).
            checksAtClosing = c?.Status == ClosingStatus.Closed ? JsonSerializer.Deserialize<List<ClosingCheck>>(c.ChecksJson, Json) : null,
        };
    }

    public async Task<object> CloseAsync(int year, int month, CloseMonthRequest r)
    {
        var (p, checks) = await ChecksAsync(year, month);
        var notes = (r.Notes ?? "").Trim();
        if (notes.Length > 2000) throw new DomainException("notes_too_long", "Observações: até 2000 caracteres.");
        var open = checks.Where(c => c.State is CheckState.Pending or CheckState.Divergent or CheckState.Review).ToList();
        if (open.Count > 0 && notes.Length < 10)
            throw new DomainException("closing_needs_notes", $"Há {open.Count} verificação(ões) não concluída(s). Descreva nas observações como foram tratadas (mín. 10 caracteres).", 409);
        var c = await db.FiscalMonthClosings.FirstOrDefaultAsync(x => x.Year == year && x.Month == month);
        if (c is null) { c = new FiscalMonthClosing { Year = year, Month = month }; db.FiscalMonthClosings.Add(c); }
        if (c.Status == ClosingStatus.Closed) throw new DomainException("already_closed", "Este mês já foi fechado.", 409);
        c.Status = ClosingStatus.Closed; c.Notes = notes.Length == 0 ? null : notes; c.ChecksJson = JsonSerializer.Serialize(checks, Json);
        c.LastCheckedAt = clock.GetUtcNow(); c.ClosedAt = clock.GetUtcNow(); c.ClosedByUserId = actor.UserId;
        c.LastExportId = await db.FiscalExports.Where(e => e.Year == year && e.Month == month).OrderByDescending(e => e.CreatedAt).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        audit.Add("fiscal.month_closed", actor.TenantId, nameof(FiscalMonthClosing), c.Id, new { year, month, open = open.Select(x => x.Key), notes = c.Notes });
        await db.SaveChangesAsync();
        return await GetAsync(year, month);
    }

    public async Task<object> ReopenAsync(int year, int month, CloseMonthRequest r)
    {
        var reason = (r.Notes ?? "").Trim();
        if (reason.Length < 10) throw new DomainException("reason_required", "Informe o motivo da reabertura (mín. 10 caracteres).");
        var c = await db.FiscalMonthClosings.FirstOrDefaultAsync(x => x.Year == year && x.Month == month && x.Status == ClosingStatus.Closed)
            ?? throw new DomainException("not_closed", "Este mês não está fechado.", 409);
        audit.Add("fiscal.month_reopened", actor.TenantId, nameof(FiscalMonthClosing), c.Id, new { year, month, reason, previousNotes = c.Notes, c.ClosedAt, c.ClosedByUserId });
        c.Status = ClosingStatus.Open; c.ClosedAt = null; c.ClosedByUserId = null;
        c.Notes = (c.Notes is null ? "" : c.Notes + "\n") + $"Reaberto em {clock.GetUtcNow():dd/MM/yyyy}: {reason}";
        if (c.Notes.Length > 2000) c.Notes = c.Notes[^2000..];
        await db.SaveChangesAsync();
        return await GetAsync(year, month);
    }
}

/// <summary>
/// Pacote ZIP para a contabilidade: XMLs ORIGINAIS (sem alteração) organizados por empresa/ano/mês/loja/modelo, eventos,
/// inutilizações, relatórios CSV, resumo e manifesto SHA-256. Pacote idêntico ao último do mês é reaproveitado.
/// </summary>
public sealed class FiscalExportService(AppDbContext db, AccountingReportService reports, MonthClosingService closing, RequestActor actor, AuditService audit,
    IConfiguration config, IHostEnvironment env, TimeProvider clock)
{
    private string Dir(Guid tenantId) => Path.GetFullPath(Path.Combine(env.ContentRootPath, config["Storage:Root"] ?? Path.Combine(AppContext.BaseDirectory, "..", "storage"),
        "fiscal-exports", tenantId.ToString("N")));
    public string PathOf(FiscalExport e) => Path.Combine(Dir(e.TenantId), e.StorageName);

    private static string Slug(string s)
    {
        var norm = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in norm)
            if (char.IsLetterOrDigit(ch) && ch < 128) sb.Append(ch);
            else if (ch is ' ' or '-' or '_') sb.Append('_');
        var r = sb.ToString().Trim('_');
        return r.Length == 0 ? "loja" : r.Length > 40 ? r[..40] : r;
    }

    private sealed record Entry(string Path, string Sha256, string Source);

    public async Task<(FiscalExport Export, bool Reused)> CreateAsync(CreateExportRequest r, string trigger = "manual", CancellationToken ct = default)
    {
        var p = await reports.MonthAsync(r.Year, r.Month);
        if (r.StoreId is { } sid && !await db.Stores.AnyAsync(s => s.Id == sid, ct)) throw new DomainException("not_found", "Loja não encontrada.", 404);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == actor.TenantId, ct);
        var profile = await db.FiscalProfiles.AsNoTracking().FirstOrDefaultAsync(ct);
        var stores = await db.Stores.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var company = profile?.Cnpj is { Length: 14 } cnpj ? cnpj : Slug(tenant.Code);
        var root = $"Exportacao_Contabilidade/{company}/{r.Year}/{r.Month:00}_{AccountingReportService.MonthName(r.Month)}";

        var docFiles = await (from f in db.FiscalXmlFiles.AsNoTracking()
                              where f.FiscalDocumentId != null && f.ReferenceAt >= p.From && f.ReferenceAt < p.To
                              join d in db.FiscalDocuments.AsNoTracking() on f.FiscalDocumentId equals d.Id
                              join s in db.Sales.AsNoTracking() on d.SaleId equals s.Id
                              where r.StoreId == null || s.StoreId == r.StoreId
                              orderby f.ReferenceAt
                              select new { f.Id, f.Kind, f.Model, f.AccessKey, f.Sha256, s.StoreId, d.Status, DocId = d.Id }).ToListAsync(ct);
        var voidFiles = await (from f in db.FiscalXmlFiles.AsNoTracking()
                               where f.NumberVoidId != null && f.ReferenceAt >= p.From && f.ReferenceAt < p.To
                               join v in db.FiscalNumberVoids.AsNoTracking() on f.NumberVoidId equals v.Id
                               select new { f.Id, f.Sha256, v.Series, v.FirstNumber, v.LastNumber }).ToListAsync(ct);
        var missingVoids = await db.FiscalNumberVoids.AsNoTracking()
            .Where(v => v.Status == "autorizada" && v.CreatedAt >= p.From && v.CreatedAt < p.To && !db.FiscalXmlFiles.Any(f => f.NumberVoidId == v.Id))
            .Select(v => new { v.Series, v.FirstNumber, v.LastNumber }).ToListAsync(ct);

        var entries = new List<Entry>();
        var paths = new Dictionary<Guid, string>();
        static string Model(int m) => m == 55 ? "NFe" : "NFCe";
        foreach (var f in docFiles)
        {
            var folder = $"{root}/Loja_{Slug(stores.GetValueOrDefault(f.StoreId, "loja"))}/{Model(f.Model)}/";
            var name = f.AccessKey ?? f.DocId.ToString("N");
            var path = f.Kind == FiscalXmlKind.Cancellation ? folder + $"eventos/{name}-110111-cancelamento-procEventoNFe.xml"
                : f.Status == FiscalStatus.Denied ? folder + $"denegadas/{name}-procNFe.xml" : folder + $"autorizadas/{name}-procNFe.xml";
            if (paths.ContainsValue(path)) continue; // nunca o mesmo arquivo duas vezes
            paths[f.Id] = path; entries.Add(new(path, f.Sha256, f.Id.ToString()));
        }
        foreach (var v in voidFiles)
        {
            var path = $"{root}/NFCe_inutilizacoes/inutilizacao-serie{v.Series}-{v.FirstNumber}-a-{v.LastNumber}-procInutNFe.xml";
            if (paths.ContainsValue(path)) continue;
            paths[v.Id] = path; entries.Add(new(path, v.Sha256, v.Id.ToString()));
        }

        // Pendências (documentos sem o XML oficial). Nada é reconstruído: só listado.
        var pending = await reports.PendingAsync(p, r.StoreId);
        var missing = pending.Rows.Select(x => $"{x[0]};venda {x[1]};{x[2]};{x[3]};{x[4]}").ToList();
        missing.AddRange(missingVoids.Select(v => $"inutilização série {v.Series} {v.FirstNumber}-{v.LastNumber};sem XML arquivado"));

        var reportFiles = new List<(string Path, byte[] Bytes)>();
        if (r.IncludeReports)
            foreach (var key in AccountingReportService.ReportKeys)
                reportFiles.Add(($"{root}/Relatorios/{key}.csv", Encoding.UTF8.GetBytes(AccountingReportService.Csv(await reports.ReportAsync(key, p, r.StoreId)))));
        if (missing.Count > 0)
            reportFiles.Add(($"{root}/pendencias.csv", Encoding.UTF8.GetBytes("﻿data;referencia;valor;situacao;pendencia\r\n" + string.Join("\r\n", missing) + "\r\n")));
        foreach (var (path, bytes) in reportFiles) entries.Add(new(path, FiscalArchiveService.Sha256(bytes), "relatorio"));

        var contentHash = FiscalArchiveService.Sha256(Encoding.UTF8.GetBytes(string.Join("\n", entries.OrderBy(e => e.Path, StringComparer.Ordinal).Select(e => e.Path + ":" + e.Sha256))));
        var previous = await db.FiscalExports.Where(e => e.Year == r.Year && e.Month == r.Month && e.StoreId == r.StoreId).OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync(ct);
        if (previous is not null && previous.ContentHash == contentHash && File.Exists(PathOf(previous)))
        {
            audit.Add("fiscal.export_reused", actor.TenantId, nameof(FiscalExport), previous.Id, new { r.Year, r.Month, r.StoreId });
            await db.SaveChangesAsync(ct);
            return (previous, true);
        }

        var export = new FiscalExport
        {
            TenantId = actor.TenantId, Year = r.Year, Month = r.Month, StoreId = r.StoreId, CreatedByUserId = actor.UserId, Trigger = trigger, CreatedAt = clock.GetUtcNow(),
            DocumentCount = docFiles.Count(f => f.Kind == FiscalXmlKind.Authorization), EventCount = docFiles.Count(f => f.Kind == FiscalXmlKind.Cancellation),
            VoidCount = voidFiles.Count, ReportCount = reportFiles.Count, MissingCount = missing.Count, ContentHash = contentHash,
        };
        export.StorageName = export.Id.ToString("N") + ".zip";
        export.FileName = $"contabilidade-{company}-{r.Year}-{r.Month:00}{(r.StoreId is { } st ? "-" + Slug(stores.GetValueOrDefault(st, "loja")) : "")}.zip";
        var by = actor.UserId is { } uid ? await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
        var (_, checks) = await closing.ChecksAsync(r.Year, r.Month);
        export.Summary =
            $"Exportação para a contabilidade — {p.Label} ({p.FirstDay:dd/MM/yyyy} a {p.LastDay:dd/MM/yyyy})\r\n" +
            $"Empresa: {tenant.LegalName} ({tenant.TradeName}) — {(profile is null ? "perfil fiscal não configurado" : $"CNPJ {profile.Cnpj} / {profile.Uf} / {(profile.Environment == 1 ? "produção" : "HOMOLOGAÇÃO (sem valor fiscal)")}")}\r\n" +
            $"Loja: {(r.StoreId is { } s2 ? stores.GetValueOrDefault(s2) : "todas")}\r\n" +
            $"Gerado em {TimeZoneInfo.ConvertTime(export.CreatedAt, p.Tz):dd/MM/yyyy HH:mm} por {(by ?? (trigger == "auto" ? "envio automático" : "sistema"))}\r\n\r\n" +
            $"NFC-e autorizadas/denegadas (XML com protocolo): {export.DocumentCount}\r\n" +
            $"Eventos de cancelamento: {export.EventCount}\r\nInutilizações: {export.VoidCount}\r\nRelatórios: {export.ReportCount}\r\n" +
            $"Pendências (documentos sem XML oficial): {export.MissingCount}\r\n" +
            (missing.Count > 0 ? "  " + string.Join("\r\n  ", missing.Take(200)) + "\r\n" : "") +
            "\r\nOs XMLs são os ORIGINAIS recebidos do provedor fiscal, sem alteração. Confira a integridade com manifesto_sha256.txt (formato sha256sum).\r\n" +
            "NF-e (55), CF-e-SAT e ECF: não emitidos por este sistema.\r\n\r\n" + MonthClosingService.Summary(p, checks, null);

        var dir = Dir(actor.TenantId);
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, export.Id.ToString("N") + ".tmp");
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                void Add(string path, byte[] bytes)
                {
                    var e = zip.CreateEntry(path, CompressionLevel.Optimal);
                    using var s = e.Open();
                    s.Write(bytes);
                }
                var ids = paths.Keys.ToList();
                foreach (var chunk in ids.Chunk(200))
                    foreach (var f in await db.FiscalXmlFiles.AsNoTracking().Where(x => chunk.Contains(x.Id)).Select(x => new { x.Id, x.Content, x.Sha256 }).ToListAsync(ct))
                    {
                        if (FiscalArchiveService.Sha256(f.Content) != f.Sha256) throw new DomainException("xml_integrity", "XML guardado com hash diferente do registrado. Exportação interrompida; avise o suporte.", 409);
                        Add(paths[f.Id], f.Content);
                    }
                foreach (var (path, bytes) in reportFiles) Add(path, bytes);
                Add($"{root}/resumo_exportacao.txt", Encoding.UTF8.GetBytes(export.Summary));
                Add($"{root}/manifesto_sha256.txt", Encoding.UTF8.GetBytes(string.Join("\n", entries.Select(e => $"{e.Sha256}  {e.Path[(root.Length + 1)..]}")) + "\n"));
            }
            await using (var read = File.OpenRead(tmp)) export.Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(read, ct)).ToLowerInvariant();
            export.SizeBytes = new FileInfo(tmp).Length;
            File.Move(tmp, PathOf(export));
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        export.ManifestJson = JsonSerializer.Serialize(entries.Select(e => new { path = e.Path, sha256 = e.Sha256, source = e.Source }));
        db.FiscalExports.Add(export);
        audit.Add("fiscal.export_created", actor.TenantId, nameof(FiscalExport), export.Id,
            new { r.Year, r.Month, r.StoreId, trigger, export.DocumentCount, export.EventCount, export.VoidCount, export.MissingCount, export.Sha256 });
        await db.SaveChangesAsync(ct);
        return (export, false);
    }

    public static object View(FiscalExport e, IReadOnlyDictionary<Guid, string>? users = null) => new
    {
        e.Id, e.Year, e.Month, e.StoreId, e.Trigger, e.CreatedAt, e.FileName, e.SizeBytes, e.Sha256, e.DocumentCount, e.EventCount, e.VoidCount, e.ReportCount, e.MissingCount,
        createdBy = e.CreatedByUserId is { } u ? users?.GetValueOrDefault(u) ?? "?" : "automático", e.Summary,
    };

    public async Task<object> ListAsync(int? year, int? month)
    {
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var list = await db.FiscalExports.AsNoTracking().Where(e => (year == null || e.Year == year) && (month == null || e.Month == month))
            .OrderByDescending(e => e.CreatedAt).Take(100).ToListAsync();
        return list.Select(e => View(e, users));
    }

    /// <summary>Arquivo do pacote, conferido pelo SHA-256 antes de entregar.</summary>
    public async Task<(FiscalExport Export, string Path)> FileAsync(Guid id, bool audited = true)
    {
        var e = await db.FiscalExports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) ?? throw new DomainException("not_found", "Pacote não encontrado.", 404);
        var path = PathOf(e);
        if (!File.Exists(path)) throw new DomainException("export_file_missing", "O arquivo do pacote não está mais no servidor. Gere a exportação de novo.", 410);
        await using (var s = File.OpenRead(path))
            if (Convert.ToHexString(await SHA256.HashDataAsync(s)).ToLowerInvariant() != e.Sha256)
                throw new DomainException("export_integrity", "O arquivo do pacote não confere com o hash registrado. Gere a exportação de novo.", 409);
        if (audited)
        {
            audit.Add("fiscal.export_downloaded", actor.TenantId, nameof(FiscalExport), e.Id, new { e.Year, e.Month });
            await db.SaveChangesAsync();
        }
        return (e, path);
    }
}

/// <summary>Escritório de contabilidade: configuração, envio manual/automático, reenvio e histórico.</summary>
public sealed class AccountantService(AppDbContext db, FiscalExportService exports, MonthClosingService closing, AccountingReportService reports, EmailService email,
    RequestActor actor, AuditService audit, TimeProvider clock, IConfiguration config)
{
    private long MaxAttachment => config.GetValue("Accountant:MaxAttachmentBytes", 15L * 1024 * 1024);

    private static List<string> Emails(string? list) => (list ?? "").Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(e => e.ToLowerInvariant()).Distinct().ToList();

    private static bool ValidEmail(string e) => e.Length <= 254 && MailAddress.TryCreate(e, out var a) && a.Address == e && e.Contains('.');

    public static object View(AccountantSettings? s) => s is null ? new { configured = false } : new
    {
        configured = s.Email.Length > 0, s.OfficeName, s.ContactName, s.Email, extraEmails = s.ExtraEmails, s.AutoSend, s.DayOfMonth, s.Hour, s.IncludeXml, s.IncludeReports,
    };

    public async Task<object> GetAsync() => View(await db.AccountantSettings.AsNoTracking().FirstOrDefaultAsync());

    public async Task<object> SaveAsync(AccountantSettingsRequest r)
    {
        var mail = (r.Email ?? "").Trim().ToLowerInvariant();
        if (!ValidEmail(mail)) throw new DomainException("invalid_email", "E-mail do contador inválido.");
        var extra = Emails(r.ExtraEmails).Where(e => e != mail).ToList();
        if (extra.Count > 5) throw new DomainException("too_many_emails", "No máximo 5 e-mails adicionais.");
        if (extra.FirstOrDefault(e => !ValidEmail(e)) is { } bad) throw new DomainException("invalid_email", "E-mail adicional inválido: " + bad);
        if (r.DayOfMonth is < 1 or > 28) throw new DomainException("invalid_day", "Dia do envio: de 1 a 28.");
        if (r.Hour is < 0 or > 23) throw new DomainException("invalid_hour", "Horário do envio: de 0 a 23.");
        var office = (r.OfficeName ?? "").Trim();
        if (office.Length is < 2 or > 120) throw new DomainException("invalid_office", "Nome do escritório: de 2 a 120 caracteres.");
        var s = await db.AccountantSettings.FirstOrDefaultAsync();
        if (s is null) { s = new AccountantSettings { TenantId = actor.TenantId }; db.AccountantSettings.Add(s); }
        s.OfficeName = office; var contact = (r.ContactName ?? "").Trim(); s.ContactName = contact.Length > 120 ? contact[..120] : contact; s.Email = mail; s.ExtraEmails = string.Join(", ", extra);
        s.AutoSend = r.AutoSend; s.DayOfMonth = r.DayOfMonth; s.Hour = r.Hour; s.IncludeXml = r.IncludeXml; s.IncludeReports = r.IncludeReports; s.UpdatedAt = clock.GetUtcNow();
        audit.Add("fiscal.accountant_updated", actor.TenantId, nameof(AccountantSettings), actor.TenantId, new { s.OfficeName, s.Email, extra = extra.Count, s.AutoSend, s.DayOfMonth, s.Hour });
        await db.SaveChangesAsync();
        return View(s);
    }

    /// <summary>Envia um pacote JÁ GERADO (manual, automático ou reenvio): não gera documentos de novo.</summary>
    public async Task<AccountantDelivery> SendAsync(Guid exportId, string trigger)
    {
        var s = await db.AccountantSettings.AsNoTracking().FirstOrDefaultAsync();
        if (s is null || s.Email.Length == 0) throw new DomainException("accountant_not_configured", "Cadastre o e-mail do contador antes de enviar.", 409);
        var (export, path) = await exports.FileAsync(exportId, audited: false);
        await RefreshAsync(await db.AccountantDeliveries.Where(d => d.FiscalExportId == exportId && d.Status == DeliveryStatus.Queued).ToListAsync());
        if (await db.AccountantDeliveries.AnyAsync(d => d.FiscalExportId == exportId && d.Status == DeliveryStatus.Queued))
            throw new DomainException("delivery_in_progress", "Este pacote já está na fila de envio. Aguarde o resultado antes de reenviar.", 409);
        if (export.SizeBytes > MaxAttachment)
            throw new DomainException("export_too_big", $"O pacote tem {export.SizeBytes / 1024 / 1024} MB e passa do limite de anexo do e-mail. Baixe e envie por outro meio (ex.: nuvem).", 409);
        if (trigger != "auto" && await db.AccountantDeliveries.AnyAsync(d => d.FiscalExportId == exportId && d.Status != DeliveryStatus.Blocked)) trigger = "reenvio";

        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == actor.TenantId);
        var recipients = new[] { s.Email }.Concat(Emails(s.ExtraEmails)).Distinct().ToList();
        var label = $"{AccountingReportService.MonthName(export.Month)}/{export.Year}";
        var html = EmailService.Layout($"Documentos fiscais — {label}",
            $"<p>Olá{(s.ContactName.Length > 0 ? ", " + WebUtility.HtmlEncode(s.ContactName) : "")}.</p>" +
            $"<p>Segue em anexo o pacote de <b>{WebUtility.HtmlEncode(tenant.TradeName)}</b> referente a <b>{label}</b>: XMLs originais das NFC-e (com protocolo), " +
            "eventos de cancelamento, inutilizações e relatórios (caixa, pagamentos, vendas × notas, cancelamentos, devoluções, produtos e financeiro).</p>" +
            $"<p>NFC-e: {export.DocumentCount} • Eventos: {export.EventCount} • Inutilizações: {export.VoidCount} • Pendências: {export.MissingCount}</p>" +
            (export.MissingCount > 0 ? "<p><b>Atenção:</b> há documentos pendentes listados em <i>pendencias.csv</i> e no resumo.</p>" : "") +
            $"<p style=\"font-size:12px;color:#6b7280\">SHA-256 do arquivo: {export.Sha256}</p>");
        var delivery = new AccountantDelivery
        {
            FiscalExportId = export.Id, Year = export.Year, Month = export.Month, Trigger = trigger, Recipients = string.Join(", ", recipients), CreatedByUserId = actor.UserId, CreatedAt = clock.GetUtcNow(),
        };
        var ids = new List<Guid>();
        foreach (var to in recipients)
        {
            var m = new EmailMessage { TenantId = actor.TenantId, To = to, Subject = $"Documentos fiscais {label} — {tenant.TradeName}", HtmlBody = html, Kind = "accountant_package",
                AttachmentPath = path, AttachmentName = export.FileName };
            db.EmailMessages.Add(m);
            ids.Add(m.Id);
        }
        delivery.EmailMessageIds = string.Join(",", ids);
        db.AccountantDeliveries.Add(delivery);
        audit.Add("fiscal.accountant_sent", actor.TenantId, nameof(AccountantDelivery), delivery.Id, new { export.Id, trigger, recipients = recipients.Count, export.Year, export.Month });
        await db.SaveChangesAsync();
        return delivery;
    }

    /// <summary>Atualiza o estado com o que se sabe da fila de e-mail (aceito pelo servidor ≠ lido pelo contador).</summary>
    private async Task RefreshAsync(List<AccountantDelivery> list)
    {
        var changed = false;
        foreach (var d in list.Where(d => d.Status == DeliveryStatus.Queued && d.EmailMessageIds.Length > 0))
        {
            var ids = d.EmailMessageIds.Split(',').Select(Guid.Parse).ToList();
            var msgs = await db.EmailMessages.IgnoreQueryFilters().AsNoTracking().Where(m => ids.Contains(m.Id) && m.TenantId == d.TenantId).ToListAsync();
            if (msgs.Count == 0) continue;
            if (msgs.Any(m => m.Status == EmailStatus.Failed))
            {
                d.Status = DeliveryStatus.Failed; d.CompletedAt = clock.GetUtcNow();
                d.Detail = string.Join(" | ", msgs.Where(m => m.Status == EmailStatus.Failed).Select(m => $"{m.To}: {m.LastError}"));
                if (d.Detail.Length > 1000) d.Detail = d.Detail[..1000];
                changed = true;
            }
            else if (msgs.All(m => m.Status == EmailStatus.Sent))
            {
                d.Status = DeliveryStatus.Accepted; d.CompletedAt = msgs.Max(m => m.SentAt); d.Detail = "Aceito pelo servidor de e-mail (a leitura pelo contador não é confirmável).";
                changed = true;
            }
            else if (msgs.FirstOrDefault(m => m.LastError is not null) is { } retry) { d.Detail = $"Nova tentativa programada: {retry.LastError}"; changed = true; }
        }
        if (changed) await db.SaveChangesAsync();
    }

    public async Task<object> HistoryAsync()
    {
        var list = await db.AccountantDeliveries.OrderByDescending(d => d.CreatedAt).Take(100).ToListAsync();
        await RefreshAsync(list);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var files = await db.FiscalExports.AsNoTracking().Where(e => list.Select(d => d.FiscalExportId).Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.FileName);
        return list.Select(d => new
        {
            d.Id, d.Year, d.Month, d.Trigger, d.Status, d.Recipients, d.Detail, d.CreatedAt, d.CompletedAt, d.FiscalExportId,
            file = d.FiscalExportId is { } f ? files.GetValueOrDefault(f) : null, by = d.CreatedByUserId is { } u ? users.GetValueOrDefault(u) : "automático",
        });
    }

    /// <summary>
    /// Envio automático do mês anterior, no dia/horário configurado. Só envia se as verificações essenciais (XMLs, eventos, notas pendentes)
    /// estiverem concluídas; senão registra "bloqueado" e avisa o dono (nova tentativa no dia seguinte).
    /// </summary>
    public async Task<string> AutoSendAsync(CancellationToken ct = default)
    {
        var s = await db.AccountantSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (s is not { AutoSend: true } || s.Email.Length == 0) return "desligado";
        if (!await reports.FiscalEnabledAsync()) return "sem_modulo";
        var tz = await StoreClock.ZoneAsync(db, actor);
        var now = clock.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, tz);
        if (local.Day < s.DayOfMonth || local.Day == s.DayOfMonth && local.Hour < s.Hour) return "fora_do_horario";
        var prev = new DateOnly(local.Year, local.Month, 1).AddMonths(-1);
        var last = await db.AccountantDeliveries.Where(d => d.Year == prev.Year && d.Month == prev.Month && d.Trigger == "auto").OrderByDescending(d => d.CreatedAt).FirstOrDefaultAsync(ct);
        if (last is not null && last.Status != DeliveryStatus.Blocked && last.Status != DeliveryStatus.Failed) return "ja_enviado";
        if (last is not null && last.CreatedAt > now.AddHours(-20)) return "aguardando_nova_tentativa";

        var (_, checks) = await closing.ChecksAsync(prev.Year, prev.Month);
        var blocking = checks.Where(c => c.Key is "xml" or "eventos" or "vendas_documento" && c.State is CheckState.Divergent or CheckState.Pending).ToList();
        if (blocking.Count > 0)
        {
            var detail = string.Join(" | ", blocking.Select(c => c.Title + ": " + c.Detail));
            var b = new AccountantDelivery { Year = prev.Year, Month = prev.Month, Trigger = "auto", Status = DeliveryStatus.Blocked, Recipients = s.Email, CreatedAt = now,
                Detail = detail.Length > 1000 ? detail[..1000] : detail };
            db.AccountantDeliveries.Add(b);
            db.Notifications.Add(new Notification { Type = "accountant_blocked", Title = "Envio ao contador suspenso",
                Body = $"O pacote de {AccountingReportService.MonthName(prev.Month)}/{prev.Year} não foi enviado: há pendências fiscais. Veja Fiscal e Contabilidade → Fechamento." });
            audit.Add("fiscal.accountant_auto_blocked", actor.TenantId, nameof(AccountantDelivery), b.Id, new { prev.Year, prev.Month, blocking = blocking.Select(c => c.Key) });
            await db.SaveChangesAsync(ct);
            return "bloqueado";
        }
        var (export, _) = await exports.CreateAsync(new CreateExportRequest(prev.Year, prev.Month, null, s.IncludeReports), "auto", ct);
        await SendAsync(export.Id, "auto");
        return "enviado";
    }
}

/// <summary>Verifica de hora em hora as empresas com envio automático ao contador ligado.</summary>
public sealed class AccountingJob(IServiceProvider sp, IConfiguration config, ILogger<AccountingJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("Accountant:AutoRun", true)) return;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(config.GetValue("Accountant:IntervalMinutes", 60)), ct); } catch (OperationCanceledException) { return; }
            try { await RunOnceAsync(sp, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Falha no envio automático ao contador"); }
        }
    }

    public static async Task<Dictionary<Guid, string>> RunOnceAsync(IServiceProvider root, CancellationToken ct)
    {
        List<Guid> tenants;
        using (var scope = root.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
            tenants = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AccountantSettings.IgnoreQueryFilters().Where(s => s.AutoSend).Select(s => s.TenantId).ToListAsync(ct);
        }
        var result = new Dictionary<Guid, string>();
        foreach (var t in tenants)
        {
            using var scope = root.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(t);
            var actor = scope.ServiceProvider.GetRequiredService<RequestActor>();
            actor.TenantId = t; actor.Type = ActorType.System;
            try { result[t] = await scope.ServiceProvider.GetRequiredService<AccountantService>().AutoSendAsync(ct); }
            catch (Exception ex) when (ex is DomainException or IOException)
            {
                result[t] = "erro: " + ex.Message;
                scope.ServiceProvider.GetRequiredService<ILogger<AccountingJob>>().LogWarning("Envio automático ao contador ({Tenant}): {Error}", t, ex.Message);
            }
        }
        return result;
    }
}

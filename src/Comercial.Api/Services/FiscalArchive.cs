using System.Security.Cryptography;
using System.Xml;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record FiscalDocumentFilter(DateTimeOffset? From, DateTimeOffset? To, string? Status, int? Number, int? Series, string? AccessKey,
    int? Model, Guid? StoreId, int Page = 1, int PageSize = 50);

/// <summary>
/// Arquivo fiscal: baixa do provedor os XMLs ORIGINAIS (nota autorizada com protocolo, cancelamento, inutilização), confere
/// se são mesmo o documento esperado e guarda sem alterar. Nada é reconstruído: sem XML oficial, fica a pendência.
/// </summary>
public sealed class FiscalArchiveService(AppDbContext db, FiscalService fiscal, IEnumerable<IFiscalProvider> providers, RequestActor actor,
    AuditService audit, ScopedLookup lookup, TimeProvider clock, ILogger<FiscalArchiveService> log)
{
    private const string Ns = "http://www.portalfiscal.inf.br/nfe";
    public static readonly int[] AuthorizedCStat = [100, 150];
    public static readonly int[] DeniedCStat = [110, 301, 302, 303];

    // ---------------- Validação do XML original ----------------

    /// <summary>Confere se o XML é o documento oficial esperado. Retorna o motivo da recusa, ou null se estiver correto.</summary>
    public static string? Validate(string kind, byte[] bytes, string? expectedKey, bool denied = false)
    {
        XmlDocument x;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var reader = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 });
            x = new XmlDocument { XmlResolver = null };
            x.Load(reader);
        }
        catch (XmlException) { return "arquivo não é um XML válido"; }
        var m = new XmlNamespaceManager(x.NameTable);
        m.AddNamespace("n", Ns);
        string? V(string xpath) => x.SelectSingleNode(xpath, m)?.InnerText.Trim();
        static bool In(string? v, int[] set) => int.TryParse(v, out var c) && set.Contains(c);
        switch (kind)
        {
            case FiscalXmlKind.Authorization:
                if (x.DocumentElement?.LocalName != "nfeProc") return "não é uma nota com protocolo (nfeProc)";
                var key = V("//n:protNFe/n:infProt/n:chNFe");
                if (string.IsNullOrEmpty(key) || expectedKey is not null && key != expectedKey) return "chave de acesso diferente da nota";
                if (string.IsNullOrEmpty(V("//n:protNFe/n:infProt/n:nProt"))) return "sem protocolo da SEFAZ";
                if (!In(V("//n:protNFe/n:infProt/n:cStat"), denied ? DeniedCStat : AuthorizedCStat)) return "situação (cStat) não confere com a nota";
                return null;
            case FiscalXmlKind.Cancellation:
                if (x.DocumentElement?.LocalName != "procEventoNFe") return "não é um evento com protocolo (procEventoNFe)";
                if (V("//n:evento/n:infEvento/n:tpEvento") != "110111") return "evento não é de cancelamento";
                var ck = V("//n:retEvento/n:infEvento/n:chNFe") ?? V("//n:evento/n:infEvento/n:chNFe");
                if (string.IsNullOrEmpty(ck) || expectedKey is not null && ck != expectedKey) return "chave de acesso diferente da nota";
                if (!In(V("//n:retEvento/n:infEvento/n:cStat"), [135, 155])) return "cancelamento não homologado (cStat)";
                return null;
            case FiscalXmlKind.NumberVoid:
                if (x.DocumentElement?.LocalName is not ("procInutNFe" or "ProcInutNFe")) return "não é uma inutilização com protocolo (procInutNFe)";
                if (V("//n:retInutNFe/n:infInut/n:cStat") != "102") return "inutilização não homologada (cStat)";
                return null;
            default: return "tipo desconhecido";
        }
    }

    public static string Sha256(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    // ---------------- Busca no provedor ----------------

    /// <summary>
    /// Baixa os XMLs que ainda não estão no arquivo. tenantId null = todas as empresas (serviço automático, escopo de plataforma).
    /// Falhas viram pendência com nova tentativa (espera crescente); nada é dado como arquivado sem o XML conferido.
    /// </summary>
    public async Task<int> FetchPendingAsync(Guid? tenantId, CancellationToken ct, bool ignoreWait = false)
    {
        var now = clock.GetUtcNow();
        var files = db.FiscalXmlFiles.IgnoreQueryFilters();
        var docs = await db.FiscalDocuments.IgnoreQueryFilters()
            .Where(d => (tenantId == null || d.TenantId == tenantId) && (ignoreWait || d.ArchiveNextAt == null || d.ArchiveNextAt <= now) && d.ArchiveAttempts < 40
                && ((d.XmlUrl != null && (d.Status == FiscalStatus.Authorized || d.Status == FiscalStatus.Denied || d.Status == FiscalStatus.Cancelled && d.AuthorizedAt != null)
                        && !files.Any(f => f.FiscalDocumentId == d.Id && f.Kind == FiscalXmlKind.Authorization))
                    || (d.Status == FiscalStatus.Cancelled && d.CancelXmlUrl != null && !files.Any(f => f.FiscalDocumentId == d.Id && f.Kind == FiscalXmlKind.Cancellation))))
            .OrderBy(d => d.CreatedAt).Take(50).ToListAsync(ct);
        var voids = await db.FiscalNumberVoids.IgnoreQueryFilters()
            .Where(v => (tenantId == null || v.TenantId == tenantId) && (ignoreWait || v.ArchiveNextAt == null || v.ArchiveNextAt <= now) && v.ArchiveAttempts < 40
                && v.Status == "autorizada" && v.XmlUrl != null && !files.Any(f => f.NumberVoidId == v.Id))
            .OrderBy(v => v.CreatedAt).Take(20).ToListAsync(ct);
        if (docs.Count == 0 && voids.Count == 0) return 0;

        var saleIds = docs.Select(d => d.SaleId).ToList();
        var saleDates = await db.Sales.IgnoreQueryFilters().Where(s => saleIds.Contains(s.Id)).Select(s => new { s.Id, s.ReceivedAt }).ToDictionaryAsync(s => s.Id, s => s.ReceivedAt, ct);
        var contexts = new Dictionary<Guid, (FiscalContext? Ctx, IFiscalProvider? P)>();
        async Task<(FiscalContext? Ctx, IFiscalProvider? P)> Ctx(Guid t) => contexts.TryGetValue(t, out var c) ? c : contexts[t] = await fiscal.ProviderContextAsync(t);

        var stored = 0;
        foreach (var d in docs)
        {
            var (ctx, provider) = await Ctx(d.TenantId);
            if (ctx is null || provider is null) continue;
            var ok = true;
            if (d.XmlUrl is not null && !await files.AnyAsync(f => f.FiscalDocumentId == d.Id && f.Kind == FiscalXmlKind.Authorization, ct))
                ok &= await TryStoreAsync(provider, ctx, d.TenantId, d.XmlUrl, FiscalXmlKind.Authorization, d.AccessKey, d.Status == FiscalStatus.Denied,
                    saleDates.GetValueOrDefault(d.SaleId, d.CreatedAt), f => { f.FiscalDocumentId = d.Id; f.AccessKey = d.AccessKey; f.Model = (int)d.Model; }, d.Id, ct);
            if (d.Status == FiscalStatus.Cancelled && d.CancelXmlUrl is not null)
                ok &= await TryStoreAsync(provider, ctx, d.TenantId, d.CancelXmlUrl, FiscalXmlKind.Cancellation, d.AccessKey, false,
                    d.CancelledAt ?? now, f => { f.FiscalDocumentId = d.Id; f.AccessKey = d.AccessKey; f.Model = (int)d.Model; }, d.Id, ct);
            if (ok) { stored++; d.ArchiveNextAt = null; }
            else { d.ArchiveAttempts++; d.ArchiveNextAt = now.AddMinutes(Math.Min(24 * 60, 5 * Math.Pow(2, Math.Min(d.ArchiveAttempts, 10)))); }
            await db.SaveChangesAsync(ct);
        }
        foreach (var v in voids)
        {
            var (ctx, provider) = await Ctx(v.TenantId);
            if (ctx is null || provider is null) continue;
            if (await TryStoreAsync(provider, ctx, v.TenantId, v.XmlUrl!, FiscalXmlKind.NumberVoid, null, false, v.CreatedAt, f => f.NumberVoidId = v.Id, null, ct)) stored++;
            else { v.ArchiveAttempts++; v.ArchiveNextAt = now.AddMinutes(Math.Min(24 * 60, 5 * Math.Pow(2, Math.Min(v.ArchiveAttempts, 10)))); }
            await db.SaveChangesAsync(ct);
        }
        return stored;
    }

    private async Task<bool> TryStoreAsync(IFiscalProvider provider, FiscalContext ctx, Guid tenantId, string url, string kind, string? key, bool denied,
        DateTimeOffset referenceAt, Action<FiscalXmlFile> link, Guid? docId, CancellationToken ct)
    {
        string problem;
        try
        {
            var bytes = await provider.DownloadXmlAsync(ctx, url, ct);
            if (bytes is null) problem = "XML não encontrado no provedor fiscal";
            else if (Validate(kind, bytes, key, denied) is { } why) problem = "XML recusado: " + why;
            else
            {
                var file = new FiscalXmlFile { TenantId = tenantId, Kind = kind, Content = bytes, Sha256 = Sha256(bytes), SizeBytes = bytes.Length, ReferenceAt = referenceAt, Source = "provedor:" + provider.Name };
                link(file);
                db.FiscalXmlFiles.Add(file);
                audit.Add("fiscal.xml_archived", tenantId, nameof(FiscalXmlFile), file.Id, new { kind, key, file.Sha256, file.SizeBytes });
                return true;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or DomainException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            problem = ex is DomainException de ? de.Message : "falha ao baixar do provedor fiscal";
        }
        log.LogWarning("Arquivo fiscal ({Kind}) pendente para {Key}: {Problem}", kind, key, problem);
        if (docId is { } id) db.FiscalEvents.Add(new FiscalEvent { TenantId = tenantId, FiscalDocumentId = id, Type = "xml_" + kind, Message = problem });
        return false;
    }

    // ---------------- Consulta ----------------

    /// <summary>Situação exibida: "cancelada antes de transmitir" não é um cancelamento fiscal (não tem evento na SEFAZ).</summary>
    public static string StatusKey(FiscalStatus s, DateTimeOffset? authorizedAt) => s switch
    {
        FiscalStatus.PendingTransmission => "pendente",
        FiscalStatus.Authorized => "autorizada",
        FiscalStatus.Rejected => "rejeitada",
        FiscalStatus.Contingency => "contingencia",
        FiscalStatus.Cancelled when authorizedAt is null => "descartada",
        FiscalStatus.Cancelled => "cancelada",
        FiscalStatus.Denied => "denegada",
        _ => s.ToString(),
    };

    public async Task<object> ListAsync(FiscalDocumentFilter f)
    {
        var page = Math.Max(1, f.Page);
        var size = Math.Clamp(f.PageSize, 10, 200);
        var q = from d in db.FiscalDocuments.AsNoTracking()
                join s in db.Sales.AsNoTracking() on d.SaleId equals s.Id
                select new { d, s.ReceivedAt, s.StoreId, s.TotalCents, s.CustomerId, SaleStatus = s.Status };
        if (f.From is { } from) q = q.Where(x => x.ReceivedAt >= from);
        if (f.To is { } to) q = q.Where(x => x.ReceivedAt < to);
        if (f.StoreId is { } st) q = q.Where(x => x.StoreId == st);
        if (f.Number is { } n) q = q.Where(x => x.d.Number == n);
        if (f.Series is { } se) q = q.Where(x => x.d.Series == se);
        if (f.Model is { } mo) q = q.Where(x => (int)x.d.Model == mo);
        if (!string.IsNullOrWhiteSpace(f.AccessKey))
        {
            var key = new string(f.AccessKey.Where(char.IsDigit).ToArray());
            if (key.Length is < 4 or > 44) throw new DomainException("invalid_key", "Informe ao menos 4 dígitos da chave de acesso.");
            q = q.Where(x => x.d.AccessKey != null && x.d.AccessKey.Contains(key));
        }
        q = f.Status switch
        {
            null or "" or "todas" => q,
            "pendente" => q.Where(x => x.d.Status == FiscalStatus.PendingTransmission),
            "autorizada" => q.Where(x => x.d.Status == FiscalStatus.Authorized),
            "rejeitada" => q.Where(x => x.d.Status == FiscalStatus.Rejected),
            "contingencia" => q.Where(x => x.d.Status == FiscalStatus.Contingency),
            "cancelada" => q.Where(x => x.d.Status == FiscalStatus.Cancelled && x.d.AuthorizedAt != null),
            "descartada" => q.Where(x => x.d.Status == FiscalStatus.Cancelled && x.d.AuthorizedAt == null),
            "denegada" => q.Where(x => x.d.Status == FiscalStatus.Denied),
            _ => throw new DomainException("invalid_status", "Situação inválida."),
        };
        var total = await q.CountAsync();
        var rows = await q.OrderByDescending(x => x.ReceivedAt).Skip((page - 1) * size).Take(size).ToListAsync();
        var ids = rows.Select(r => r.d.Id).ToList();
        var archived = await db.FiscalXmlFiles.AsNoTracking().Where(x => x.FiscalDocumentId != null && ids.Contains(x.FiscalDocumentId.Value))
            .Select(x => new { x.FiscalDocumentId, x.Kind }).ToListAsync();
        var custIds = rows.Where(r => r.CustomerId != null).Select(r => r.CustomerId!.Value).Distinct().ToList();
        var customers = await db.Customers.AsNoTracking().Where(c => custIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
        return new
        {
            total, page, pageSize = size,
            items = rows.Select(r => new
            {
                r.d.Id, r.d.SaleId, model = (int)r.d.Model, r.d.Number, r.d.Series, issuedAt = r.ReceivedAt, r.StoreId, totalCents = r.TotalCents,
                status = StatusKey(r.d.Status, r.d.AuthorizedAt), statusCode = (int)r.d.Status, r.d.AccessKey, r.d.Protocol, r.d.LastMessage, r.d.DanfeUrl,
                environment = r.d.Environment,
                // NFC-e deste sistema não identifica o consumidor na nota; o cliente da venda (CRM) aparece só como referência.
                recipient = r.CustomerId is { } cid && customers.TryGetValue(cid, out var name) ? name + " (cliente da venda)" : "Consumidor não identificado",
                hasXml = archived.Any(a => a.FiscalDocumentId == r.d.Id && a.Kind == FiscalXmlKind.Authorization),
                hasCancelXml = archived.Any(a => a.FiscalDocumentId == r.d.Id && a.Kind == FiscalXmlKind.Cancellation),
                saleCancelled = r.SaleStatus == SaleStatus.Cancelled,
            }),
        };
    }

    public async Task<object> DetailAsync(Guid id)
    {
        var d = await lookup.GetAsync<FiscalDocument>(id);
        var sale = await db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Payments).FirstAsync(s => s.Id == d.SaleId);
        var events = await db.FiscalEvents.AsNoTracking().Where(e => e.FiscalDocumentId == d.Id).OrderBy(e => e.CreatedAt).Select(e => new { e.Type, e.Message, e.CreatedAt }).ToListAsync();
        var files = await db.FiscalXmlFiles.AsNoTracking().Where(x => x.FiscalDocumentId == d.Id).Select(x => new { x.Id, x.Kind, x.Sha256, x.SizeBytes, x.StoredAt, x.Source }).ToListAsync();
        return new
        {
            d.Id, model = (int)d.Model, d.Number, d.Series, status = StatusKey(d.Status, d.AuthorizedAt), d.AccessKey, d.Protocol, d.LastMessage, d.AuthorizedAt,
            d.CancelledAt, d.CancelReason, d.Environment, d.DanfeUrl, d.ProviderName, d.Attempts,
            sale = new
            {
                sale.Id, sale.ReceivedAt, sale.StoreId, sale.SubtotalCents, sale.DiscountCents, sale.TotalCents, status = sale.Status.ToString(), sale.ReturnedCents,
                items = sale.Items.Select(i => new { i.ProductName, i.Quantity, i.UnitPriceCents, i.DiscountCents, i.TotalCents }),
                payments = sale.Payments.Select(p => new { method = p.Method.ToString(), p.AmountCents }),
            },
            events, files,
        };
    }

    public async Task<(byte[] Bytes, string Name)> XmlAsync(Guid documentId, string kind)
    {
        var d = await lookup.GetAsync<FiscalDocument>(documentId);
        var f = await db.FiscalXmlFiles.AsNoTracking().FirstOrDefaultAsync(x => x.FiscalDocumentId == d.Id && x.Kind == kind)
            ?? throw new DomainException("xml_not_archived", "O XML original ainda não está no arquivo fiscal (veja as pendências).", 404);
        if (Sha256(f.Content) != f.Sha256) throw new DomainException("xml_integrity", "Falha de integridade no XML guardado. Avise o suporte.", 409);
        audit.Add("fiscal.xml_downloaded", actor.TenantId, nameof(FiscalXmlFile), f.Id, new { kind, d.AccessKey });
        await db.SaveChangesAsync();
        return (f.Content, $"{d.AccessKey ?? d.Id.ToString("N")}-{(kind == FiscalXmlKind.Authorization ? "procNFe" : "procEventoNFe-cancelamento")}.xml");
    }

    public async Task<object> VoidsAsync() => await db.FiscalNumberVoids.AsNoTracking().OrderByDescending(v => v.CreatedAt).Take(200)
        .Select(v => new { v.Id, v.Series, v.FirstNumber, v.LastNumber, v.Status, v.Protocol, v.Message, v.CreatedAt, hasXml = db.FiscalXmlFiles.Any(f => f.NumberVoidId == v.Id) })
        .ToListAsync();
}

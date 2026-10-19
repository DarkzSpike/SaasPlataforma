using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record FiscalProviderResult(FiscalStatus Status, string Message, string? Protocol = null, string? AccessKey = null,
    int? Number = null, int? Series = null, string? DanfeUrl = null, string? XmlUrl = null, string? QrCodeUrl = null, bool Retry = false);

/// <summary>Credenciais e ambiente do provedor para uma operação (token já descriptografado, só em memória).</summary>
public sealed record FiscalContext(TenantFiscalProfile Profile, string Token);

/// <summary>
/// Abstração do provedor fiscal (Focus NFe, ou outro intermediário homologado). O provedor guarda o certificado A1
/// do contribuinte, assina, transmite à SEFAZ da UF e trata contingência.
/// </summary>
public interface IFiscalProvider
{
    string Name { get; }
    Task<FiscalProviderResult> TransmitAsync(FiscalContext ctx, FiscalDocument doc, JsonObject payload, CancellationToken ct);
    Task<FiscalProviderResult> CancelAsync(FiscalContext ctx, FiscalDocument doc, string reason, CancellationToken ct);
    Task<FiscalProviderResult> VoidNumbersAsync(FiscalContext ctx, int series, int first, int last, string reason, CancellationToken ct);
}

/// <summary>
/// Focus NFe (https://focusnfe.com.br) — API REST v2. Ambiente de homologação para testes, sem valor fiscal.
/// STATUS: escrito conforme a documentação pública, NÃO testado contra a conta real (exige CNPJ e token da loja).
/// Os campos de IBS/CBS (reforma tributária) devem ser conferidos na homologação antes de produção.
/// </summary>
public sealed class FocusNfeProvider(HttpClient http, ILogger<FocusNfeProvider> log) : IFiscalProvider
{
    public string Name => "focus";
    private static string Base(FiscalContext c) => c.Profile.Environment == 1 ? "https://api.focusnfe.com.br" : "https://homologacao.focusnfe.com.br";

    private async Task<(int Status, JsonNode Body)> SendAsync(FiscalContext c, HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, Base(c) + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(c.Token + ":")));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) log.LogWarning("Focus NFe {Path} -> {Status}: {Body}", path, (int)res.StatusCode, text.Length > 400 ? text[..400] : text);
        JsonNode node;
        try { node = JsonNode.Parse(text) ?? new JsonObject(); } catch (System.Text.Json.JsonException) { node = new JsonObject { ["mensagem"] = text }; }
        return ((int)res.StatusCode, node);
    }

    private static string? S(JsonNode n, string k) => n[k]?.ToString();
    private string Url(FiscalContext c, string? path) => path is null ? "" : path.StartsWith("http") ? path : Base(c) + path;

    public async Task<FiscalProviderResult> TransmitAsync(FiscalContext c, FiscalDocument doc, JsonObject payload, CancellationToken ct)
    {
        try
        {
            var (code, n) = await SendAsync(c, HttpMethod.Post, "/v2/nfce?ref=" + Uri.EscapeDataString(doc.ProviderRef!), payload, ct);
            var status = S(n, "status") ?? "";
            var msg = S(n, "mensagem_sefaz") ?? S(n, "mensagem") ?? status;
            return status switch
            {
                "autorizado" => new(S(n, "contingencia_offline") == "true" ? FiscalStatus.Contingency : FiscalStatus.Authorized, msg, S(n, "protocolo") ?? S(n, "numero_protocolo"),
                    S(n, "chave_nfe")?.Replace("NFe", ""), int.TryParse(S(n, "numero"), out var num) ? num : null, int.TryParse(S(n, "serie"), out var ser) ? ser : null,
                    Url(c, S(n, "caminho_danfe")), Url(c, S(n, "caminho_xml_nota_fiscal")), S(n, "qrcode_url")),
                "processando_autorizacao" => new(FiscalStatus.PendingTransmission, "SEFAZ processando; nova consulta em instantes.", Retry: true),
                "erro_autorizacao" or "denegado" => new(FiscalStatus.Rejected, msg),
                _ when code >= 500 || code == 429 => new(FiscalStatus.PendingTransmission, "Provedor indisponível; nova tentativa automática.", Retry: true),
                _ => new(FiscalStatus.Rejected, msg),
            };
        }
        catch (HttpRequestException) { return new(FiscalStatus.PendingTransmission, "Sem conexão com o provedor fiscal; nova tentativa automática.", Retry: true); }
    }

    public async Task<FiscalProviderResult> CancelAsync(FiscalContext c, FiscalDocument doc, string reason, CancellationToken ct)
    {
        var (_, n) = await SendAsync(c, HttpMethod.Delete, "/v2/nfce/" + Uri.EscapeDataString(doc.ProviderRef!), new JsonObject { ["justificativa"] = reason }, ct);
        return S(n, "status") == "cancelado"
            ? new(FiscalStatus.Cancelled, S(n, "mensagem_sefaz") ?? "Cancelada.", S(n, "protocolo"))
            : new(doc.Status, S(n, "mensagem_sefaz") ?? S(n, "mensagem") ?? "Cancelamento recusado.");
    }

    public async Task<FiscalProviderResult> VoidNumbersAsync(FiscalContext c, int series, int first, int last, string reason, CancellationToken ct)
    {
        var (_, n) = await SendAsync(c, HttpMethod.Post, "/v2/nfce/inutilizacao", new JsonObject
        {
            ["cnpj"] = c.Profile.Cnpj, ["serie"] = series.ToString(), ["numero_inicial"] = first.ToString(), ["numero_final"] = last.ToString(), ["justificativa"] = reason,
        }, ct);
        return S(n, "status") == "autorizado"
            ? new(FiscalStatus.Authorized, S(n, "mensagem_sefaz") ?? "Numeração inutilizada.", S(n, "protocolo_sefaz") ?? S(n, "protocolo"))
            : new(FiscalStatus.Rejected, S(n, "mensagem_sefaz") ?? S(n, "mensagem") ?? "Inutilização recusada.");
    }
}

public sealed record FiscalProfileRequest(string Cnpj, string? StateRegistration, int Regime, string Uf, int Environment, string Provider, string? ProviderToken,
    int NfceSeries, bool AutoEmit, string? DefaultCfop, string? DefaultIcmsCst, string? DefaultPisCst, string? DefaultCofinsCst,
    string? DefaultIbsCbsCst, string? DefaultIbsCbsClass, decimal? IbsRate, decimal? CbsRate);
public sealed record CancelFiscalRequest(string Reason);
public sealed record VoidNumbersRequest(int Series, int FirstNumber, int LastNumber, string Reason);

public sealed class FiscalService(AppDbContext db, IEnumerable<IFiscalProvider> providers, RequestActor actor, AuditService audit, ScopedLookup lookup,
    IDataProtectionProvider dp, TimeProvider clock)
{
    private IDataProtector Protector => dp.CreateProtector("Comercial.Fiscal.ProviderToken.v1");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string Money(long cents) => (cents / 100m).ToString("0.00", Inv);
    public static readonly TimeSpan NfceCancelWindow = TimeSpan.FromMinutes(30);

    // ---------------- Perfil fiscal ----------------

    public static object View(TenantFiscalProfile? p) => p is null ? new { configured = false } : new
    {
        configured = p.Provider != "none" && p.ProviderTokenProtected is not null, p.Cnpj, p.StateRegistration, regime = (int)p.Regime, p.Uf, p.Environment, p.Provider,
        hasToken = p.ProviderTokenProtected is not null, p.NfceSeries, p.AutoEmit, p.DefaultCfop, p.DefaultIcmsCst, p.DefaultPisCst, p.DefaultCofinsCst,
        p.DefaultIbsCbsCst, p.DefaultIbsCbsClass, p.IbsRate, p.CbsRate,
    };

    public async Task<object> GetProfileAsync() => View(await db.FiscalProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == actor.TenantId));

    public async Task<object> SaveProfileAsync(FiscalProfileRequest r)
    {
        if (!FiscalRules.IsCnpj(r.Cnpj)) throw new DomainException("invalid_cnpj", "CNPJ inválido.");
        if (!FiscalRules.Ufs.Contains((r.Uf ?? "").ToUpperInvariant())) throw new DomainException("invalid_uf", "UF inválida.");
        if (!Enum.IsDefined(typeof(TaxRegime), r.Regime)) throw new DomainException("invalid_regime", "Regime tributário inválido.");
        if (r.Environment is not (1 or 2)) throw new DomainException("invalid_environment", "Ambiente deve ser 1 (produção) ou 2 (homologação).");
        if (r.Provider is not ("none" or "focus")) throw new DomainException("invalid_provider", "Provedor fiscal inválido.");
        if (r.NfceSeries is < 1 or > 999) throw new DomainException("invalid_series", "Série inválida.");
        if (FiscalRules.ValidateProduct(null, null, r.DefaultCfop, 0, r.DefaultIcmsCst, r.DefaultIbsCbsCst, r.DefaultIbsCbsClass) is { } fe) throw new DomainException("invalid_fiscal", fe);
        if (r.IbsRate is < 0 or > 100 || r.CbsRate is < 0 or > 100) throw new DomainException("invalid_rate", "Alíquota inválida.");
        var p = await db.FiscalProfiles.FirstOrDefaultAsync(x => x.TenantId == actor.TenantId);
        if (p is null) { p = new TenantFiscalProfile { TenantId = actor.TenantId }; db.FiscalProfiles.Add(p); }
        p.Cnpj = FiscalRules.Digits(r.Cnpj); p.StateRegistration = string.IsNullOrWhiteSpace(r.StateRegistration) ? null : FiscalRules.Digits(r.StateRegistration);
        p.Regime = (TaxRegime)r.Regime; p.Uf = (r.Uf ?? "").ToUpperInvariant(); p.Environment = r.Environment; p.Provider = r.Provider;
        if (!string.IsNullOrWhiteSpace(r.ProviderToken)) p.ProviderTokenProtected = Protector.Protect(r.ProviderToken.Trim());
        p.NfceSeries = r.NfceSeries; p.AutoEmit = r.AutoEmit;
        p.DefaultCfop = r.DefaultCfop ?? p.DefaultCfop; p.DefaultIcmsCst = r.DefaultIcmsCst ?? p.DefaultIcmsCst;
        p.DefaultPisCst = r.DefaultPisCst ?? p.DefaultPisCst; p.DefaultCofinsCst = r.DefaultCofinsCst ?? p.DefaultCofinsCst;
        p.DefaultIbsCbsCst = r.DefaultIbsCbsCst ?? p.DefaultIbsCbsCst; p.DefaultIbsCbsClass = r.DefaultIbsCbsClass ?? p.DefaultIbsCbsClass;
        p.IbsRate = r.IbsRate ?? p.IbsRate; p.CbsRate = r.CbsRate ?? p.CbsRate;
        p.UpdatedAt = clock.GetUtcNow();
        audit.Add("fiscal.profile_updated", actor.TenantId, nameof(TenantFiscalProfile), actor.TenantId,
            new { p.Cnpj, p.Uf, regime = p.Regime.ToString(), p.Environment, p.Provider, tokenChanged = !string.IsNullOrWhiteSpace(r.ProviderToken) });
        await db.SaveChangesAsync();
        return View(p);
    }

    private async Task<(FiscalContext? Ctx, IFiscalProvider? Provider)> ContextAsync(Guid tenantId)
    {
        var p = await db.FiscalProfiles.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId);
        if (p is null || p.Provider == "none" || p.ProviderTokenProtected is null) return (null, null);
        var provider = providers.FirstOrDefault(x => x.Name == p.Provider);
        return provider is null ? (null, null) : (new FiscalContext(p, Protector.Unprotect(p.ProviderTokenProtected)), provider);
    }

    // ---------------- Montagem da NFC-e ----------------

    public async Task<JsonObject> BuildNfceAsync(FiscalDocument doc, TenantFiscalProfile p)
    {
        var sale = await db.Sales.IgnoreQueryFilters().AsNoTracking().Include(s => s.Items).Include(s => s.Payments).FirstAsync(s => s.Id == doc.SaleId && s.TenantId == doc.TenantId);
        var ids = sale.Items.Select(i => i.ProductId).ToList();
        var products = await db.Products.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == doc.TenantId && ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var missing = sale.Items.Where(i => string.IsNullOrEmpty(products[i.ProductId].Ncm)).Select(i => i.ProductName).ToList();
        if (missing.Count > 0) throw new DomainException("fiscal_missing_ncm", "Produto(s) sem NCM: " + string.Join(", ", missing.Take(5)));

        // Desconto da venda (total) rateado nos itens proporcionalmente.
        var saleLevel = sale.DiscountCents - sale.Items.Sum(i => i.DiscountCents);
        var gross = sale.Items.Sum(i => i.TotalCents);
        long allocated = 0;
        var items = new JsonArray();
        var n = 0;
        foreach (var i in sale.Items)
        {
            n++;
            var pr = products[i.ProductId];
            var share = n == sale.Items.Count ? saleLevel - allocated : gross == 0 ? 0 : saleLevel * i.TotalCents / gross;
            allocated += share;
            var bruto = (long)Math.Round(i.UnitPriceCents * i.Quantity);
            var desconto = i.DiscountCents + share;
            var baseIbs = bruto - desconto;
            var item = new JsonObject
            {
                ["numero_item"] = n.ToString(), ["codigo_produto"] = pr.Sku, ["descricao"] = i.ProductName,
                ["codigo_ncm"] = pr.Ncm, ["cfop"] = pr.Cfop ?? p.DefaultCfop, ["unidade_comercial"] = pr.Unit, ["unidade_tributavel"] = pr.Unit,
                ["quantidade_comercial"] = i.Quantity.ToString("0.0000", Inv), ["quantidade_tributavel"] = i.Quantity.ToString("0.0000", Inv),
                ["valor_unitario_comercial"] = Money(i.UnitPriceCents), ["valor_unitario_tributavel"] = Money(i.UnitPriceCents),
                ["valor_bruto"] = Money(bruto), ["valor_desconto"] = Money(desconto),
                ["icms_origem"] = pr.FiscalOrigin.ToString(), ["icms_situacao_tributaria"] = pr.IcmsCst ?? p.DefaultIcmsCst,
                ["pis_situacao_tributaria"] = p.DefaultPisCst, ["cofins_situacao_tributaria"] = p.DefaultCofinsCst,
                // Reforma tributária (IBS/CBS) — nomes de campo a conferir na homologação do provedor.
                ["ibs_cbs_situacao_tributaria"] = pr.IbsCbsCst ?? p.DefaultIbsCbsCst, ["ibs_cbs_classificacao_tributaria"] = pr.IbsCbsClass ?? p.DefaultIbsCbsClass,
                ["ibs_cbs_base_calculo"] = Money(baseIbs), ["ibs_uf_aliquota"] = p.IbsRate.ToString("0.0000", Inv), ["cbs_aliquota"] = p.CbsRate.ToString("0.0000", Inv),
                ["ibs_uf_valor"] = Money((long)Math.Round(baseIbs * p.IbsRate / 100m)), ["cbs_valor"] = Money((long)Math.Round(baseIbs * p.CbsRate / 100m)),
            };
            if (!string.IsNullOrEmpty(pr.Cest)) item["cest"] = pr.Cest;
            items.Add(item);
        }
        var pays = new JsonArray();
        foreach (var pay in sale.Payments) pays.Add(new JsonObject { ["forma_pagamento"] = FiscalRules.PaymentCode(pay.Method), ["valor_pagamento"] = Money(pay.AmountCents) });
        return new JsonObject
        {
            ["cnpj_emitente"] = p.Cnpj, ["data_emissao"] = sale.ReceivedAt.ToOffset(TimeSpan.FromHours(-3)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", Inv),
            ["natureza_operacao"] = "VENDA AO CONSUMIDOR", ["presenca_comprador"] = "1", ["modalidade_frete"] = "9", ["local_destino"] = "1",
            ["indicador_inscricao_estadual_destinatario"] = "9", ["serie"] = p.NfceSeries.ToString(),
            ["items"] = items, ["formas_pagamento"] = pays, ["valor_troco"] = Money(sale.ChangeCents),
        };
    }

    // ---------------- Transmissão / cancelamento / inutilização ----------------

    public async Task<FiscalDocument> TransmitAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await lookup.GetAsync<FiscalDocument>(documentId);
        return await TransmitDocAsync(doc, ct);
    }

    /// <summary>Transmite um documento (também usado pelo serviço automático, em escopo de plataforma).</summary>
    public async Task<FiscalDocument> TransmitDocAsync(FiscalDocument doc, CancellationToken ct)
    {
        if (doc.Status is FiscalStatus.Authorized or FiscalStatus.Cancelled)
            throw new DomainException("fiscal_final_state", "Documento já está em estado final.", 409);
        var (ctx, provider) = await ContextAsync(doc.TenantId);
        doc.Attempts++;
        if (ctx is null || provider is null)
        {
            doc.ProviderName = "nao_configurado";
            doc.LastMessage = "Provedor fiscal não configurado para esta empresa. Documento permanece pendente (não autorizado).";
            doc.NextAttemptAt = clock.GetUtcNow().AddHours(6);
            await db.SaveChangesAsync(ct);
            return doc;
        }
        doc.ProviderRef ??= $"nfce-{doc.Id:N}"[..30];
        doc.Environment = ctx.Profile.Environment;
        FiscalProviderResult result;
        try { result = await provider.TransmitAsync(ctx, doc, await BuildNfceAsync(doc, ctx.Profile), ct); }
        catch (DomainException ex) { result = new(FiscalStatus.Rejected, ex.Message); }
        if (result.Status is FiscalStatus.Authorized or FiscalStatus.Contingency && (string.IsNullOrWhiteSpace(result.AccessKey) || result.Status == FiscalStatus.Authorized && string.IsNullOrWhiteSpace(result.Protocol)))
            result = new(FiscalStatus.PendingTransmission, "Resposta do provedor sem chave/protocolo; nova consulta automática.", Retry: true);
        doc.ProviderName = provider.Name;
        doc.LastMessage = result.Message;
        doc.Status = result.Status;
        if (result.Status is FiscalStatus.Authorized or FiscalStatus.Contingency)
        {
            doc.Protocol = result.Protocol; doc.AccessKey = result.AccessKey; doc.Number = result.Number; doc.Series = result.Series;
            doc.DanfeUrl = result.DanfeUrl; doc.XmlUrl = result.XmlUrl; doc.QrCodeUrl = result.QrCodeUrl;
            if (result.Status == FiscalStatus.Authorized) doc.AuthorizedAt = clock.GetUtcNow();
        }
        // Contingência: emitida offline, precisa ser enviada à SEFAZ depois.
        doc.NextAttemptAt = result.Retry || result.Status == FiscalStatus.Contingency ? clock.GetUtcNow().AddMinutes(Math.Min(60, 2 * doc.Attempts)) : null;
        db.FiscalEvents.Add(new FiscalEvent { TenantId = doc.TenantId, FiscalDocumentId = doc.Id, Type = "transmit", Message = result.Message });
        audit.Add("fiscal.transmit", doc.TenantId, nameof(FiscalDocument), doc.Id, new { status = doc.Status.ToString(), provider = provider.Name, doc.Number });
        await db.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<FiscalDocument> CancelAsync(Guid documentId, CancelFiscalRequest r, CancellationToken ct)
    {
        var reason = (r.Reason ?? "").Trim();
        if (reason.Length is < 15 or > 255) throw new DomainException("reason_required", "Justificativa do cancelamento: de 15 a 255 caracteres (exigência da SEFAZ).");
        var doc = await lookup.GetAsync<FiscalDocument>(documentId);
        if (doc.Status != FiscalStatus.Authorized) throw new DomainException("fiscal_not_authorized", "Só é possível cancelar NFC-e autorizada.", 409);
        if (clock.GetUtcNow() - doc.AuthorizedAt > NfceCancelWindow)
            throw new DomainException("fiscal_cancel_window", "Prazo de cancelamento da NFC-e (30 minutos) encerrado. Fale com o contador (pode exigir nota de entrada/devolução).", 409);
        var (ctx, provider) = await ContextAsync(doc.TenantId);
        if (ctx is null || provider is null) throw new DomainException("fiscal_not_configured", "Provedor fiscal não configurado.", 409);
        var result = await provider.CancelAsync(ctx, doc, reason, ct);
        doc.LastMessage = result.Message;
        if (result.Status == FiscalStatus.Cancelled) { doc.Status = FiscalStatus.Cancelled; doc.CancelledAt = clock.GetUtcNow(); doc.CancelReason = reason; }
        db.FiscalEvents.Add(new FiscalEvent { FiscalDocumentId = doc.Id, Type = "cancel", Message = result.Message });
        audit.Add("fiscal.cancel", actor.TenantId, nameof(FiscalDocument), doc.Id, new { ok = result.Status == FiscalStatus.Cancelled, reason });
        await db.SaveChangesAsync(ct);
        if (result.Status != FiscalStatus.Cancelled) throw new DomainException("fiscal_cancel_refused", "Cancelamento recusado: " + result.Message, 409);
        return doc;
    }

    public async Task<FiscalNumberVoid> VoidNumbersAsync(VoidNumbersRequest r, CancellationToken ct)
    {
        var reason = (r.Reason ?? "").Trim();
        if (reason.Length is < 15 or > 255) throw new DomainException("reason_required", "Justificativa: de 15 a 255 caracteres.");
        if (r.FirstNumber < 1 || r.LastNumber < r.FirstNumber || r.LastNumber - r.FirstNumber > 10_000) throw new DomainException("invalid_range", "Faixa de numeração inválida.");
        var (ctx, provider) = await ContextAsync(actor.TenantId);
        if (ctx is null || provider is null) throw new DomainException("fiscal_not_configured", "Provedor fiscal não configurado.", 409);
        var result = await provider.VoidNumbersAsync(ctx, r.Series, r.FirstNumber, r.LastNumber, reason, ct);
        var v = new FiscalNumberVoid { Series = r.Series, FirstNumber = r.FirstNumber, LastNumber = r.LastNumber, Reason = reason,
            Status = result.Status == FiscalStatus.Authorized ? "autorizada" : "recusada", Protocol = result.Protocol, Message = result.Message };
        db.FiscalNumberVoids.Add(v);
        audit.Add("fiscal.void_numbers", actor.TenantId, nameof(FiscalNumberVoid), v.Id, new { r.Series, r.FirstNumber, r.LastNumber, v.Status });
        await db.SaveChangesAsync(ct);
        return v;
    }

    public Task<List<FiscalDocument>> ListAsync(FiscalStatus? status) =>
        db.FiscalDocuments.AsNoTracking().Where(d => status == null || d.Status == status).OrderByDescending(d => d.CreatedAt).Take(500).ToListAsync();
}

/// <summary>Emite automaticamente as NFC-e pendentes e reenvia as em contingência (quando a loja ativou "emitir automaticamente").</summary>
public sealed class FiscalJob(IServiceProvider sp, IConfiguration config, ILogger<FiscalJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("Fiscal:AutoRun", true)) return;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
            try
            {
                using var scope = sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
                await RunOnceAsync(scope.ServiceProvider, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Falha na emissão automática de NFC-e"); }
        }
    }

    public static async Task<int> RunOnceAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var svc = sp.GetRequiredService<FiscalService>();
        var now = DateTimeOffset.UtcNow;
        var auto = await db.FiscalProfiles.IgnoreQueryFilters().Where(p => p.AutoEmit && p.Provider != "none").Select(p => p.TenantId).ToListAsync(ct);
        var docs = await db.FiscalDocuments.IgnoreQueryFilters()
            .Where(d => auto.Contains(d.TenantId) && (d.Status == FiscalStatus.PendingTransmission || d.Status == FiscalStatus.Contingency)
                        && d.Attempts < 20 && (d.NextAttemptAt == null || d.NextAttemptAt <= now))
            .OrderBy(d => d.CreatedAt).Take(30).ToListAsync(ct);
        foreach (var d in docs) await svc.TransmitDocAsync(d, ct);
        return docs.Count;
    }
}

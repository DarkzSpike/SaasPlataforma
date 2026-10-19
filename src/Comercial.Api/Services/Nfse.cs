using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record NfseConfig(string Cnpj, string MunicipalRegistration, string CityCode, string ServiceItem, decimal IssRate, string Token, int Environment);
public sealed record NfseSettingsRequest(bool Enabled, string? Cnpj, string? MunicipalRegistration, string? CityCode, string? ServiceItem, decimal? IssRate, string? Token, int? Environment);
public sealed record NfseResult(string Status, string Message, string? Number = null, string? Verification = null, string? PdfUrl = null);

public interface INfseProvider
{
    Task<NfseResult> IssueAsync(NfseConfig cfg, NfseDocument doc, JsonObject payload, CancellationToken ct);
    Task<NfseResult> QueryAsync(NfseConfig cfg, NfseDocument doc, CancellationToken ct);
}

/// <summary>Focus NFe — NFS-e (API v2). STATUS: escrito conforme a documentação; NÃO testado (exige CNPJ e inscrição municipal).</summary>
public sealed class FocusNfseProvider(HttpClient http) : INfseProvider
{
    private static string Base(NfseConfig c) => c.Environment == 1 ? "https://api.focusnfe.com.br" : "https://homologacao.focusnfe.com.br";

    private async Task<JsonNode> SendAsync(NfseConfig c, HttpMethod m, string path, JsonObject? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(m, Base(c) + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(c.Token + ":")));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        try { return JsonNode.Parse(text) ?? new JsonObject(); } catch (System.Text.Json.JsonException) { return new JsonObject { ["mensagem"] = text }; }
    }

    private static NfseResult Map(JsonNode n) => (n["status"]?.ToString() ?? "") switch
    {
        "autorizado" => new("issued", "NFS-e autorizada.", n["numero"]?.ToString(), n["codigo_verificacao"]?.ToString(), n["url"]?.ToString() ?? n["caminho_xml_nota_fiscal"]?.ToString()),
        "processando_autorizacao" => new("processing", "Prefeitura processando."),
        _ => new("error", n["mensagem"]?.ToString() ?? n["erros"]?.ToJsonString() ?? "Erro na emissão."),
    };

    public async Task<NfseResult> IssueAsync(NfseConfig c, NfseDocument d, JsonObject payload, CancellationToken ct) =>
        Map(await SendAsync(c, HttpMethod.Post, $"/v2/nfse?ref=nfse-{d.Id:N}", payload, ct));

    public async Task<NfseResult> QueryAsync(NfseConfig c, NfseDocument d, CancellationToken ct) =>
        Map(await SendAsync(c, HttpMethod.Get, $"/v2/nfse/nfse-{d.Id:N}", null, ct));
}

/// <summary>
/// NFS-e das mensalidades: quando a fatura é paga, entra na fila; o serviço automático emite pela prefeitura via provedor.
/// Desligada por padrão (exige CNPJ e inscrição municipal de quem vende o sistema).
/// </summary>
public sealed class NfseService(AppDbContext db, PlatformSettingsService settings, INfseProvider provider, AuditService audit, TimeProvider clock)
{
    public async Task QueueForInvoiceAsync(Invoice inv)
    {
        if (await settings.NfseAsync() is null) return;
        if (await db.NfseDocuments.AnyAsync(x => x.InvoiceId == inv.Id)) return;
        db.NfseDocuments.Add(new NfseDocument { InvoiceId = inv.Id, TenantId = inv.TenantId, AmountCents = inv.AmountCents });
        await db.SaveChangesAsync();
    }

    public async Task<int> ProcessAsync(CancellationToken ct)
    {
        var cfg = await settings.NfseAsync();
        if (cfg is null) return 0;
        var pending = await db.NfseDocuments.Where(x => (x.Status == "pending" || x.Status == "processing") && x.Attempts < 30).OrderBy(x => x.CreatedAt).Take(20).ToListAsync(ct);
        foreach (var d in pending)
        {
            d.Attempts++;
            NfseResult r;
            try
            {
                if (d.Status == "processing") r = await provider.QueryAsync(cfg, d, ct);
                else
                {
                    var t = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == d.TenantId, ct);
                    var doc = FiscalRules.Digits(t.Document);
                    var tomador = new JsonObject { ["razao_social"] = t.LegalName };
                    if (doc.Length == 14) tomador["cnpj"] = doc; else if (doc.Length == 11) tomador["cpf"] = doc;
                    r = await provider.IssueAsync(cfg, d, new JsonObject
                    {
                        ["data_emissao"] = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(-3)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                        ["prestador"] = new JsonObject { ["cnpj"] = cfg.Cnpj, ["inscricao_municipal"] = cfg.MunicipalRegistration, ["codigo_municipio"] = cfg.CityCode },
                        ["tomador"] = tomador,
                        ["servico"] = new JsonObject
                        {
                            ["valor_servicos"] = (d.AmountCents / 100m).ToString("0.00", CultureInfo.InvariantCulture), ["item_lista_servico"] = cfg.ServiceItem,
                            ["aliquota"] = cfg.IssRate.ToString("0.00", CultureInfo.InvariantCulture), ["iss_retido"] = false,
                            ["discriminacao"] = $"Licença de uso de software de gestão comercial (SaaS) - mensalidade. Empresa: {t.TradeName}.",
                        },
                    }, ct);
                }
            }
            catch (HttpRequestException) { r = new("pending", "Sem conexão com o provedor; nova tentativa."); }
            d.Status = r.Status == "error" ? "error" : r.Status;
            d.LastMessage = r.Message;
            if (r.Status == "issued") { d.Number = r.Number; d.VerificationCode = r.Verification; d.PdfUrl = r.PdfUrl; d.IssuedAt = clock.GetUtcNow(); }
            audit.Add("nfse." + d.Status, d.TenantId, nameof(NfseDocument), d.Id, new { d.Number, r.Message });
            await db.SaveChangesAsync(ct);
        }
        return pending.Count;
    }
}

public sealed class NfseJob(IServiceProvider sp, IConfiguration config, ILogger<NfseJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("Fiscal:AutoRun", true)) return;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
            try
            {
                using var scope = sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
                await scope.ServiceProvider.GetRequiredService<NfseService>().ProcessAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Falha na emissão de NFS-e"); }
        }
    }
}

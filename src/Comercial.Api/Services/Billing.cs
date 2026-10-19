using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

// ============================== Configurações da plataforma ==============================

public sealed record MercadoPagoSettingsRequest(string? AccessToken, string? PublicKey, string? WebhookSecret, string? NotificationBaseUrl,
    bool RemoveAccessToken = false, bool RemoveWebhookSecret = false);

/// <summary>Visão do painel: segredos aparecem só como "configurado (final ...1234)".</summary>
public sealed record MercadoPagoSettingsView(bool Configured, string? AccessTokenHint, bool IsTestToken, string? PublicKey, bool HasWebhookSecret,
    string? NotificationBaseUrl, string WebhookUrl, DateTimeOffset? UpdatedAt);

public sealed record MercadoPagoConfig(string AccessToken, string? WebhookSecret, string? NotificationBaseUrl);

public sealed record SmtpSettingsRequest(string? Host, int? Port, bool? Ssl, string? User, string? Password, string? From, string? FromName, bool RemovePassword = false);
public sealed record SmtpSettingsView(bool Configured, string? Host, int Port, bool Ssl, string? User, bool HasPassword, string? From, string? FromName);

public sealed class PlatformSettingsService(AppDbContext db, IDataProtectionProvider dp, AuditService audit, RequestActor actor)
{
    public const string MpAccessToken = "mp.access_token";
    public const string MpPublicKey = "mp.public_key";
    public const string MpWebhookSecret = "mp.webhook_secret";
    public const string MpNotificationBaseUrl = "mp.notification_base_url";
    public const string SmtpHost = "smtp.host", SmtpPort = "smtp.port", SmtpSsl = "smtp.ssl", SmtpUser = "smtp.user", SmtpPassword = "smtp.password",
        SmtpFrom = "smtp.from", SmtpFromName = "smtp.from_name";
    private IDataProtector Protector => dp.CreateProtector("Comercial.PlatformSettings.v1");

    public const string NfseEnabled = "nfse.enabled", NfseCnpj = "nfse.cnpj", NfseIm = "nfse.im", NfseCity = "nfse.city", NfseItem = "nfse.item",
        NfseRate = "nfse.rate", NfseToken = "nfse.token", NfseEnv = "nfse.env";

    /// <summary>Configuração de NFS-e (null = desligada ou incompleta).</summary>
    public async Task<NfseConfig?> NfseAsync()
    {
        if (await GetAsync(NfseEnabled) != "true") return null;
        var cnpj = await GetAsync(NfseCnpj); var im = await GetAsync(NfseIm); var city = await GetAsync(NfseCity); var token = await GetAsync(NfseToken);
        if (cnpj is null || im is null || city is null || token is null) return null;
        return new NfseConfig(cnpj, im, city, await GetAsync(NfseItem) ?? "01.07", decimal.TryParse(await GetAsync(NfseRate), CultureInfo.InvariantCulture, out var r) ? r : 2m,
            token, int.TryParse(await GetAsync(NfseEnv), out var e) ? e : 2);
    }

    public async Task<object> NfseViewAsync() => new
    {
        enabled = await GetAsync(NfseEnabled) == "true", ready = await NfseAsync() is not null, cnpj = await GetAsync(NfseCnpj), municipalRegistration = await GetAsync(NfseIm),
        cityCode = await GetAsync(NfseCity), serviceItem = await GetAsync(NfseItem) ?? "01.07", issRate = await GetAsync(NfseRate) ?? "2.00",
        hasToken = await GetAsync(NfseToken) is not null, environment = int.TryParse(await GetAsync(NfseEnv), out var e) ? e : 2,
    };

    public async Task SaveNfseAsync(NfseSettingsRequest r)
    {
        if (r.Cnpj is { Length: > 0 } && !FiscalRules.IsCnpj(r.Cnpj)) throw new DomainException("invalid_cnpj", "CNPJ inválido. NFS-e exige empresa (CNPJ).");
        if (r.CityCode is { Length: > 0 } && FiscalRules.Digits(r.CityCode).Length != 7) throw new DomainException("invalid_city", "Código IBGE do município deve ter 7 dígitos.");
        if (r.IssRate is < 0 or > 5) throw new DomainException("invalid_rate", "Alíquota do ISS deve ficar entre 0% e 5%.");
        if (r.Environment is not (null or 1 or 2)) throw new DomainException("invalid_environment", "Ambiente inválido.");
        if (r.Cnpj is not null) await SetAsync(NfseCnpj, r.Cnpj.Length == 0 ? null : FiscalRules.Digits(r.Cnpj), false);
        if (r.MunicipalRegistration is not null) await SetAsync(NfseIm, r.MunicipalRegistration.Trim().Length == 0 ? null : r.MunicipalRegistration.Trim(), false);
        if (r.CityCode is not null) await SetAsync(NfseCity, r.CityCode.Length == 0 ? null : FiscalRules.Digits(r.CityCode), false);
        if (r.ServiceItem is not null) await SetAsync(NfseItem, r.ServiceItem.Trim(), false);
        if (r.IssRate is { } rate) await SetAsync(NfseRate, rate.ToString(CultureInfo.InvariantCulture), false);
        if (!string.IsNullOrWhiteSpace(r.Token)) await SetAsync(NfseToken, r.Token.Trim(), true);
        if (r.Environment is { } env) await SetAsync(NfseEnv, env.ToString(), false);
        await SetAsync(NfseEnabled, r.Enabled ? "true" : "false", false);
        await db.SaveChangesAsync();
        if (r.Enabled && await NfseAsync() is null)
            throw new DomainException("nfse_incomplete", "Para ligar a NFS-e preencha CNPJ, inscrição municipal, código do município e o token do provedor.");
        audit.Add("platform.nfse_updated", null, nameof(PlatformSetting), "nfse", new { r.Enabled, r.Cnpj, r.CityCode, r.ServiceItem, r.IssRate, tokenChanged = !string.IsNullOrWhiteSpace(r.Token) });
        await db.SaveChangesAsync();
    }

    public const string SignupEnabled = "signup.enabled", SignupPlan = "signup.plan", SignupTrialDays = "signup.trial_days";
    public sealed record SignupConfig(bool Enabled, string PlanCode, int TrialDays);

    public async Task<SignupConfig> SignupAsync() => new(
        await GetAsync(SignupEnabled) != "false", await GetAsync(SignupPlan) ?? "profissional", int.TryParse(await GetAsync(SignupTrialDays), out var d) ? d : 14);

    public async Task SaveSignupAsync(SignupSettingsRequest r)
    {
        if (r.TrialDays is < 0 or > 90) throw new DomainException("invalid_trial", "Teste grátis de 0 a 90 dias.");
        if (!await db.Plans.AnyAsync(p => p.Code == r.PlanCode && p.IsActive)) throw new DomainException("plan_not_found", "Plano não encontrado.", 404);
        await SetAsync(SignupEnabled, r.Enabled ? "true" : "false", false);
        await SetAsync(SignupPlan, r.PlanCode, false);
        await SetAsync(SignupTrialDays, r.TrialDays.ToString(), false);
        audit.Add("platform.signup_updated", null, nameof(PlatformSetting), "signup", r);
        await db.SaveChangesAsync();
    }

    // ---------- Versão do caixa (atualização automática) ----------
    public const string DesktopVersion = "desktop.version", DesktopUrl = "desktop.url", DesktopSha = "desktop.sha256", DesktopNotes = "desktop.notes", DesktopMandatory = "desktop.mandatory";
    public sealed record DesktopRelease(string Version, string Url, string Sha256, string? Notes, bool Mandatory);

    public async Task<DesktopRelease?> DesktopReleaseAsync()
    {
        var v = await GetAsync(DesktopVersion);
        var url = await GetAsync(DesktopUrl);
        var sha = await GetAsync(DesktopSha);
        return v is null || url is null || sha is null ? null : new(v, url, sha, await GetAsync(DesktopNotes), await GetAsync(DesktopMandatory) == "true");
    }

    public async Task SaveDesktopReleaseAsync(DesktopReleaseRequest r, bool allowHttpLocal)
    {
        if (!Version.TryParse(r.Version?.Trim(), out var ver) || ver.Build < 0) throw new DomainException("invalid_version", "Versão no formato 1.2.3.");
        var url = (r.Url ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !(u.Scheme == Uri.UriSchemeHttps || (allowHttpLocal && u.IsLoopback)))
            throw new DomainException("invalid_url", "O instalador precisa estar num endereço HTTPS (ex.: GitHub Releases).");
        var sha = (r.Sha256 ?? "").Trim().ToLowerInvariant();
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) throw new DomainException("invalid_sha256", "SHA-256 com 64 caracteres (o script de publicação mostra).");
        await SetAsync(DesktopVersion, r.Version!.Trim(), false);
        await SetAsync(DesktopUrl, url, false);
        await SetAsync(DesktopSha, sha, false);
        await SetAsync(DesktopNotes, string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim()[..Math.Min(r.Notes.Trim().Length, 1000)], false);
        await SetAsync(DesktopMandatory, r.Mandatory ? "true" : "false", false);
        audit.Add("platform.desktop_release", null, nameof(PlatformSetting), "desktop", new { r.Version, url, sha, r.Mandatory });
        await db.SaveChangesAsync();
    }

    public async Task<SmtpConfig?> SmtpAsync()
    {
        var host = await GetAsync(SmtpHost);
        var from = await GetAsync(SmtpFrom);
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from)) return null;
        return new SmtpConfig(host, int.TryParse(await GetAsync(SmtpPort), out var p) ? p : 587, (await GetAsync(SmtpSsl)) != "false",
            await GetAsync(SmtpUser), await GetAsync(SmtpPassword), from, await GetAsync(SmtpFromName) ?? "Sistema Comercial");
    }

    public async Task<SmtpSettingsView> SmtpViewAsync()
    {
        var host = await GetAsync(SmtpHost);
        return new SmtpSettingsView(await SmtpAsync() is not null, host, int.TryParse(await GetAsync(SmtpPort), out var p) ? p : 587,
            (await GetAsync(SmtpSsl)) != "false", await GetAsync(SmtpUser), await GetAsync(SmtpPassword) is not null, await GetAsync(SmtpFrom), await GetAsync(SmtpFromName));
    }

    public async Task SaveSmtpAsync(SmtpSettingsRequest r)
    {
        if (r.Host is not null)
        {
            var h = r.Host.Trim();
            if (h.Length > 0 && (h.Length > 200 || h.Contains(' ') || h.Contains('/'))) throw new DomainException("invalid_host", "Servidor SMTP inválido (ex.: smtp.gmail.com).");
            await SetAsync(SmtpHost, h.Length == 0 ? null : h, secret: false);
        }
        if (r.Port is { } port)
        {
            if (port is < 1 or > 65535) throw new DomainException("invalid_port", "Porta inválida (normalmente 587).");
            await SetAsync(SmtpPort, port.ToString(), secret: false);
        }
        if (r.Ssl is { } ssl) await SetAsync(SmtpSsl, ssl ? "true" : "false", secret: false);
        if (r.User is not null) await SetAsync(SmtpUser, r.User.Trim().Length == 0 ? null : r.User.Trim(), secret: false);
        if (!string.IsNullOrEmpty(r.Password)) await SetAsync(SmtpPassword, r.Password, secret: true);
        else if (r.RemovePassword) await SetAsync(SmtpPassword, null, secret: true);
        if (r.From is not null)
        {
            var f = r.From.Trim();
            if (f.Length > 0 && !System.Net.Mail.MailAddress.TryCreate(f, out _)) throw new DomainException("invalid_email", "E-mail do remetente inválido.");
            await SetAsync(SmtpFrom, f.Length == 0 ? null : f, secret: false);
        }
        if (r.FromName is not null) await SetAsync(SmtpFromName, r.FromName.Trim().Length == 0 ? null : r.FromName.Trim(), secret: false);
        audit.Add("platform.smtp_updated", null, nameof(PlatformSetting), "smtp", new { r.Host, r.Port, r.Ssl, r.User, passwordChanged = !string.IsNullOrEmpty(r.Password) || r.RemovePassword, r.From });
        await db.SaveChangesAsync();
    }

    private Task<PlatformSetting?> Find(string key) => db.PlatformSettings.FirstOrDefaultAsync(x => x.Key == key);

    private async Task<string?> GetAsync(string key)
    {
        var s = await db.PlatformSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key);
        if (s?.Value is null) return null;
        return s.IsSecret ? Protector.Unprotect(s.Value) : s.Value;
    }

    private async Task SetAsync(string key, string? value, bool secret)
    {
        var s = await Find(key);
        if (s is null) { s = new PlatformSetting { Key = key, IsSecret = secret }; db.PlatformSettings.Add(s); }
        s.Value = value is null ? null : secret ? Protector.Protect(value) : value;
        s.IsSecret = secret;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        s.UpdatedByAdminId = actor.AdminId;
    }

    public async Task<MercadoPagoConfig?> MercadoPagoAsync()
    {
        var token = await GetAsync(MpAccessToken);
        return string.IsNullOrWhiteSpace(token) ? null : new MercadoPagoConfig(token, await GetAsync(MpWebhookSecret), await GetAsync(MpNotificationBaseUrl));
    }

    public async Task<MercadoPagoSettingsView> MercadoPagoViewAsync(string apiBaseUrl)
    {
        var token = await GetAsync(MpAccessToken);
        var baseUrl = await GetAsync(MpNotificationBaseUrl);
        var updated = await db.PlatformSettings.AsNoTracking().Where(x => x.Key.StartsWith("mp.")).MaxAsync(x => (DateTimeOffset?)x.UpdatedAt);
        return new MercadoPagoSettingsView(token is not null, token is null ? null : "final ..." + token[^Math.Min(4, token.Length)..],
            token?.StartsWith("TEST-") ?? false, await GetAsync(MpPublicKey), await GetAsync(MpWebhookSecret) is not null, baseUrl,
            (baseUrl ?? apiBaseUrl).TrimEnd('/') + "/webhooks/mercadopago", updated);
    }

    public async Task SaveMercadoPagoAsync(MercadoPagoSettingsRequest r)
    {
        if (!string.IsNullOrWhiteSpace(r.AccessToken))
        {
            var t = r.AccessToken.Trim();
            if (!(t.StartsWith("APP_USR-") || t.StartsWith("TEST-")) || t.Length > 300)
                throw new DomainException("invalid_token", "Access Token do Mercado Pago inválido (começa com APP_USR- ou TEST-).");
            await SetAsync(MpAccessToken, t, secret: true);
        }
        else if (r.RemoveAccessToken) await SetAsync(MpAccessToken, null, secret: true);

        if (!string.IsNullOrWhiteSpace(r.WebhookSecret))
        {
            if (r.WebhookSecret.Trim().Length is < 16 or > 200) throw new DomainException("invalid_secret", "Assinatura secreta do webhook inválida.");
            await SetAsync(MpWebhookSecret, r.WebhookSecret.Trim(), secret: true);
        }
        else if (r.RemoveWebhookSecret) await SetAsync(MpWebhookSecret, null, secret: true);

        if (r.PublicKey is not null)
        {
            var pk = r.PublicKey.Trim();
            if (pk.Length > 0 && !(pk.StartsWith("APP_USR-") || pk.StartsWith("TEST-"))) throw new DomainException("invalid_public_key", "Public Key inválida.");
            await SetAsync(MpPublicKey, pk.Length == 0 ? null : pk, secret: false);
        }
        if (r.NotificationBaseUrl is not null)
        {
            var u = r.NotificationBaseUrl.Trim().TrimEnd('/');
            if (u.Length > 0 && (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme != "https"))
                throw new DomainException("invalid_url", "O endereço público da API precisa ser https:// (o Mercado Pago não envia avisos para localhost).");
            await SetAsync(MpNotificationBaseUrl, u.Length == 0 ? null : u, secret: false);
        }
        audit.Add("platform.mercadopago_updated", null, nameof(PlatformSetting), "mercadopago",
            new { tokenChanged = !string.IsNullOrWhiteSpace(r.AccessToken) || r.RemoveAccessToken, secretChanged = !string.IsNullOrWhiteSpace(r.WebhookSecret) || r.RemoveWebhookSecret, r.NotificationBaseUrl });
        await db.SaveChangesAsync();
    }
}

// ============================== Cliente Mercado Pago ==============================

public sealed record MpPixRequest(long AmountCents, string Description, string ExternalReference, string PayerEmail, string? NotificationUrl, DateTimeOffset ExpiresAt, string IdempotencyKey);
public sealed record MpPixResult(string PaymentId, string QrCode, string QrBase64, string? TicketUrl);
public sealed record MpPreferenceRequest(string Title, long AmountCents, string ExternalReference, string PayerEmail, string? NotificationUrl, DateTimeOffset ExpiresAt);
public sealed record MpPreferenceResult(string Id, string InitPoint);
public sealed record MpPayment(string Id, string Status, string? ExternalReference, long AmountCents, string Currency, string? PaymentType);

public interface IMercadoPagoClient
{
    Task<MpPixResult> CreatePixAsync(string accessToken, MpPixRequest r, CancellationToken ct);
    Task<MpPreferenceResult> CreatePreferenceAsync(string accessToken, MpPreferenceRequest r, CancellationToken ct);
    Task<MpPayment?> GetPaymentAsync(string accessToken, string paymentId, CancellationToken ct);
    Task<IReadOnlyList<MpPayment>> SearchByReferenceAsync(string accessToken, string externalReference, CancellationToken ct);
    Task<(bool Ok, string Message)> CheckAsync(string accessToken, CancellationToken ct);
    Task CancelPaymentAsync(string accessToken, string paymentId, CancellationToken ct);
}

/// <summary>API REST do Mercado Pago (https://api.mercadopago.com). Valores em reais com 2 casas.</summary>
public sealed class MercadoPagoClient(HttpClient http, ILogger<MercadoPagoClient> log) : IMercadoPagoClient
{
    private static string Amount(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
    private static string Iso(DateTimeOffset d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, string token, JsonObject? body, string? idempotencyKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (idempotencyKey is not null) req.Headers.Add("X-Idempotency-Key", idempotencyKey);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Mercado Pago {Method} {Path} -> {Status}: {Body}", method, path, (int)res.StatusCode, text.Length > 400 ? text[..400] : text);
            var msg = (int)res.StatusCode is 401 or 403 ? "Credencial do Mercado Pago recusada." : "O Mercado Pago recusou a operação.";
            throw new DomainException("payment_provider_error", msg, 502);
        }
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    public async Task<MpPixResult> CreatePixAsync(string token, MpPixRequest r, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["transaction_amount"] = decimal.Parse(Amount(r.AmountCents), CultureInfo.InvariantCulture),
            ["description"] = r.Description,
            ["payment_method_id"] = "pix",
            ["external_reference"] = r.ExternalReference,
            ["date_of_expiration"] = Iso(r.ExpiresAt),
            ["payer"] = new JsonObject { ["email"] = r.PayerEmail },
        };
        if (r.NotificationUrl is not null) body["notification_url"] = r.NotificationUrl;
        var n = await SendAsync(HttpMethod.Post, "/v1/payments", token, body, r.IdempotencyKey, ct);
        var tx = n["point_of_interaction"]?["transaction_data"];
        return new MpPixResult(n["id"]!.ToString(), tx?["qr_code"]?.GetValue<string>() ?? "", tx?["qr_code_base64"]?.GetValue<string>() ?? "", tx?["ticket_url"]?.GetValue<string>());
    }

    public async Task<MpPreferenceResult> CreatePreferenceAsync(string token, MpPreferenceRequest r, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject
            {
                ["title"] = r.Title, ["quantity"] = 1, ["currency_id"] = "BRL",
                ["unit_price"] = decimal.Parse(Amount(r.AmountCents), CultureInfo.InvariantCulture),
            }),
            ["external_reference"] = r.ExternalReference,
            ["payer"] = new JsonObject { ["email"] = r.PayerEmail },
            ["expires"] = true,
            ["expiration_date_to"] = Iso(r.ExpiresAt),
        };
        if (r.NotificationUrl is not null) body["notification_url"] = r.NotificationUrl;
        var n = await SendAsync(HttpMethod.Post, "/checkout/preferences", token, body, null, ct);
        return new MpPreferenceResult(n["id"]!.ToString(), n["init_point"]!.GetValue<string>());
    }

    private static MpPayment Parse(JsonNode n) => new(
        n["id"]!.ToString(), n["status"]?.GetValue<string>() ?? "", n["external_reference"]?.GetValue<string>(),
        (long)Math.Round((n["transaction_amount"]?.GetValue<decimal>() ?? 0) * 100), n["currency_id"]?.GetValue<string>() ?? "", n["payment_type_id"]?.GetValue<string>());

    public async Task<MpPayment?> GetPaymentAsync(string token, string paymentId, CancellationToken ct)
    {
        if (!paymentId.All(char.IsAsciiLetterOrDigit) || paymentId.Length > 40) return null;
        return Parse(await SendAsync(HttpMethod.Get, "/v1/payments/" + paymentId, token, null, null, ct));
    }

    public async Task<IReadOnlyList<MpPayment>> SearchByReferenceAsync(string token, string externalReference, CancellationToken ct)
    {
        var n = await SendAsync(HttpMethod.Get, "/v1/payments/search?sort=date_created&criteria=desc&external_reference=" + Uri.EscapeDataString(externalReference), token, null, null, ct);
        return (n["results"] as JsonArray ?? []).OfType<JsonNode>().Select(Parse).ToList();
    }

    public async Task CancelPaymentAsync(string token, string paymentId, CancellationToken ct)
    {
        if (!paymentId.All(char.IsAsciiLetterOrDigit) || paymentId.Length > 40) return;
        await SendAsync(HttpMethod.Put, "/v1/payments/" + paymentId, token, new JsonObject { ["status"] = "cancelled" }, null, ct);
    }

    public async Task<(bool Ok, string Message)> CheckAsync(string token, CancellationToken ct)
    {
        try
        {
            var n = await SendAsync(HttpMethod.Get, "/users/me", token, null, null, ct);
            return (true, $"Conta conectada: {n["nickname"]?.GetValue<string>()} ({n["email"]?.GetValue<string>()})");
        }
        catch (DomainException ex) { return (false, ex.Message); }
        catch (HttpRequestException) { return (false, "Sem conexão com o Mercado Pago."); }
    }
}

// ============================== Faturas ==============================

public sealed record InvoiceView(Guid Id, long AmountCents, DateTimeOffset DueAt, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, string Status,
    DateTimeOffset? PaidAt, string? PaidMethod, bool Overdue, int DaysToDue, string? PixCopyPaste, string? PixQrBase64, DateTimeOffset? PixExpiresAt, string? CheckoutUrl);

public sealed class BillingService(AppDbContext db, AuditService audit, RequestActor actor, PlatformSettingsService settings, IMercadoPagoClient mp,
    NotificationService notifications, TimeProvider clock, IConfiguration config, ILogger<BillingService> log, EmailService email, NfseService nfse)
{
    /// <summary>Avisa por e-mail os proprietários da empresa (além do aviso no app).</summary>
    private async Task EmailOwnersAsync(Guid tenantId, string subject, string title, string text, string kind)
    {
        var owners = await (from u in db.Users.IgnoreQueryFilters() join r in db.UserRoles.IgnoreQueryFilters() on u.Id equals r.UserId
                            where u.TenantId == tenantId && r.TenantId == tenantId && r.Role == Roles.Owner && u.IsActive select u.Email).Distinct().ToListAsync();
        foreach (var to in owners) email.Simple(tenantId, to, subject, title, text, kind);
        if (owners.Count > 0) await db.SaveChangesAsync();
    }

    public const string Provider = "mercadopago";
    /// <summary>Quantos dias antes do fim do período a fatura é gerada.</summary>
    public int LeadDays => config.GetValue("Billing:LeadDays", 10);
    public const int ReminderDays = 3;
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private static string Brl(long c) => (c / 100m).ToString("C", Pt);
    private static string Status(InvoiceStatus s) => s switch { InvoiceStatus.Paid => "paga", InvoiceStatus.Void => "cancelada", _ => "aberta" };

    public InvoiceView View(Invoice i)
    {
        var now = clock.GetUtcNow();
        var pixValid = i.Status == InvoiceStatus.Open && i.PixExpiresAt > now;
        return new InvoiceView(i.Id, i.AmountCents, i.DueAt, i.PeriodStart, i.PeriodEnd, Status(i.Status), i.PaidAt, i.PaidMethod,
            i.Status == InvoiceStatus.Open && i.DueAt < now, (int)Math.Ceiling((i.DueAt - now).TotalDays),
            pixValid ? i.PixCopyPaste : null, pixValid ? i.PixQrBase64 : null, pixValid ? i.PixExpiresAt : null,
            i.Status == InvoiceStatus.Open ? i.CheckoutUrl : null);
    }

    // ---------------- Geração automática ----------------

    /// <summary>Cria a fatura do próximo período de cada assinatura que vence em até LeadDays (idempotente).</summary>
    public async Task<int> GenerateDueInvoicesAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var limit = now.AddDays(LeadDays);
        var subs = await db.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.Status != SubscriptionStatus.Cancelled && s.PriceCents > 0 && s.CurrentPeriodEnd <= limit).ToListAsync(ct);
        var created = 0;
        foreach (var sub in subs)
        {
            if (await db.Invoices.IgnoreQueryFilters().AnyAsync(i => i.TenantId == sub.TenantId && i.SubscriptionId == sub.Id
                    && i.PeriodStart == sub.CurrentPeriodEnd && i.Status != InvoiceStatus.Void, ct)) continue;
            var inv = NewInvoice(sub, sub.CurrentPeriodEnd, "auto");
            db.Invoices.Add(inv);
            audit.Add("invoice.auto_generated", sub.TenantId, nameof(Invoice), inv.Id, new { inv.AmountCents, inv.DueAt, inv.PeriodStart, inv.PeriodEnd });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); continue; } // outra instância gerou ao mesmo tempo
            created++;
            await notifications.CreateAsync(sub.TenantId, "invoice.available", $"Nova fatura: {Brl(inv.AmountCents)}",
                $"Sua fatura do sistema vence em {inv.DueAt.ToOffset(TimeSpan.FromHours(-3)):dd/MM/yyyy}. Pague por PIX, boleto ou cartão em Faturas.",
                new { invoiceId = inv.Id, inv.AmountCents, inv.DueAt }, ct);
            inv.AvailableNotifiedAt = now;
            await db.SaveChangesAsync(ct);
            await EmailOwnersAsync(sub.TenantId, $"Nova fatura do sistema: {Brl(inv.AmountCents)}", "Nova fatura disponível",
                $"Sua fatura de {Brl(inv.AmountCents)} vence em {inv.DueAt.ToOffset(TimeSpan.FromHours(-3)):dd/MM/yyyy}. Pague por PIX, boleto ou cartão no menu Faturas do sistema.", "invoice_available");
        }
        return created;
    }

    /// <summary>Lembrete único para faturas em aberto que vencem nos próximos 3 dias.</summary>
    public async Task<int> SendRemindersAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var due = await db.Invoices.IgnoreQueryFilters()
            .Where(i => i.Status == InvoiceStatus.Open && i.ReminderSentAt == null && i.DueAt <= now.AddDays(ReminderDays)).ToListAsync(ct);
        foreach (var i in due)
        {
            var days = (int)Math.Ceiling((i.DueAt - now).TotalDays);
            var when = days < 0 ? "está vencida" : days == 0 ? "vence hoje" : $"vence em {days} dia(s)";
            await notifications.CreateAsync(i.TenantId, "invoice.due_soon", $"Fatura {when}: {Brl(i.AmountCents)}",
                $"Evite o bloqueio do sistema: pague por PIX, boleto ou cartão em Faturas.", new { invoiceId = i.Id, i.AmountCents, i.DueAt }, ct);
            i.ReminderSentAt = now;
            await db.SaveChangesAsync(ct);
            await EmailOwnersAsync(i.TenantId, $"Sua fatura {when}: {Brl(i.AmountCents)}", "Lembrete de pagamento",
                $"A fatura de {Brl(i.AmountCents)} {when}. Evite o bloqueio do sistema: pague por PIX, boleto ou cartão no menu Faturas.", "invoice_due_soon");
        }
        return due.Count;
    }

    private Invoice NewInvoice(Subscription sub, DateTimeOffset periodStart, string origin, DateTimeOffset? dueAt = null) => new()
    {
        TenantId = sub.TenantId, SubscriptionId = sub.Id, AmountCents = sub.PriceCents, DueAt = dueAt ?? periodStart,
        PeriodStart = periodStart, PeriodEnd = periodStart.AddMonths((int)sub.Period), Origin = origin, CreatedAt = clock.GetUtcNow(),
    };

    /// <summary>Painel: garante a fatura do período atual (não duplica se já existir).</summary>
    public async Task<Invoice> EnsureCurrentInvoiceAsync(Guid tenantId, int dueInDays)
    {
        var sub = await db.Subscriptions.IgnoreQueryFilters().Where(s => s.TenantId == tenantId && s.Status != SubscriptionStatus.Cancelled)
                      .OrderByDescending(s => s.StartedAt).FirstOrDefaultAsync() ?? throw new DomainException("no_subscription", "Empresa sem assinatura.", 404);
        var existing = await db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.TenantId == tenantId && i.SubscriptionId == sub.Id
            && i.PeriodStart == sub.CurrentPeriodEnd && i.Status != InvoiceStatus.Void);
        if (existing is not null) return existing;
        var inv = NewInvoice(sub, sub.CurrentPeriodEnd, "manual", clock.GetUtcNow().AddDays(Math.Clamp(dueInDays, 0, 60)));
        db.Invoices.Add(inv);
        audit.Add("invoice.created", tenantId, nameof(Invoice), inv.Id, new { inv.AmountCents, inv.DueAt });
        await db.SaveChangesAsync();
        return inv;
    }

    // ---------------- Pagamento pelo cliente ----------------

    private async Task<(Invoice Invoice, MercadoPagoConfig Mp, string Email)> PrepareAsync(Guid invoiceId)
    {
        var inv = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId) ?? throw new DomainException("not_found", "Fatura não encontrada.", 404);
        if (inv.Status != InvoiceStatus.Open) throw new DomainException("invoice_not_open", "Esta fatura não está em aberto.", 409);
        var cfg = await settings.MercadoPagoAsync() ?? throw new DomainException("payments_not_configured", "Pagamento online ainda não configurado. Fale com o suporte.", 409);
        var email = await db.Users.AsNoTracking().Where(u => u.Id == actor.UserId).Select(u => u.Email).FirstAsync();
        return (inv, cfg, email);
    }

    private static string? NotificationUrl(MercadoPagoConfig c) =>
        c.NotificationBaseUrl is { Length: > 0 } b ? b.TrimEnd('/') + "/webhooks/mercadopago" : null;

    public async Task<InvoiceView> PixAsync(Guid invoiceId, CancellationToken ct)
    {
        var (inv, cfg, email) = await PrepareAsync(invoiceId);
        var now = clock.GetUtcNow();
        if (inv.PixExpiresAt > now.AddMinutes(5) && inv.PixCopyPaste is not null) return View(inv); // reaproveita o PIX ainda válido
        var expires = now.AddHours(24);
        var r = await mp.CreatePixAsync(cfg.AccessToken, new MpPixRequest(inv.AmountCents, $"Mensalidade do sistema ({inv.PeriodStart:MM/yyyy})", inv.Id.ToString(),
            email, NotificationUrl(cfg), expires, $"pix-{inv.Id:N}-{now:yyyyMMddHH}"), ct);
        inv.MpPaymentId = r.PaymentId; inv.PixCopyPaste = r.QrCode; inv.PixQrBase64 = r.QrBase64; inv.PixExpiresAt = expires;
        audit.Add("invoice.pix_created", inv.TenantId, nameof(Invoice), inv.Id, new { r.PaymentId, expires });
        await db.SaveChangesAsync(ct);
        return View(inv);
    }

    public async Task<InvoiceView> CheckoutAsync(Guid invoiceId, CancellationToken ct)
    {
        var (inv, cfg, email) = await PrepareAsync(invoiceId);
        var now = clock.GetUtcNow();
        var expires = (inv.DueAt > now ? inv.DueAt : now).AddDays(7);
        var r = await mp.CreatePreferenceAsync(cfg.AccessToken, new MpPreferenceRequest($"Mensalidade do sistema ({inv.PeriodStart:MM/yyyy})", inv.AmountCents,
            inv.Id.ToString(), email, NotificationUrl(cfg), expires), ct);
        if (!Uri.TryCreate(r.InitPoint, UriKind.Absolute, out var u) || u.Scheme != "https" || !u.Host.EndsWith("mercadopago.com.br", StringComparison.OrdinalIgnoreCase)
            && !u.Host.EndsWith("mercadopago.com", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("payment_provider_error", "Link de pagamento inesperado.", 502);
        inv.CheckoutPreferenceId = r.Id; inv.CheckoutUrl = r.InitPoint;
        audit.Add("invoice.checkout_created", inv.TenantId, nameof(Invoice), inv.Id, new { preference = r.Id });
        await db.SaveChangesAsync(ct);
        return View(inv);
    }

    /// <summary>"Já paguei": consulta o Mercado Pago (útil quando o aviso automático ainda não chegou).</summary>
    public async Task<InvoiceView> RefreshAsync(Guid invoiceId, CancellationToken ct)
    {
        var inv = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw new DomainException("not_found", "Fatura não encontrada.", 404);
        if (inv.Status != InvoiceStatus.Open) return View(inv);
        var cfg = await settings.MercadoPagoAsync() ?? throw new DomainException("payments_not_configured", "Pagamento online não configurado.", 409);
        foreach (var p in await mp.SearchByReferenceAsync(cfg.AccessToken, inv.Id.ToString(), ct))
            if (await ApplyAsync(p, "consulta") == "applied") break;
        await db.Entry(inv).ReloadAsync(ct);
        return View(inv);
    }

    // ---------------- Confirmação (webhook ou consulta) ----------------

    /// <summary>Aplica um pagamento LIDO DO MERCADO PAGO (fonte da verdade). Idempotente por pagamento+status.</summary>
    public async Task<string> ApplyAsync(MpPayment p, string source)
    {
        var eventId = $"{p.Id}:{p.Status}";
        if (await db.PaymentEvents.AnyAsync(e => e.Provider == Provider && e.ProviderEventId == eventId)) return "duplicate";
        Guid.TryParse(p.ExternalReference, out var invoiceId);
        var invoice = invoiceId == Guid.Empty ? null : await db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == invoiceId);
        var ev = new PaymentEvent { Provider = Provider, ProviderEventId = eventId, Type = "payment." + p.Status, InvoiceId = invoiceId == Guid.Empty ? null : invoiceId, AmountCents = p.AmountCents,
            TenantId = invoice?.TenantId, PayloadSha256 = Hashing.Sha256Hex(JsonSerializer.Serialize(p)) };
        if (p.Status != "approved") ev.Result = "status_" + p.Status;
        else if (invoice is null) ev.Result = "invoice_not_found";
        else if (invoice.Status != InvoiceStatus.Open) ev.Result = "invoice_not_open";
        else if (p.Currency != "BRL" || p.AmountCents != invoice.AmountCents) ev.Result = "amount_mismatch";
        else
        {
            invoice.Status = InvoiceStatus.Paid;
            invoice.PaidAt = clock.GetUtcNow();
            invoice.ProviderReference = "mp:" + p.Id;
            invoice.PaidMethod = p.PaymentType switch
            {
                "bank_transfer" => "pix", "ticket" => "boleto", "credit_card" => "cartão de crédito", "debit_card" => "cartão de débito",
                "account_money" => "saldo Mercado Pago", _ => p.PaymentType,
            };
            var sub = await db.Subscriptions.IgnoreQueryFilters().FirstAsync(s => s.Id == invoice.SubscriptionId && s.TenantId == invoice.TenantId);
            if (invoice.PeriodEnd > sub.CurrentPeriodEnd) sub.CurrentPeriodEnd = invoice.PeriodEnd;
            if (sub.Status == SubscriptionStatus.Suspended) sub.Status = SubscriptionStatus.Active;
            ev.Result = "applied";
        }
        db.PaymentEvents.Add(ev);
        audit.Add("payment.mercadopago", ev.TenantId, nameof(Invoice), invoiceId, new { p.Id, p.Status, p.AmountCents, p.PaymentType, source, ev.Result },
            ev.Result == "applied" ? "ok" : "ignored");
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return "duplicate"; }
        if (ev.Result == "applied" && invoice is not null)
            await notifications.CreateAsync(invoice.TenantId, "invoice.paid", $"Pagamento confirmado: {Brl(invoice.AmountCents)}",
                $"Recebemos o pagamento da sua fatura ({invoice.PaidMethod}). Obrigado!", new { invoiceId = invoice.Id });
        if (ev.Result == "applied" && invoice is not null) await nfse.QueueForInvoiceAsync(invoice);
        if (ev.Result == "applied" && invoice is not null)
            await EmailOwnersAsync(invoice.TenantId, "Pagamento confirmado", "Pagamento confirmado",
                $"Recebemos o pagamento de {Brl(invoice.AmountCents)} ({invoice.PaidMethod}). Obrigado!", "invoice_paid");
        return ev.Result;
    }

    /// <summary>
    /// Assinatura do webhook do Mercado Pago: cabeçalho x-signature "ts=...,v1=..." com HMAC-SHA256 de
    /// "id:{data.id};request-id:{x-request-id};ts:{ts};" usando a assinatura secreta configurada no painel do Mercado Pago.
    /// </summary>
    public static bool VerifySignature(string secret, string? xSignature, string? xRequestId, string dataId, TimeProvider clock, out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(xSignature)) { reason = "missing_signature"; return false; }
        string? ts = null, v1 = null;
        foreach (var part in xSignature.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "ts") ts = kv[1].Trim();
            if (kv[0].Trim() == "v1") v1 = kv[1].Trim();
        }
        if (ts is null || v1 is null || !long.TryParse(ts, out var tsv)) { reason = "invalid_signature"; return false; }
        // ts pode vir em segundos ou milissegundos.
        var when = tsv > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(tsv) : DateTimeOffset.FromUnixTimeSeconds(tsv);
        if ((clock.GetUtcNow() - when).Duration() > TimeSpan.FromMinutes(10)) { reason = "stale_timestamp"; return false; }
        var manifest = $"id:{dataId};" + (string.IsNullOrEmpty(xRequestId) ? "" : $"request-id:{xRequestId};") + $"ts:{ts};";
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(v1.ToLowerInvariant())))
        { reason = "invalid_signature"; return false; }
        return true;
    }

    public async Task<(int Status, string Result)> HandleWebhookAsync(string? type, string? dataId, string? xSignature, string? xRequestId, CancellationToken ct)
    {
        var cfg = await settings.MercadoPagoAsync();
        if (cfg?.WebhookSecret is null) return (404, "provider_not_configured");
        if (string.IsNullOrWhiteSpace(dataId) || dataId.Length > 40 || !dataId.All(char.IsAsciiLetterOrDigit)) return (400, "invalid_payload");
        if (!VerifySignature(cfg.WebhookSecret, xSignature, xRequestId, dataId.ToLowerInvariant(), clock, out var reason))
        {
            await audit.WriteNowAsync("security.webhook_rejected", null, "webhook", null, new { provider = Provider, reason });
            return (401, reason);
        }
        if (type is not ("payment" or null)) return (200, "ignored_type");
        // Nunca confia no corpo do aviso: busca o pagamento no Mercado Pago com a nossa credencial.
        var p = await mp.GetPaymentAsync(cfg.AccessToken, dataId, ct);
        if (p is null) return (200, "payment_not_found");
        var result = await ApplyAsync(p, "webhook");
        log.LogInformation("Webhook Mercado Pago {Payment}: {Result}", dataId, result);
        return (200, result);
    }

    // ---------------- Painel administrativo ----------------

    public async Task<object> UpcomingAsync(int days)
    {
        var now = clock.GetUtcNow();
        var until = now.AddDays(Math.Clamp(days, 0, 60));
        var rows = await (from i in db.Invoices.IgnoreQueryFilters().AsNoTracking()
                          join t in db.Tenants.AsNoTracking() on i.TenantId equals t.Id
                          where i.Status == InvoiceStatus.Open && i.DueAt <= until
                          orderby i.DueAt
                          select new { i.Id, i.TenantId, t.Code, t.TradeName, i.AmountCents, i.DueAt, i.Origin, i.ReminderSentAt, hasPix = i.MpPaymentId != null, hasCheckout = i.CheckoutUrl != null })
                         .Take(500).ToListAsync();
        return rows.Select(r => new
        {
            r.Id, r.TenantId, r.Code, r.TradeName, r.AmountCents, r.DueAt, r.Origin, r.ReminderSentAt, r.hasPix, r.hasCheckout,
            overdue = r.DueAt < now, daysToDue = (int)Math.Ceiling((r.DueAt - now).TotalDays),
        });
    }

    public async Task<Invoice> MarkPaidManuallyAsync(Guid invoiceId, string confirmTenantCode, string reason)
    {
        var inv = await db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == invoiceId) ?? throw new DomainException("not_found", "Fatura não encontrada.", 404);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == inv.TenantId);
        if (confirmTenantCode != tenant.Code) throw new DomainException("confirmation_mismatch", "Código de confirmação não confere.", 409);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5) throw new DomainException("reason_required", "Informe o motivo (ex.: pago por transferência).");
        if (inv.Status != InvoiceStatus.Open) throw new DomainException("invoice_not_open", "Fatura não está em aberto.", 409);
        inv.Status = InvoiceStatus.Paid; inv.PaidAt = clock.GetUtcNow(); inv.PaidMethod = "manual"; inv.ProviderReference = "manual:" + actor.AdminId;
        var sub = await db.Subscriptions.IgnoreQueryFilters().FirstAsync(s => s.Id == inv.SubscriptionId && s.TenantId == inv.TenantId);
        if (inv.PeriodEnd > sub.CurrentPeriodEnd) sub.CurrentPeriodEnd = inv.PeriodEnd;
        if (sub.Status == SubscriptionStatus.Suspended) sub.Status = SubscriptionStatus.Active;
        audit.Add("invoice.marked_paid_manually", inv.TenantId, nameof(Invoice), inv.Id, new { reason = reason.Trim(), inv.AmountCents });
        await db.SaveChangesAsync();
        await nfse.QueueForInvoiceAsync(inv);
        return inv;
    }
}

/// <summary>Gera faturas e envia lembretes periodicamente (padrão: a cada hora; desligado nos testes automatizados).</summary>
public sealed class BillingJob(IServiceProvider sp, IConfiguration config, ILogger<BillingJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("Billing:AutoRun", true)) return;
        var interval = TimeSpan.FromMinutes(Math.Max(5, config.GetValue("Billing:IntervalMinutes", 60)));
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
                var billing = scope.ServiceProvider.GetRequiredService<BillingService>();
                var created = await billing.GenerateDueInvoicesAsync(ct);
                var reminded = await billing.SendRemindersAsync(ct);
                if (created + reminded > 0) log.LogInformation("Faturamento: {Created} fatura(s) gerada(s), {Reminded} lembrete(s)", created, reminded);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Falha na geração automática de faturas"); }
            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { return; }
        }
    }
}

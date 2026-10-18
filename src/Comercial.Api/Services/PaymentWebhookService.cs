using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record PaymentWebhookPayload(string EventId, string Type, Guid InvoiceId, long AmountCents);

/// <summary>
/// Webhook genérico de pagamento. Um provedor real (Asaas, Mercado Pago, Stripe, Pagar.me, etc.)
/// deve ter um adaptador que valide a assinatura no formato DELE. A regra é a mesma:
/// só o evento autenticado do provedor confirma pagamento — nunca o redirecionamento do navegador.
/// </summary>
public sealed class PaymentWebhookService(AppDbContext db, AuditService audit, IConfiguration config, TimeProvider clock, ILogger<PaymentWebhookService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromMinutes(5);

    public static string Sign(string secret, string timestamp, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body))).ToLowerInvariant();

    public async Task<(int Status, string Result)> HandleAsync(string provider, string body, string? timestamp, string? signature)
    {
        var secret = config[$"Payments:{provider}:WebhookSecret"];
        if (string.IsNullOrEmpty(secret)) return (404, "provider_not_configured");

        if (timestamp is null || signature is null || !long.TryParse(timestamp, out var ts)) return await Deny("missing_signature");
        var age = clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(ts);
        if (age.Duration() > ReplayWindow) return await Deny("stale_timestamp");
        var expected = Sign(secret, timestamp, body);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.ToLowerInvariant())))
            return await Deny("invalid_signature");

        PaymentWebhookPayload? p;
        try { p = JsonSerializer.Deserialize<PaymentWebhookPayload>(body, Json); } catch (JsonException) { p = null; }
        if (p is null || string.IsNullOrWhiteSpace(p.EventId) || p.EventId.Length > 120) return (400, "invalid_payload");

        if (await db.PaymentEvents.AnyAsync(e => e.Provider == provider && e.ProviderEventId == p.EventId)) return (200, "duplicate");

        var ev = new PaymentEvent { Provider = provider, ProviderEventId = p.EventId, Type = p.Type, InvoiceId = p.InvoiceId, AmountCents = p.AmountCents, PayloadSha256 = Hashing.Sha256Hex(body) };
        var invoice = await db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == p.InvoiceId);
        ev.TenantId = invoice?.TenantId;

        if (p.Type != "invoice.paid") ev.Result = "ignored_type";
        else if (invoice is null) ev.Result = "invoice_not_found";
        else if (invoice.Status != InvoiceStatus.Open) ev.Result = "invoice_not_open";
        else if (invoice.AmountCents != p.AmountCents) ev.Result = "amount_mismatch";
        else
        {
            invoice.Status = InvoiceStatus.Paid;
            invoice.PaidAt = clock.GetUtcNow();
            invoice.ProviderReference = p.EventId;
            var sub = await db.Subscriptions.IgnoreQueryFilters().FirstAsync(s => s.Id == invoice.SubscriptionId && s.TenantId == invoice.TenantId);
            if (invoice.PeriodEnd > sub.CurrentPeriodEnd) sub.CurrentPeriodEnd = invoice.PeriodEnd;
            if (sub.Status == SubscriptionStatus.Suspended) sub.Status = SubscriptionStatus.Active;
            ev.Result = "applied";
        }
        db.PaymentEvents.Add(ev);
        audit.Add("payment.webhook", ev.TenantId, nameof(Invoice), p.InvoiceId, new { provider, p.EventId, p.Type, p.AmountCents, ev.Result }, ev.Result == "applied" ? "ok" : "ignored");
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException ex)
        {
            log.LogWarning(ex, "Evento de pagamento concorrente {EventId}", p.EventId);
            return (200, "duplicate");
        }
        return (200, ev.Result);
    }

    private async Task<(int, string)> Deny(string reason)
    {
        await audit.WriteNowAsync("security.webhook_rejected", null, "webhook", null, new { reason });
        return (401, reason);
    }
}

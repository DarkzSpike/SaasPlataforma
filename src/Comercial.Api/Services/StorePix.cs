using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record StorePaymentSettingsRequest(string? MpAccessToken, bool PixEnabled, bool RemoveToken = false);
public sealed record StorePaymentSettingsView(bool Configured, bool PixEnabled, string? AccountLabel, string? TokenHint, bool IsTestToken);
public sealed record CreatePixChargeRequest(Guid CashSessionId, long AmountCents);
public sealed record PixChargeView(Guid Id, long AmountCents, string Status, string QrCode, string QrBase64, DateTimeOffset ExpiresAt);

/// <summary>
/// PIX dinâmico no caixa: cobrança criada na conta Mercado Pago DA LOJA; o PDV consulta até o Mercado Pago confirmar.
/// A venda só aceita o PIX como pago com uma cobrança aprovada, do mesmo valor, ainda não usada.
/// </summary>
public sealed class StorePixService(AppDbContext db, RequestActor actor, AuditService audit, IDataProtectionProvider dp, IMercadoPagoClient mp, TimeProvider clock)
{
    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);
    private IDataProtector Protector => dp.CreateProtector("Comercial.StorePayments.v1");

    private Task<TenantPaymentSettings?> FindAsync() => db.TenantPaymentSettings.FirstOrDefaultAsync(x => x.TenantId == actor.TenantId);

    public async Task<StorePaymentSettingsView> GetAsync()
    {
        var s = await FindAsync();
        string? hint = null; var test = false;
        if (s?.MpAccessTokenProtected is { } p) { var tok = Protector.Unprotect(p); hint = "final ..." + tok[^4..]; test = tok.StartsWith("TEST-"); }
        return new StorePaymentSettingsView(s?.MpAccessTokenProtected is not null, s?.PixEnabled ?? false, s?.MpAccountLabel, hint, test);
    }

    public async Task<StorePaymentSettingsView> SaveAsync(StorePaymentSettingsRequest r, CancellationToken ct)
    {
        var s = await FindAsync();
        if (s is null) { s = new TenantPaymentSettings { TenantId = actor.TenantId }; db.TenantPaymentSettings.Add(s); }
        if (!string.IsNullOrWhiteSpace(r.MpAccessToken))
        {
            var tok = r.MpAccessToken.Trim();
            if (!(tok.StartsWith("APP_USR-") || tok.StartsWith("TEST-")) || tok.Length > 300)
                throw new DomainException("invalid_token", "Access Token do Mercado Pago inválido (começa com APP_USR- ou TEST-).");
            var (ok, message) = await mp.CheckAsync(tok, ct);
            if (!ok) throw new DomainException("invalid_token", "O Mercado Pago recusou esta credencial: " + message);
            s.MpAccessTokenProtected = Protector.Protect(tok);
            s.MpAccountLabel = message.Replace("Conta conectada: ", "");
        }
        else if (r.RemoveToken) { s.MpAccessTokenProtected = null; s.MpAccountLabel = null; }
        if (r.PixEnabled && s.MpAccessTokenProtected is null) throw new DomainException("credentials_required", "Informe a credencial do Mercado Pago da loja para ativar o PIX no caixa.");
        s.PixEnabled = r.PixEnabled;
        s.Version++; s.UpdatedAt = clock.GetUtcNow(); s.UpdatedByUserId = actor.UserId;
        audit.Add("store.payments_updated", actor.TenantId, nameof(TenantPaymentSettings), actor.TenantId,
            new { r.PixEnabled, tokenChanged = !string.IsNullOrWhiteSpace(r.MpAccessToken) || r.RemoveToken, account = s.MpAccountLabel });
        await db.SaveChangesAsync(ct);
        return await GetAsync();
    }

    private async Task<string> TokenAsync()
    {
        var s = await FindAsync();
        if (s is not { PixEnabled: true, MpAccessTokenProtected: { } p }) throw new DomainException("pix_not_configured", "PIX no caixa não está ativado nesta loja.", 409);
        return Protector.Unprotect(p);
    }

    private static PixChargeView View(PosPixCharge c) => new(c.Id, c.AmountCents, c.Status, c.QrCode, c.QrBase64, c.ExpiresAt);

    public async Task<PixChargeView> CreateAsync(CreatePixChargeRequest r, CancellationToken ct)
    {
        if (r.AmountCents <= 0 || r.AmountCents > 100_000_00) throw new DomainException("invalid_amount", "Valor inválido.");
        var session = await db.CashSessions.FirstOrDefaultAsync(c => c.Id == r.CashSessionId, ct) ?? throw new DomainException("not_found", "Caixa não encontrado.", 404);
        if (session.Status != CashSessionStatus.Open) throw new DomainException("cash_not_open", "Caixa não está aberto.", 409);
        var token = await TokenAsync();
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == actor.TenantId, ct);
        var email = await db.Users.AsNoTracking().Where(u => u.Id == actor.UserId).Select(u => u.Email).FirstAsync(ct);
        var now = clock.GetUtcNow();
        var c = new PosPixCharge { CashSessionId = session.Id, RequestedByUserId = actor.UserId!.Value, AmountCents = r.AmountCents, CreatedAt = now, ExpiresAt = now + Validity };
        // Sem webhook: o PDV consulta o status a cada poucos segundos (funciona sem domínio público).
        var pix = await mp.CreatePixAsync(token, new MpPixRequest(r.AmountCents, $"Compra em {tenant.TradeName}", "pos:" + c.Id, email, null, c.ExpiresAt, "pospix-" + c.Id.ToString("N")), ct);
        c.MpPaymentId = pix.PaymentId; c.QrCode = pix.QrCode; c.QrBase64 = pix.QrBase64;
        db.PosPixCharges.Add(c);
        audit.Add("pos.pix_created", actor.TenantId, nameof(PosPixCharge), c.Id, new { r.AmountCents, pix.PaymentId });
        await db.SaveChangesAsync(ct);
        return View(c);
    }

    /// <summary>Consulta o Mercado Pago (fonte da verdade) e atualiza o status da cobrança.</summary>
    public async Task<PixChargeView> StatusAsync(Guid id, CancellationToken ct)
    {
        var c = await db.PosPixCharges.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new DomainException("not_found", "Cobrança não encontrada.", 404);
        if (c.Status != PixChargeStatus.Pending) return View(c);
        var p = await mp.GetPaymentAsync(await TokenAsync(), c.MpPaymentId, ct);
        var now = clock.GetUtcNow();
        if (p is { Status: "approved" } && p.AmountCents == c.AmountCents && p.ExternalReference == "pos:" + c.Id)
        {
            c.Status = PixChargeStatus.Approved; c.ApprovedAt = now;
            audit.Add("pos.pix_approved", actor.TenantId, nameof(PosPixCharge), c.Id, new { c.AmountCents, c.MpPaymentId });
        }
        else if (p is { Status: "cancelled" or "rejected" }) c.Status = PixChargeStatus.Cancelled;
        else if (now > c.ExpiresAt) c.Status = PixChargeStatus.Expired;
        await db.SaveChangesAsync(ct);
        return View(c);
    }

    public async Task CancelAsync(Guid id, CancellationToken ct)
    {
        var c = await db.PosPixCharges.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new DomainException("not_found", "Cobrança não encontrada.", 404);
        if (c.Status != PixChargeStatus.Pending) return;
        await mp.CancelPaymentAsync(await TokenAsync(), c.MpPaymentId, ct);
        c.Status = PixChargeStatus.Cancelled;
        audit.Add("pos.pix_cancelled", actor.TenantId, nameof(PosPixCharge), c.Id);
        await db.SaveChangesAsync(ct);
    }
}

using System.Text.RegularExpressions;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record CustomerInput(string Name, string? Document, string? Email, string? Phone, DateOnly? BirthDate, string? Notes, bool MarketingConsent);
public sealed record LoyaltyProgramInput(bool Enabled, int PointsPerReal, int CentsPerPoint, int MinPointsToRedeem, int ExpirationDays, int MaxRedeemPercent);
public sealed record LoyaltyBalance(int Available, int Earned, int Redeemed, int Expired, DateTimeOffset? NextExpiration);

public sealed partial class CustomerService(AppDbContext db, RequestActor actor, AuditService audit, ScopedLookup lookup)
{
    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigits();

    public static string? NormalizeDocument(string? d)
    {
        if (string.IsNullOrWhiteSpace(d)) return null;
        var digits = NonDigits().Replace(d, "");
        if (digits.Length is not (11 or 14)) throw new DomainException("invalid_document", "CPF deve ter 11 dígitos e CNPJ 14.");
        return digits;
    }

    public async Task<Customer> SaveAsync(Guid? id, CustomerInput r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) throw new DomainException("invalid_name", "Nome inválido.");
        if (r.Email is { Length: > 200 } || (r.Email is { Length: > 0 } && !r.Email.Contains('@'))) throw new DomainException("invalid_email", "E-mail inválido.");
        if (r.Phone is { Length: > 30 } || r.Notes is { Length: > 1000 }) throw new DomainException("invalid_field", "Campo muito longo.");
        var doc = NormalizeDocument(r.Document);
        if (doc is not null && await db.Customers.AnyAsync(c => c.Document == doc && c.Id != id))
            throw new DomainException("document_taken", "Já existe cliente com este CPF/CNPJ.", 409);

        var c = id is { } existing ? await lookup.GetAsync<Customer>(existing) : new Customer();
        if (c.AnonymizedAt is not null) throw new DomainException("anonymized", "Cliente anonimizado não pode ser alterado.", 409);
        var consentChanged = c.MarketingConsent != r.MarketingConsent;
        c.Name = r.Name.Trim(); c.Document = doc; c.Email = r.Email?.Trim(); c.Phone = r.Phone?.Trim(); c.BirthDate = r.BirthDate; c.Notes = r.Notes;
        if (consentChanged || id is null) { c.MarketingConsent = r.MarketingConsent; c.ConsentAt = DateTimeOffset.UtcNow; }
        if (id is null) db.Customers.Add(c);
        audit.Add(id is null ? "customer.created" : "customer.updated", actor.TenantId, nameof(Customer), c.Id,
            new { consent = c.MarketingConsent, consentChanged }); // sem dados pessoais na auditoria
        await db.SaveChangesAsync();
        return c;
    }

    /// <summary>LGPD: todos os dados do titular em formato estruturado (inclui compras e pontos).</summary>
    public async Task<object> ExportAsync(Guid id)
    {
        var c = await lookup.GetAsync<Customer>(id);
        var sales = await db.Sales.AsNoTracking().Where(s => s.CustomerId == id)
            .Select(s => new { s.Id, s.ReceivedAt, s.TotalCents, s.Status, items = s.Items.Select(i => new { i.ProductName, i.Quantity, i.TotalCents }) }).ToListAsync();
        var points = await db.LoyaltyTransactions.AsNoTracking().Where(t => t.CustomerId == id).OrderBy(t => t.CreatedAt)
            .Select(t => new { t.Type, t.Points, t.CreatedAt, t.ExpiresAt, t.Reason }).ToListAsync();
        audit.Add("customer.data_exported", actor.TenantId, nameof(Customer), id);
        await db.SaveChangesAsync();
        return new
        {
            exportedAt = DateTimeOffset.UtcNow,
            customer = new { c.Id, c.Name, c.Document, c.Email, c.Phone, c.BirthDate, c.Notes, c.MarketingConsent, c.ConsentAt, c.CreatedAt, c.AnonymizedAt },
            sales, loyalty = points,
            note = "Dados mantidos por obrigação legal (vendas/fiscal) permanecem após anonimização, sem identificação do titular.",
        };
    }

    /// <summary>LGPD: remove dados pessoais. Vendas permanecem (obrigação fiscal), mas sem identificar o titular.</summary>
    public async Task AnonymizeAsync(Guid id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5) throw new DomainException("reason_required", "Informe o motivo (ex.: pedido do titular).");
        var c = await lookup.GetAsync<Customer>(id);
        if (c.AnonymizedAt is not null) return;
        c.Name = "Cliente anonimizado"; c.Document = null; c.Email = null; c.Phone = null; c.BirthDate = null; c.Notes = null;
        c.MarketingConsent = false; c.IsActive = false; c.AnonymizedAt = DateTimeOffset.UtcNow;
        audit.Add("customer.anonymized", actor.TenantId, nameof(Customer), id, new { reason });
        await db.SaveChangesAsync();
    }
}

/// <summary>Fidelidade: crédito na venda, resgate como desconto, estorno no cancelamento/devolução, validade FIFO.</summary>
public sealed class LoyaltyService(AppDbContext db, TimeProvider clock)
{
    public async Task<LoyaltyProgram> ProgramAsync(Guid tenantId) =>
        await db.LoyaltyPrograms.FirstOrDefaultAsync(p => p.TenantId == tenantId) ?? new LoyaltyProgram { TenantId = tenantId };

    /// <summary>Saldo com validade: resgates consomem os pontos mais antigos primeiro; o que vence sem uso expira.</summary>
    public async Task<LoyaltyBalance> BalanceAsync(Guid customerId)
    {
        var now = clock.GetUtcNow();
        var txs = await db.LoyaltyTransactions.AsNoTracking().Where(t => t.CustomerId == customerId).ToListAsync();
        return Compute(txs, now);
    }

    public static LoyaltyBalance Compute(IReadOnlyList<LoyaltyTransaction> txs, DateTimeOffset now)
    {
        // Lotes de crédito (FIFO). Estornos de crédito removem do lote da própria venda.
        var lots = txs.Where(t => t.Type is LoyaltyType.Earn or LoyaltyType.RedeemRefund or LoyaltyType.Adjust && t.Points > 0)
            .OrderBy(t => t.CreatedAt).Select(t => new Lot(t.Points, t.ExpiresAt, t.SaleId, t.Type)).ToList();
        foreach (var rev in txs.Where(t => t.Type == LoyaltyType.EarnReversal))
        {
            var left = -rev.Points;
            foreach (var lot in lots.Where(l => l.SaleId == rev.SaleId && l.Type == LoyaltyType.Earn))
            { var take = Math.Min(left, lot.Remaining); lot.Remaining -= take; left -= take; }
        }
        // Débitos (resgates e ajustes negativos) em ordem cronológica, consumindo lotes válidos naquele momento.
        var debits = txs.Where(t => t.Type == LoyaltyType.Redeem || (t.Type == LoyaltyType.Adjust && t.Points < 0)).OrderBy(t => t.CreatedAt);
        foreach (var d in debits)
        {
            var left = -d.Points;
            foreach (var lot in lots.Where(l => l.Remaining > 0 && (l.ExpiresAt is null || l.ExpiresAt > d.CreatedAt)))
            { var take = Math.Min(left, lot.Remaining); lot.Remaining -= take; left -= take; if (left == 0) break; }
        }
        var valid = lots.Where(l => l.ExpiresAt is null || l.ExpiresAt > now).ToList();
        return new LoyaltyBalance(
            valid.Sum(l => l.Remaining),
            txs.Where(t => t.Type == LoyaltyType.Earn).Sum(t => t.Points) + txs.Where(t => t.Type == LoyaltyType.EarnReversal).Sum(t => t.Points),
            -txs.Where(t => t.Type == LoyaltyType.Redeem).Sum(t => t.Points) - txs.Where(t => t.Type == LoyaltyType.RedeemRefund).Sum(t => t.Points),
            lots.Where(l => l.ExpiresAt is not null && l.ExpiresAt <= now).Sum(l => l.Remaining),
            valid.Where(l => l.Remaining > 0 && l.ExpiresAt is not null).Min(l => l.ExpiresAt));
    }

    private sealed class Lot(int points, DateTimeOffset? expiresAt, Guid? saleId, LoyaltyType type)
    {
        public int Remaining { get; set; } = points;
        public DateTimeOffset? ExpiresAt { get; } = expiresAt;
        public Guid? SaleId { get; } = saleId;
        public LoyaltyType Type { get; } = type;
    }

    public static int PointsFor(long totalCents, LoyaltyProgram p) => (int)(totalCents / 100 * p.PointsPerReal);
}

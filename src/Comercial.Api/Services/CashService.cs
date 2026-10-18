using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record OpenCashRequest(Guid StoreId, long OpeningCents, Guid? DeviceId);
public sealed record CashMovementRequest(long AmountCents, string Reason);
public sealed record CloseCashRequest(long DeclaredCashCents, string? Justification);

public sealed class CashService(AppDbContext db, RequestActor actor, AuditService audit, StockService stock, ScopedLookup lookup, NotificationService notifications)
{
    public async Task<CashSession> OpenAsync(OpenCashRequest r)
    {
        if (r.OpeningCents < 0 || r.OpeningCents > 100_000_00) throw new DomainException("invalid_amount", "Valor inicial inválido.");
        await stock.EnsureStoreAccessAsync(r.StoreId);
        if (await db.CashSessions.AnyAsync(c => c.OperatorUserId == actor.UserId && c.Status == CashSessionStatus.Open))
            throw new DomainException("cash_already_open", "Já existe um caixa aberto para este operador.", 409);

        var s = new CashSession { StoreId = r.StoreId, DeviceId = r.DeviceId ?? actor.DeviceId, OperatorUserId = actor.UserId!.Value, OpeningCents = r.OpeningCents };
        db.CashSessions.Add(s);
        db.CashMovements.Add(new CashMovement { CashSessionId = s.Id, Type = CashMovementType.Opening, AmountCents = r.OpeningCents, UserId = actor.UserId.Value });
        audit.Add("cash.opened", actor.TenantId, nameof(CashSession), s.Id, new { r.StoreId, r.OpeningCents });
        await db.SaveChangesAsync();
        return s;
    }

    public async Task<long> ExpectedCashAsync(Guid sessionId) =>
        await db.CashMovements.Where(m => m.CashSessionId == sessionId).SumAsync(m => m.AmountCents);

    public async Task<CashMovement> MoveAsync(Guid sessionId, CashMovementType type, CashMovementRequest r)
    {
        if (r.AmountCents <= 0) throw new DomainException("invalid_amount", "Valor deve ser positivo.");
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length > 300) throw new DomainException("reason_required", "Informe o motivo.");
        var s = await GetOwnOpenAsync(sessionId);
        var amount = type == CashMovementType.Withdrawal ? -r.AmountCents : r.AmountCents;
        if (type == CashMovementType.Withdrawal && await ExpectedCashAsync(s.Id) + amount < 0)
            throw new DomainException("insufficient_cash", "Sangria maior que o saldo esperado em dinheiro.", 409);
        var m = new CashMovement { CashSessionId = s.Id, Type = type, AmountCents = amount, Reason = r.Reason, UserId = actor.UserId!.Value };
        db.CashMovements.Add(m);
        audit.Add(type == CashMovementType.Withdrawal ? "cash.withdrawal" : "cash.supply", actor.TenantId, nameof(CashSession), s.Id, new { r.AmountCents, r.Reason });
        await db.SaveChangesAsync();
        return m;
    }

    public async Task<CashSession> CloseAsync(Guid sessionId, CloseCashRequest r)
    {
        var s = await lookup.GetAsync<CashSession>(sessionId);
        if (s.Status != CashSessionStatus.Open) throw new DomainException("cash_not_open", "Caixa não está aberto.", 409);
        if (s.OperatorUserId != actor.UserId && !actor.Permissions.Contains(Permissions.CashReview))
            throw new DomainException("not_owner", "Somente o operador do caixa ou um gerente pode fechá-lo.", 403);
        if (r.DeclaredCashCents < 0) throw new DomainException("invalid_amount", "Valor informado inválido.");

        var expected = await ExpectedCashAsync(s.Id);
        var diff = r.DeclaredCashCents - expected;
        if (diff != 0 && string.IsNullOrWhiteSpace(r.Justification))
            throw new DomainException("justification_required", "Há diferença de caixa: informe uma justificativa.");

        s.Status = CashSessionStatus.Closed;
        s.ClosedAt = DateTimeOffset.UtcNow;
        s.ExpectedCashCents = expected;
        s.DeclaredCashCents = r.DeclaredCashCents;
        s.DifferenceCents = diff;
        s.Justification = r.Justification;
        audit.Add("cash.closed", actor.TenantId, nameof(CashSession), s.Id, new { expected, r.DeclaredCashCents, diff });
        await db.SaveChangesAsync();

        // Quebra de caixa (faltou dinheiro): avisa o dono no app (push quando configurado).
        if (diff < 0)
        {
            var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
            var who = await db.Users.AsNoTracking().Where(u => u.Id == s.OperatorUserId).Select(u => u.DisplayName).FirstOrDefaultAsync() ?? "operador";
            var store = await db.Stores.AsNoTracking().Where(x => x.Id == s.StoreId).Select(x => x.Name).FirstOrDefaultAsync() ?? "";
            await notifications.CreateAsync(actor.TenantId, "cash.shortage", $"Quebra de caixa: {(-diff / 100m).ToString("C", pt)}",
                $"Caixa de {who} ({store}) fechado com falta de {(-diff / 100m).ToString("C", pt)}. Esperado {(expected / 100m).ToString("C", pt)}, contado {(r.DeclaredCashCents / 100m).ToString("C", pt)}. Justificativa: {r.Justification}",
                new { cashSessionId = s.Id, diffCents = diff, expectedCents = expected, declaredCents = r.DeclaredCashCents });
        }
        return s;
    }

    public async Task<CashSession> ReviewAsync(Guid sessionId)
    {
        var s = await lookup.GetAsync<CashSession>(sessionId);
        if (s.Status != CashSessionStatus.Closed) throw new DomainException("cash_not_closed", "Somente caixas fechados podem ser revisados.", 409);
        s.ReviewedByUserId = actor.UserId;
        audit.Add("cash.reviewed", actor.TenantId, nameof(CashSession), s.Id);
        await db.SaveChangesAsync();
        return s;
    }

    private async Task<CashSession> GetOwnOpenAsync(Guid sessionId)
    {
        var s = await lookup.GetAsync<CashSession>(sessionId);
        if (s.Status != CashSessionStatus.Open) throw new DomainException("cash_not_open", "Caixa não está aberto.", 409);
        if (s.OperatorUserId != actor.UserId) throw new DomainException("not_owner", "Caixa pertence a outro operador.", 403);
        return s;
    }
}

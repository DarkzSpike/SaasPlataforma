using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record TitleInput(int Kind, string Description, string Category, string? CostCenter, string? Counterparty, Guid? SupplierId, Guid? CustomerId,
    DateOnly FirstDueDate, long TotalCents, int Installments = 1, int RecurrenceMonths = 0, string? Notes = null);
public sealed record PayTitleRequest(long? AmountCents, DateOnly? PaidOn, string? Method);
public sealed record ExpenseInput(string Description, string Category, long AmountCents, DateOnly PaidOn, string? Method, string? CostCenter, string? Notes);

/// <summary>Contas a pagar/receber com parcelas ou recorrência mensal; despesas são contas a pagar já quitadas.</summary>
public sealed class FinanceService(AppDbContext db, RequestActor actor, AuditService audit, ScopedLookup lookup, TimeProvider clock)
{
    public async Task<List<FinancialTitle>> CreateAsync(TitleInput r)
    {
        if (!Enum.IsDefined(typeof(TitleKind), r.Kind)) throw new DomainException("invalid_kind", "Tipo inválido (1 = pagar, 2 = receber).");
        if (string.IsNullOrWhiteSpace(r.Description) || r.Description.Length > 200) throw new DomainException("invalid_description", "Descrição inválida.");
        if (string.IsNullOrWhiteSpace(r.Category) || r.Category.Length > 60) throw new DomainException("invalid_category", "Categoria inválida.");
        if (r.TotalCents <= 0 || r.TotalCents > 1_000_000_000) throw new DomainException("invalid_amount", "Valor inválido.");
        if (r.Installments is < 1 or > 60 || r.RecurrenceMonths is < 0 or > 60) throw new DomainException("invalid_installments", "Parcelas/recorrência inválidas.");
        if (r.Installments > 1 && r.RecurrenceMonths > 0) throw new DomainException("invalid_installments", "Use parcelas OU recorrência.");
        if (r.SupplierId is { } sid) await lookup.GetAsync<Supplier>(sid);
        if (r.CustomerId is { } cid) await lookup.GetAsync<Customer>(cid);

        var group = Guid.NewGuid();
        var count = r.RecurrenceMonths > 0 ? r.RecurrenceMonths : r.Installments;
        var list = new List<FinancialTitle>();
        for (var i = 0; i < count; i++)
        {
            // Parcelas: divide o total (a última absorve o arredondamento). Recorrência: repete o valor todo mês.
            var amount = r.RecurrenceMonths > 0 ? r.TotalCents
                : i < count - 1 ? r.TotalCents / count : r.TotalCents - r.TotalCents / count * (count - 1);
            list.Add(new FinancialTitle
            {
                Kind = (TitleKind)r.Kind, Description = count > 1 ? $"{r.Description.Trim()} ({i + 1}/{count})" : r.Description.Trim(),
                Category = r.Category.Trim(), CostCenter = r.CostCenter, Counterparty = r.Counterparty, SupplierId = r.SupplierId, CustomerId = r.CustomerId,
                DueDate = r.FirstDueDate.AddMonths(i), AmountCents = amount, GroupId = group, InstallmentNumber = i + 1, InstallmentCount = count,
                Notes = r.Notes, CreatedByUserId = actor.UserId!.Value,
            });
        }
        db.FinancialTitles.AddRange(list);
        audit.Add("finance.title_created", actor.TenantId, nameof(FinancialTitle), group, new { r.Kind, r.Category, r.TotalCents, count });
        await db.SaveChangesAsync();
        return list;
    }

    public async Task<FinancialTitle> PayAsync(Guid id, PayTitleRequest r)
    {
        var t = await lookup.GetAsync<FinancialTitle>(id);
        if (t.Status != TitleStatus.Open) throw new DomainException("invalid_status", "Título não está em aberto.", 409);
        var amount = r.AmountCents ?? t.AmountCents;
        if (amount <= 0 || amount > t.AmountCents * 10) throw new DomainException("invalid_amount", "Valor pago inválido.");
        t.Status = TitleStatus.Paid;
        t.PaidAmountCents = amount;
        t.PaidOn = r.PaidOn ?? DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        t.PaymentMethod = r.Method;
        audit.Add(t.Kind == TitleKind.Payable ? "finance.paid" : "finance.received", actor.TenantId, nameof(FinancialTitle), t.Id, new { amount, original = t.AmountCents, t.PaidOn });
        await db.SaveChangesAsync();
        return t;
    }

    public async Task<FinancialTitle> CancelAsync(Guid id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5) throw new DomainException("reason_required", "Informe o motivo.");
        var t = await lookup.GetAsync<FinancialTitle>(id);
        if (t.Status != TitleStatus.Open) throw new DomainException("invalid_status", "Só títulos em aberto podem ser cancelados (estorne o pagamento antes).", 409);
        t.Status = TitleStatus.Cancelled;
        t.CancelReason = reason.Trim();
        audit.Add("finance.cancelled", actor.TenantId, nameof(FinancialTitle), t.Id, new { reason });
        await db.SaveChangesAsync();
        return t;
    }

    /// <summary>Estorno de baixa: volta o título para "em aberto" (com motivo, auditado).</summary>
    public async Task<FinancialTitle> ReopenAsync(Guid id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5) throw new DomainException("reason_required", "Informe o motivo do estorno.");
        var t = await lookup.GetAsync<FinancialTitle>(id);
        if (t.Status != TitleStatus.Paid) throw new DomainException("invalid_status", "Só títulos pagos podem ser estornados.", 409);
        audit.Add("finance.payment_reversed", actor.TenantId, nameof(FinancialTitle), t.Id, new { reason, t.PaidAmountCents, t.PaidOn });
        t.Status = TitleStatus.Open; t.PaidAmountCents = null; t.PaidOn = null; t.PaymentMethod = null;
        await db.SaveChangesAsync();
        return t;
    }

    public async Task<FinancialTitle> ExpenseAsync(ExpenseInput r)
    {
        var t = (await CreateAsync(new TitleInput(1, r.Description, r.Category, r.CostCenter, null, null, null, r.PaidOn, r.AmountCents, 1, 0, r.Notes)))[0];
        return await PayAsync(t.Id, new PayTitleRequest(r.AmountCents, r.PaidOn, r.Method));
    }

    /// <summary>Fluxo de caixa: realizado (pagos/recebidos) e previsto (em aberto) por dia, mais vencidos.</summary>
    public async Task<object> SummaryAsync(DateOnly from, DateOnly to)
    {
        if (to < from || to.DayNumber - from.DayNumber > 400) throw new DomainException("invalid_period", "Período inválido.");
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var open = await db.FinancialTitles.AsNoTracking().Where(t => t.Status == TitleStatus.Open).ToListAsync();
        var paid = await db.FinancialTitles.AsNoTracking().Where(t => t.Status == TitleStatus.Paid && t.PaidOn >= from && t.PaidOn <= to).ToListAsync();
        long Sum(IEnumerable<FinancialTitle> x, TitleKind k, bool real) => x.Where(t => t.Kind == k).Sum(t => real ? t.PaidAmountCents ?? 0 : t.AmountCents);
        var inPeriod = open.Where(t => t.DueDate >= from && t.DueDate <= to).ToList();
        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(i => from.AddDays(i)).Select(d => new
        {
            date = d,
            receivedCents = Sum(paid.Where(t => t.PaidOn == d), TitleKind.Receivable, true),
            paidCents = Sum(paid.Where(t => t.PaidOn == d), TitleKind.Payable, true),
            expectedInCents = Sum(inPeriod.Where(t => t.DueDate == d), TitleKind.Receivable, false),
            expectedOutCents = Sum(inPeriod.Where(t => t.DueDate == d), TitleKind.Payable, false),
        }).Where(d => d.receivedCents + d.paidCents + d.expectedInCents + d.expectedOutCents > 0).ToList();
        return new
        {
            from, to,
            paidCents = Sum(paid, TitleKind.Payable, true),
            receivedCents = Sum(paid, TitleKind.Receivable, true),
            openPayableCents = Sum(inPeriod, TitleKind.Payable, false),
            openReceivableCents = Sum(inPeriod, TitleKind.Receivable, false),
            overduePayableCents = Sum(open.Where(t => t.DueDate < today), TitleKind.Payable, false),
            overdueReceivableCents = Sum(open.Where(t => t.DueDate < today), TitleKind.Receivable, false),
            overdueCount = open.Count(t => t.DueDate < today),
            expensesByCategory = paid.Where(t => t.Kind == TitleKind.Payable && t.Category != FinanceCategories.Merchandise)
                .GroupBy(t => t.Category).Select(g => new { category = g.Key, cents = g.Sum(t => t.PaidAmountCents ?? 0) }).OrderByDescending(g => g.cents),
            days,
            note = "Vendas do PDV entram como faturamento nos relatórios de vendas; aqui ficam as contas lançadas no financeiro.",
        };
    }

    /// <summary>Despesas pagas no período (exceto compra de mercadoria, que já está no custo dos produtos).</summary>
    public Task<long> OperatingExpensesAsync(DateOnly from, DateOnly to) =>
        db.FinancialTitles.AsNoTracking()
            .Where(t => t.Kind == TitleKind.Payable && t.Status == TitleStatus.Paid && t.Category != FinanceCategories.Merchandise && t.PaidOn >= from && t.PaidOn <= to)
            .SumAsync(t => t.PaidAmountCents ?? 0);
}

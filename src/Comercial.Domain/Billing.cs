namespace Comercial.Domain;

public enum BillingPeriod { Monthly = 1, Yearly = 12 }

public class SubscriptionPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public long PriceCents { get; set; }
    public string Currency { get; set; } = "BRL";
    public BillingPeriod Period { get; set; } = BillingPeriod.Monthly;
    public int MaxUsers { get; set; } = 5;
    public int MaxDevices { get; set; } = 2;
    public int MaxStores { get; set; } = 1;
    public int GraceDays { get; set; } = 7;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset ValidFrom { get; set; } = DateTimeOffset.UtcNow;
    public List<PlanModule> Modules { get; set; } = [];
}

public class PlanModule
{
    public Guid PlanId { get; set; }
    public string ModuleKey { get; set; } = "";
}

public enum SubscriptionStatus { Active = 1, Suspended = 2, Cancelled = 3 }

/// <summary>
/// Assinatura de uma empresa. Preço e limites são copiados do plano no momento da contratação
/// (snapshot) — alterar o plano não altera contratos existentes retroativamente.
/// </summary>
public class Subscription : TenantEntity
{
    public Guid PlanId { get; set; }
    public string PlanCode { get; set; } = "";
    public long PriceCents { get; set; }
    public BillingPeriod Period { get; set; }
    public int MaxUsers { get; set; }
    public int MaxDevices { get; set; }
    public int MaxStores { get; set; }
    public int GraceDays { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CurrentPeriodEnd { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}

public enum ModuleSource { Plan = 1, AddOn = 2, Manual = 3 }

public class TenantModule : ITenantScoped
{
    public Guid TenantId { get; set; }
    public string ModuleKey { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ModuleSource Source { get; set; } = ModuleSource.Plan;
    public DateTimeOffset? ValidUntil { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Reason { get; set; }
}

public enum InvoiceStatus { Open = 1, Paid = 2, Void = 3 }

public class Invoice : TenantEntity
{
    public Guid SubscriptionId { get; set; }
    public long AmountCents { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Open;
    public DateTimeOffset? PaidAt { get; set; }
    public string? ProviderReference { get; set; }
}

/// <summary>Evento recebido do provedor de pagamentos. ProviderEventId único garante idempotência.</summary>
public class PaymentEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = "";
    public string ProviderEventId { get; set; } = "";
    public string Type { get; set; } = "";
    public Guid? TenantId { get; set; }
    public Guid? InvoiceId { get; set; }
    public long AmountCents { get; set; }
    public string PayloadSha256 { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Result { get; set; } = "";
}

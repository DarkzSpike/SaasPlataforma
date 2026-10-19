namespace Comercial.Domain;

// ===================== Fornecedores e compras (módulos 6 e 7) =====================

public class Supplier : TenantEntity
{
    public string Name { get; set; } = "";
    public string? Document { get; set; }
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public int LeadTimeDays { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum PurchaseStatus { Draft = 1, Approved = 2, PartiallyReceived = 3, Received = 4, Cancelled = 5 }

public class PurchaseOrder : TenantEntity
{
    public int Number { get; set; }
    public Guid SupplierId { get; set; }
    public Guid StoreId { get; set; }
    public PurchaseStatus Status { get; set; } = PurchaseStatus.Draft;
    public string? Notes { get; set; }
    public DateOnly? ExpectedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? CancelReason { get; set; }
    public List<PurchaseItem> Items { get; set; } = [];
}

public class PurchaseItem : TenantEntity
{
    public Guid PurchaseOrderId { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public decimal QuantityOrdered { get; set; }
    public decimal QuantityReceived { get; set; }
    public long UnitCostCents { get; set; }
}

/// <summary>Cada recebimento (total ou parcial) fica registrado, inclusive divergências.</summary>
public class PurchaseReceipt : TenantEntity
{
    public Guid PurchaseOrderId { get; set; }
    public Guid UserId { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? Notes { get; set; }
    public string? DivergenceJson { get; set; }
    public long TotalCents { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ===================== Clientes e fidelidade (módulos 13 e 14) =====================

public class Customer : TenantEntity
{
    public string Name { get; set; } = "";
    /// <summary>CPF/CNPJ (opcional). Único por empresa quando informado.</summary>
    public string? Document { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public DateOnly? BirthDate { get; set; }
    public string? Notes { get; set; }
    /// <summary>Consentimento para comunicações de marketing (LGPD): registrado com data.</summary>
    public bool MarketingConsent { get; set; }
    /// <summary>Limite para vender no fiado (crediário). 0 = não pode comprar fiado.</summary>
    public long CreditLimitCents { get; set; }
    public DateTimeOffset? ConsentAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Dados pessoais removidos a pedido do titular; vendas continuam (sem identificação).</summary>
    public DateTimeOffset? AnonymizedAt { get; set; }
}

/// <summary>Regras do programa de fidelidade da empresa (uma linha por empresa).</summary>
public class LoyaltyProgram : ITenantScoped
{
    public Guid TenantId { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Pontos ganhos por R$ 1,00 gasto.</summary>
    public int PointsPerReal { get; set; } = 1;
    /// <summary>Valor de desconto (centavos) de cada ponto no resgate.</summary>
    public int CentsPerPoint { get; set; } = 5;
    public int MinPointsToRedeem { get; set; } = 100;
    /// <summary>Validade dos pontos em dias (0 = não expiram).</summary>
    public int ExpirationDays { get; set; } = 365;
    /// <summary>Percentual máximo da venda que pode ser pago com pontos.</summary>
    public int MaxRedeemPercent { get; set; } = 50;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum LoyaltyType { Earn = 1, Redeem = 2, EarnReversal = 3, RedeemRefund = 4, Adjust = 5 }

/// <summary>Extrato imutável de pontos. Chave única impede crédito/débito em dobro da mesma venda.</summary>
public class LoyaltyTransaction : TenantEntity
{
    public Guid CustomerId { get; set; }
    public Guid? SaleId { get; set; }
    public LoyaltyType Type { get; set; }
    public int Points { get; set; }
    public string UniqueKey { get; set; } = "";
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
}

// ===================== Financeiro (módulos 9, 10 e 11) =====================

public enum TitleKind { Payable = 1, Receivable = 2 }
public enum TitleStatus { Open = 1, Paid = 2, Cancelled = 3 }

/// <summary>Conta a pagar ou a receber (inclui despesas). Parcelas compartilham o mesmo GroupId.</summary>
public class FinancialTitle : TenantEntity
{
    public TitleKind Kind { get; set; }
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string? CostCenter { get; set; }
    public string? Counterparty { get; set; }
    public Guid? SupplierId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? PurchaseOrderId { get; set; }
    /// <summary>Venda que gerou a conta (fiado).</summary>
    public Guid? SaleId { get; set; }
    public DateOnly DueDate { get; set; }
    public long AmountCents { get; set; }
    public TitleStatus Status { get; set; } = TitleStatus.Open;
    public DateOnly? PaidOn { get; set; }
    public long? PaidAmountCents { get; set; }
    public string? PaymentMethod { get; set; }
    public Guid GroupId { get; set; }
    public int InstallmentNumber { get; set; } = 1;
    public int InstallmentCount { get; set; } = 1;
    public string? Notes { get; set; }
    public string? CancelReason { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ===================== Trocas e devoluções (módulo 22) =====================

public class SaleReturn : TenantEntity
{
    public Guid SaleId { get; set; }
    public Guid UserId { get; set; }
    public string Reason { get; set; } = "";
    public bool Restock { get; set; }
    public string RefundMethod { get; set; } = "cash";
    public long RefundCents { get; set; }
    public long CostCents { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SaleReturnItem> Items { get; set; } = [];
}

public class SaleReturnItem : TenantEntity
{
    public Guid SaleReturnId { get; set; }
    public Guid SaleItemId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public long RefundCents { get; set; }
}

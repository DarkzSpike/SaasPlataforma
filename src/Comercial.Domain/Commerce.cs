namespace Comercial.Domain;

// Valores monetários em centavos (long) para evitar erros de arredondamento.
// Quantidades em decimal com 3 casas (produtos por peso).

public class Category : TenantEntity
{
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }
}

public class Product : TenantEntity
{
    public string Sku { get; set; } = "";
    public string? Barcode { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid? CategoryId { get; set; }
    public string Unit { get; set; } = "UN";
    public long PriceCents { get; set; }
    public long CostCents { get; set; }
    public decimal MinStock { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Controle de concorrência otimista (incrementado a cada alteração).</summary>
    public int Version { get; set; } = 1;
    /// <summary>Chave da foto no armazenamento configurado (local ou Google Drive). Nunca é uma URL pública.</summary>
    public string? ImageKey { get; set; }
    public string? ImageContentType { get; set; }
    public DateTimeOffset? ImageUpdatedAt { get; set; }
}

public enum StockMovementType { Entry = 1, Sale = 2, Adjustment = 3, SaleCancellation = 4, TransferIn = 5, TransferOut = 6 }

/// <summary>Movimentação imutável. Correções geram nova movimentação, nunca edição.</summary>
public class StockMovement : TenantEntity
{
    public Guid StoreId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public StockMovementType Type { get; set; }
    public string? Reason { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ReferenceId { get; set; }
    public long UnitCostCents { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class StockBalance : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Version { get; set; } = 1;
}

public enum SaleStatus { Completed = 1, Cancelled = 2 }

public class Sale : TenantEntity
{
    public Guid StoreId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid CashSessionId { get; set; }
    public Guid OperatorUserId { get; set; }
    /// <summary>Chave gerada pelo cliente; única por empresa. Impede venda duplicada em reenvio.</summary>
    public string IdempotencyKey { get; set; } = "";
    public SaleStatus Status { get; set; } = SaleStatus.Completed;
    public long SubtotalCents { get; set; }
    public long DiscountCents { get; set; }
    public long TotalCents { get; set; }
    public long ChangeCents { get; set; }
    public DateTimeOffset LocalCreatedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool CreatedOffline { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public string? CancelReason { get; set; }
    public List<SaleItem> Items { get; set; } = [];
    public List<SalePayment> Payments { get; set; } = [];
}

public class SaleItem : TenantEntity
{
    public Guid SaleId { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public decimal Quantity { get; set; }
    /// <summary>Preço vindo do cadastro do servidor — o preço enviado pelo cliente é ignorado.</summary>
    public long UnitPriceCents { get; set; }
    /// <summary>Custo histórico no momento da venda, para margem.</summary>
    public long UnitCostCents { get; set; }
    public long DiscountCents { get; set; }
    public long TotalCents { get; set; }
}

public enum PaymentMethod { Cash = 1, DebitCard = 2, CreditCard = 3, Pix = 4, Other = 9 }

public class SalePayment : TenantEntity
{
    public Guid SaleId { get; set; }
    public PaymentMethod Method { get; set; }
    public long AmountCents { get; set; }
}

public enum CashSessionStatus { Open = 1, Closed = 2 }

public class CashSession : TenantEntity
{
    public Guid StoreId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid OperatorUserId { get; set; }
    public CashSessionStatus Status { get; set; } = CashSessionStatus.Open;
    public long OpeningCents { get; set; }
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }
    public long? ExpectedCashCents { get; set; }
    public long? DeclaredCashCents { get; set; }
    public long? DifferenceCents { get; set; }
    public string? Justification { get; set; }
    public Guid? ReviewedByUserId { get; set; }
}

public enum CashMovementType { Opening = 1, Supply = 2, Withdrawal = 3, CashSale = 4, SaleCancellation = 5 }

public class CashMovement : TenantEntity
{
    public Guid CashSessionId { get; set; }
    public CashMovementType Type { get; set; }
    /// <summary>Positivo = entra na gaveta; negativo = sai.</summary>
    public long AmountCents { get; set; }
    public string? Reason { get; set; }
    public Guid UserId { get; set; }
    public Guid? ReferenceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class StoreHours : TenantEntity
{
    public Guid StoreId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly Opens { get; set; }
    public TimeOnly Closes { get; set; }
}

public enum OperatingEventType { Open = 1, Close = 2, Correction = 3 }

/// <summary>
/// Evento operacional da loja (abertura/fechamento), separado das sessões de caixa.
/// Imutável: correções criam um novo evento que referencia o original.
/// </summary>
public class OperatingEvent : TenantEntity
{
    public Guid StoreId { get; set; }
    public OperatingEventType Type { get; set; }
    public DateTimeOffset LocalTime { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid UserId { get; set; }
    public Guid? DeviceId { get; set; }
    public bool Offline { get; set; }
    public bool ClockSuspect { get; set; }
    public Guid? CorrectsEventId { get; set; }
    public OperatingEventType? CorrectedType { get; set; }
    public DateTimeOffset? CorrectedLocalTime { get; set; }
    public string? Justification { get; set; }
    public string IdempotencyKey { get; set; } = "";
}

public enum FiscalModel { NFCe = 65, NFe = 55 }

public enum FiscalStatus
{
    /// <summary>Registrado, aguardando transmissão. NÃO é um documento autorizado.</summary>
    PendingTransmission = 1,
    Authorized = 2,
    Rejected = 3,
    /// <summary>Emitido em contingência legal; precisa ser transmitido depois.</summary>
    Contingency = 4,
    Cancelled = 5
}

public class FiscalDocument : TenantEntity
{
    public Guid SaleId { get; set; }
    public FiscalModel Model { get; set; } = FiscalModel.NFCe;
    public FiscalStatus Status { get; set; } = FiscalStatus.PendingTransmission;
    public string? AccessKey { get; set; }
    /// <summary>Protocolo de autorização — preenchido SOMENTE com resposta real da SEFAZ via provedor.</summary>
    public string? Protocol { get; set; }
    public string? ProviderName { get; set; }
    public string? LastMessage { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AuthorizedAt { get; set; }
}

public class FiscalEvent : TenantEntity
{
    public Guid FiscalDocumentId { get; set; }
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Registro de cada operação sincronizada (idempotência por dispositivo + chave).</summary>
public class SyncEvent : TenantEntity
{
    public Guid DeviceId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTimeOffset LocalTime { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "";
    public string? ResultJson { get; set; }
}

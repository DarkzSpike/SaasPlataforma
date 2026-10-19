namespace Comercial.Domain;

/// <summary>Lote com validade. Entradas criam lotes; vendas consomem os que vencem primeiro (FEFO).</summary>
public class ProductBatch : TenantEntity
{
    public Guid ProductId { get; set; }
    public Guid StoreId { get; set; }
    public string BatchCode { get; set; } = "";
    public DateOnly? ExpiresOn { get; set; }
    public decimal QuantityReceived { get; set; }
    public decimal QuantityRemaining { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AlertedAt { get; set; }
}

/// <summary>Transferência de mercadoria entre lojas da mesma empresa (saída numa, entrada na outra, na mesma transação).</summary>
public class StockTransfer : TenantEntity
{
    public int Number { get; set; }
    public Guid FromStoreId { get; set; }
    public Guid ToStoreId { get; set; }
    public string? Notes { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<StockTransferItem> Items { get; set; } = [];
}

public class StockTransferItem : TenantEntity
{
    public Guid TransferId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
}

/// <summary>NF-e de compra importada pelo XML (a chave de acesso impede importar a mesma nota duas vezes).</summary>
public class ImportedInvoice : TenantEntity
{
    public string AccessKey { get; set; } = "";
    public string Number { get; set; } = "";
    public Guid? SupplierId { get; set; }
    public string SupplierName { get; set; } = "";
    public Guid StoreId { get; set; }
    public long TotalCents { get; set; }
    public int Items { get; set; }
    public Guid ImportedByUserId { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace Comercial.Domain;

/// <summary>Códigos de barras adicionais do produto (ex.: caixa e unidade, código antigo do fornecedor).</summary>
public class ProductBarcode : TenantEntity
{
    public Guid ProductId { get; set; }
    public string Code { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Componente de um kit/combo: vender o kit baixa o estoque dos componentes.</summary>
public class ProductComponent : TenantEntity
{
    public Guid KitProductId { get; set; }
    public Guid ComponentProductId { get; set; }
    public decimal Quantity { get; set; }
}

public class Promotion : TenantEntity
{
    public string Name { get; set; } = "";
    public int Type { get; set; }
    public Guid? ProductId { get; set; }
    public int BuyQty { get; set; }
    public int PayQty { get; set; }
    public decimal MinQty { get; set; }
    public long UnitPriceCents { get; set; }
    public decimal Percent { get; set; }
    public string? CouponCode { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum QuoteStatus { Open = 1, Converted = 2, Cancelled = 3 }

/// <summary>Orçamento: lista de itens com preço da época, validade e cliente; vira venda no PDV.</summary>
public class Quote : TenantEntity
{
    public int Number { get; set; }
    public Guid? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? Notes { get; set; }
    public long TotalCents { get; set; }
    public DateOnly ValidUntil { get; set; }
    public QuoteStatus Status { get; set; } = QuoteStatus.Open;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? SaleId { get; set; }
    public List<QuoteItem> Items { get; set; } = [];
}

public class QuoteItem : TenantEntity
{
    public Guid QuoteId { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public decimal Quantity { get; set; }
    public long UnitPriceCents { get; set; }
}

namespace Comercial.Domain;

/// <summary>Conta de recebimento da PRÓPRIA loja (Mercado Pago dela). A credencial fica criptografada e nunca é devolvida.</summary>
public class TenantPaymentSettings : ITenantScoped
{
    public Guid TenantId { get; set; }
    public string? MpAccessTokenProtected { get; set; }
    public string? MpAccountLabel { get; set; }
    public bool PixEnabled { get; set; }
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}

public static class PixChargeStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
}

/// <summary>Cobrança PIX gerada no caixa. Só vira pagamento de venda depois que o Mercado Pago confirma; vale para UMA venda.</summary>
public class PosPixCharge : TenantEntity
{
    public Guid CashSessionId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public long AmountCents { get; set; }
    public string MpPaymentId { get; set; } = "";
    public string QrCode { get; set; } = "";
    public string QrBase64 { get; set; } = "";
    public string Status { get; set; } = PixChargeStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? UsedBySaleId { get; set; }
}

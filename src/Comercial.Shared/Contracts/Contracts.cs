using System.Text.Json;

namespace Comercial.Shared.Contracts;

// Contratos compartilhados entre API, cliente Windows e testes.
// Note que NENHUM contrato da API de loja aceita TenantId: o servidor o deriva do token.

public sealed record LoginRequest(string TenantCode, string Email, string Password, Guid? DeviceId, string? TotpCode = null);
public sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresInSeconds);
public sealed record RefreshRequest(string RefreshToken);

public sealed record SaleItemRequest(Guid ProductId, decimal Quantity, long DiscountCents = 0, long? ClientUnitPriceCents = null);
public sealed record SalePaymentRequest(int Method, long AmountCents);

public sealed record CreateSaleRequest(
    string IdempotencyKey,
    Guid StoreId,
    Guid CashSessionId,
    DateTimeOffset LocalCreatedAt,
    List<SaleItemRequest> Items,
    List<SalePaymentRequest> Payments,
    long SaleDiscountCents = 0,
    Guid? DeviceId = null,
    bool CreatedOffline = false,
    Guid? CustomerId = null,
    int RedeemPoints = 0,
    Guid? DiscountAuthorizationId = null,
    Guid? PixChargeId = null,
    string? CouponCode = null,
    Guid? QuoteId = null);

public sealed record SaleResponse(Guid Id, string IdempotencyKey, string Status, long SubtotalCents, long DiscountCents, long TotalCents, long ChangeCents, bool Duplicate,
    long LoyaltyDiscountCents = 0, int PointsEarned = 0);

public sealed record OperatingEventRequest(string IdempotencyKey, Guid StoreId, int Type, DateTimeOffset LocalTime, bool Offline = false, Guid? DeviceId = null);

public sealed record SyncOperation(string IdempotencyKey, string Type, DateTimeOffset LocalTime, JsonElement Payload);
public sealed record SyncBatchRequest(Guid DeviceId, DateTimeOffset DeviceClock, List<SyncOperation> Operations);
public sealed record SyncOperationResult(string IdempotencyKey, string Status, string? Error, JsonElement? Result);
public sealed record SyncBatchResponse(DateTimeOffset ServerTime, bool ClockSkewDetected, List<SyncOperationResult> Results);

public static class SyncTypes
{
    public const string SaleCreate = "sale.create";
    public const string OperatingEvent = "operating.event";
}

public static class SyncStatus
{
    public const string Applied = "applied";
    public const string Duplicate = "duplicate";
    public const string Rejected = "rejected";
}

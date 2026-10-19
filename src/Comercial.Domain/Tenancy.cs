namespace Comercial.Domain;

public enum TenantStatus { Active = 1, Suspended = 2, Cancelled = 3 }

public class Tenant
{
    /// <summary>
    /// Consentimento do proprietário: operações que o suporte pode fazer sem pedir aprovação a cada sessão
    /// (lista separada por vírgula; vazio = toda alteração exige aprovação).
    /// </summary>
    public string? SupportPreAuthorizedOperations { get; set; }
    public DateTimeOffset? SupportPreAuthorizedAt { get; set; }
    public Guid? SupportPreAuthorizedByUserId { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Código interno legível, usado para login e confirmação de operações críticas.</summary>
    public string Code { get; set; } = "";
    public string LegalName { get; set; } = "";
    public string TradeName { get; set; } = "";
    public string? Document { get; set; }
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public string? SuspensionReason { get; set; }
    /// <summary>Desconto máximo (%) que um operador pode aplicar sem permissão de gerente.</summary>
    public decimal MaxOperatorDiscountPercent { get; set; } = 5m;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Store : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string TimeZoneId { get; set; } = "America/Sao_Paulo";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class User : TenantEntity
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    /// <summary>Nulo = acesso a todas as lojas da empresa.</summary>
    public Guid? StoreId { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    /// <summary>Comissão sobre as vendas do funcionário (%). 0 = sem comissão.</summary>
    public decimal CommissionPercent { get; set; }
    /// <summary>Verificação em duas etapas (app autenticador). Segredo criptografado.</summary>
    public bool TotpEnabled { get; set; }
    public string? TotpSecretProtected { get; set; }
    public long? TotpLastStep { get; set; }
    public List<UserRole> Roles { get; set; } = [];
}

public class UserRole : TenantEntity
{
    public Guid UserId { get; set; }
    public string Role { get; set; } = "";
}

/// <summary>Permissão adicional (ou negada) individualmente, além do papel.</summary>
public class UserPermissionGrant : TenantEntity
{
    public Guid UserId { get; set; }
    public string Permission { get; set; } = "";
    public bool Granted { get; set; } = true;
}

/// <summary>Sessão de login com refresh token rotativo (família para detectar reuso).</summary>
public class UserSession : TenantEntity
{
    public Guid UserId { get; set; }
    public Guid FamilyId { get; set; }
    public string RefreshTokenHash { get; set; } = "";
    public Guid? DeviceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public Guid? ReplacedBySessionId { get; set; }
}

public enum DeviceStatus { Active = 1, Revoked = 2 }

public class Device : TenantEntity
{
    public Guid? StoreId { get; set; }
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "windows";
    public DeviceStatus Status { get; set; } = DeviceStatus.Active;
    public string? AppVersion { get; set; }
    public DateTimeOffset ActivatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? LastError { get; set; }
}

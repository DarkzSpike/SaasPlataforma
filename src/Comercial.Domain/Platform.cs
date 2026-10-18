namespace Comercial.Domain;

/// <summary>Membro da equipe do SaaS. Não pertence a nenhum tenant.</summary>
public class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    /// <summary>Segredo TOTP protegido pela Data Protection API do servidor.</summary>
    public string TotpSecretProtected { get; set; } = "";
    public string Role { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public long LastTotpStep { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum SupportMode { ReadOnly = 1, Elevated = 2 }

public class SupportSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AdminUserId { get; set; }
    public string Reason { get; set; } = "";
    public SupportMode Mode { get; set; } = SupportMode.ReadOnly;
    /// <summary>Operações liberadas na elevação (lista separada por vírgula de permissões).</summary>
    public string AllowedOperations { get; set; } = "";
    public string? ElevationReason { get; set; }
    public Guid? ApprovedByTenantUserId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ElevatedUntil { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndedReason { get; set; }

    public bool IsActive(DateTimeOffset now) => EndedAt is null && now < ExpiresAt;
    public bool IsElevated(DateTimeOffset now) => IsActive(now) && Mode == SupportMode.Elevated && ElevatedUntil > now;
}

/// <summary>Pedido de elevação aguardando aprovação do proprietário da empresa.</summary>
public class SupportElevationRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SupportSessionId { get; set; }
    public Guid TenantId { get; set; }
    public string Reason { get; set; } = "";
    public string RequestedOperations { get; set; } = "";
    public int DurationMinutes { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
    public bool? Approved { get; set; }
    public Guid? DecidedByUserId { get; set; }
}

public enum ActorType { System = 0, User = 1, Admin = 2, Support = 3, Device = 4, Webhook = 5 }

/// <summary>
/// Registro de auditoria encadeado por hash (cada registro inclui o hash do anterior).
/// O DbContext recusa UPDATE/DELETE; no PostgreSQL um trigger reforça a regra.
/// </summary>
public class AuditLog
{
    public long Id { get; set; }
    public Guid? TenantId { get; set; }
    public ActorType ActorType { get; set; }
    public Guid? ActorId { get; set; }
    public Guid? SupportSessionId { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? DataJson { get; set; }
    public string? Outcome { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string PreviousHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

public static class PushStatus
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    /// <summary>Nenhum provedor de push configurado (ex.: Firebase ainda não cadastrado): fica visível só no app.</summary>
    public const string NoProvider = "no_provider";
    /// <summary>Nenhum aparelho do destinatário registrado para push.</summary>
    public const string NoDevice = "no_device";
    public const string Failed = "failed";
}

/// <summary>Aviso para o dono da empresa (ex.: quebra de caixa). Exibido no app e enviado por push quando possível.</summary>
public class Notification : TenantEntity
{
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? DataJson { get; set; }
    /// <summary>Papel destinatário (por padrão o proprietário).</summary>
    public string TargetRole { get; set; } = "owner";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
    public string PushStatus { get; set; } = Domain.PushStatus.Pending;
    public int PushAttempts { get; set; }
    public DateTimeOffset? PushedAt { get; set; }
    public string? LastPushError { get; set; }
}

/// <summary>Token de push de um aparelho (FCM; no iOS o FCM entrega via APNs).</summary>
public class PushToken : TenantEntity
{
    public Guid UserId { get; set; }
    public string Token { get; set; } = "";
    public string Platform { get; set; } = "android";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

public class TenantBranding : ITenantScoped
{
    public Guid TenantId { get; set; }
    public string DraftJson { get; set; } = "";
    public int PublishedVersion { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class BrandingVersion : TenantEntity
{
    public int Version { get; set; }
    public string ConfigJson { get; set; } = "";
    public Guid PublishedByAdminId { get; set; }
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
    public int? RestoredFromVersion { get; set; }
    public string Note { get; set; } = "";
}

public class BrandingAsset : TenantEntity
{
    public string Kind { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Nome de arquivo gerado pelo servidor (nunca o nome enviado).</summary>
    public string StorageName { get; set; } = "";
    public Guid UploadedByAdminId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

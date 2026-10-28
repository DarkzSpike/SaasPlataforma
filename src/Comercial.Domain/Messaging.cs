namespace Comercial.Domain;

/// <summary>Fila de e-mails: gravada junto com a operação de origem e enviada por um serviço em segundo plano (com novas tentativas).</summary>
public class EmailMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    /// <summary>Categoria (password_reset, welcome, invoice...) para auditoria e diagnóstico.</summary>
    public string Kind { get; set; } = "";
    public string Status { get; set; } = EmailStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    /// <summary>Anexo opcional: arquivo gerado pelo servidor (ex.: pacote do contador). Caminho nunca vem do cliente.</summary>
    public string? AttachmentPath { get; set; }
    public string? AttachmentName { get; set; }
}

public static class EmailStatus
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
}

/// <summary>Código de redefinição de senha: só o hash é guardado; uso único, com prazo e limite de tentativas.</summary>
public class PasswordResetToken : TenantEntity
{
    public Guid UserId { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>Cadastro aguardando confirmação do e-mail (a empresa só é criada depois do código).</summary>
public class PendingSignup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string TenantCode { get; set; } = "";
    public string PayloadProtected { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public int Attempts { get; set; }
    public string? Ip { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

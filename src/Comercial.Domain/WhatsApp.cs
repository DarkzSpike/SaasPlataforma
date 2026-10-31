namespace Comercial.Domain;

/// <summary>
/// WhatsApp da PRÓPRIA loja pela API oficial (WhatsApp Business Platform / Cloud API da Meta). O token fica
/// criptografado e nunca é devolvido. Mensagens iniciadas pela loja usam modelos aprovados pela Meta.
/// </summary>
public class TenantWhatsAppSettings : ITenantScoped
{
    public Guid TenantId { get; set; }
    /// <summary>"Identificação do número de telefone" (Phone number ID) no painel da Meta.</summary>
    public string? PhoneNumberId { get; set; }
    public string? AccessTokenProtected { get; set; }
    /// <summary>Número e nome verificados, devolvidos pela Meta ao conectar (só para exibir).</summary>
    public string? DisplayPhone { get; set; }
    public string? VerifiedName { get; set; }
    /// <summary>Chave secreta do app da Meta (assina os avisos recebidos, ex.: cliente respondeu SAIR).</summary>
    public string? AppSecretProtected { get; set; }
    /// <summary>Código que a Meta devolve ao cadastrar o endereço de avisos (gerado pelo sistema).</summary>
    public string WebhookVerifyToken { get; set; } = "";
    public string ChargeTemplate { get; set; } = "cobranca_fiado";
    public string PromoTemplate { get; set; } = "promocao_loja";
    public string Language { get; set; } = "pt_BR";
    /// <summary>Cobrança automática do fiado no dia escolhido para cada cliente.</summary>
    public bool ChargesEnabled { get; set; }
    /// <summary>Campanhas de promoção (só para quem aceitou receber ofertas).</summary>
    public bool PromotionsEnabled { get; set; }
    /// <summary>Máximo de promoções enviadas por dia (protege o número contra bloqueio por excesso).</summary>
    public int DailyPromoLimit { get; set; } = 200;
    /// <summary>Cada cliente recebe no máximo uma promoção a cada N dias.</summary>
    public int PromoIntervalDays { get; set; } = 7;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}

public static class WhatsAppKinds
{
    public const string Charge = "charge";
    public const string Promo = "promo";
    public const string Test = "test";
    /// <summary>Status de mensagem mandada à mão pelo WhatsApp da loja (modo grátis).</summary>
    public const string ManualStatus = "manual";
}

/// <summary>Registro de cada mensagem enviada (ou recusada): controle de limite, de repetição e auditoria.</summary>
public class WhatsAppMessage : TenantEntity
{
    public Guid? CustomerId { get; set; }
    public Guid? CampaignId { get; set; }
    public string Kind { get; set; } = WhatsAppKinds.Charge;
    public string Phone { get; set; } = "";
    /// <summary>sent | failed | manual (aberta no WhatsApp da loja pelo dono, envio grátis)</summary>
    public string Status { get; set; } = "sent";
    public string? ProviderMessageId { get; set; }
    public string? Error { get; set; }
    /// <summary>Texto enviado (preenchido nas variáveis do modelo), para o dono conferir depois.</summary>
    public string Preview { get; set; } = "";
    public long AmountCents { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class CampaignStatus
{
    public const string Scheduled = "scheduled";
    public const string Sending = "sending";
    public const string Done = "done";
    public const string Cancelled = "cancelled";
    /// <summary>Parada automática (muitas falhas) para proteger o número.</summary>
    public const string Stopped = "stopped";
}

/// <summary>Promoção em massa pelo WhatsApp, enviada aos poucos e dentro dos limites anti-spam.</summary>
public class WhatsAppCampaign : TenantEntity
{
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset ScheduledAt { get; set; }
    public string Status { get; set; } = CampaignStatus.Scheduled;
    /// <summary>
    /// Grátis: o sistema não envia nada sozinho. Na hora marcada o dono recebe o aviso e manda uma por uma pelo
    /// WhatsApp do celular/computador, com a mensagem pronta. Falso = envio automático pela API oficial (pago).
    /// </summary>
    public bool Manual { get; set; }
    public int Sent { get; set; }
    public int Failed { get; set; }
    public string? StopReason { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}

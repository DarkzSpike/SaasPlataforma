namespace Comercial.Domain;

/// <summary>Tipos de arquivo fiscal guardados no arquivo da empresa.</summary>
public static class FiscalXmlKind
{
    /// <summary>NFC-e autorizada com o protocolo da SEFAZ (nfeProc).</summary>
    public const string Authorization = "autorizacao";
    /// <summary>Evento de cancelamento homologado (procEventoNFe, tpEvento 110111).</summary>
    public const string Cancellation = "cancelamento";
    /// <summary>Inutilização de numeração homologada (procInutNFe).</summary>
    public const string NumberVoid = "inutilizacao";
}

/// <summary>
/// XML fiscal ORIGINAL, como veio do provedor fiscal (com o protocolo da SEFAZ). Imutável: nunca é alterado nem apagado
/// pela aplicação; o SHA-256 permite conferir a integridade em qualquer exportação.
/// </summary>
public class FiscalXmlFile : TenantEntity
{
    public Guid? FiscalDocumentId { get; set; }
    public Guid? NumberVoidId { get; set; }
    public string Kind { get; set; } = "";
    /// <summary>Modelo fiscal (65 = NFC-e, 55 = NF-e).</summary>
    public int Model { get; set; } = 65;
    public string? AccessKey { get; set; }
    public byte[] Content { get; set; } = [];
    public string Sha256 { get; set; } = "";
    public int SizeBytes { get; set; }
    /// <summary>Data que define o mês do arquivo (emissão da nota, data do evento ou da inutilização).</summary>
    public DateTimeOffset ReferenceAt { get; set; }
    public string Source { get; set; } = "";
    public DateTimeOffset StoredAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Pacote de exportação para a contabilidade (ZIP guardado em disco; o registro guarda o que entrou e o hash).</summary>
public class FiscalExport : TenantEntity
{
    public int Year { get; set; }
    public int Month { get; set; }
    public Guid? StoreId { get; set; }
    /// <summary>Quem gerou (null = envio automático).</summary>
    public Guid? CreatedByUserId { get; set; }
    public string Trigger { get; set; } = "manual";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string FileName { get; set; } = "";
    public string StorageName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>Hash do conjunto de XMLs incluídos: pacote igual ao anterior é reaproveitado (sem duplicar).</summary>
    public string ContentHash { get; set; } = "";
    public int DocumentCount { get; set; }
    public int EventCount { get; set; }
    public int VoidCount { get; set; }
    public int ReportCount { get; set; }
    public int MissingCount { get; set; }
    /// <summary>Lista dos arquivos incluídos (caminho no ZIP, id de origem e SHA-256), em JSON.</summary>
    public string ManifestJson { get; set; } = "[]";
    public string Summary { get; set; } = "";
}

public static class ClosingStatus
{
    public const string Open = "aberto";
    public const string Closed = "concluido";
}

/// <summary>Fechamento fiscal mensal: guarda as verificações, observações e quem concluiu. Não altera documentos fiscais.</summary>
public class FiscalMonthClosing : TenantEntity
{
    public int Year { get; set; }
    public int Month { get; set; }
    public string Status { get; set; } = ClosingStatus.Open;
    public string ChecksJson { get; set; } = "[]";
    public string? Notes { get; set; }
    public DateTimeOffset LastCheckedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ClosedByUserId { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? LastExportId { get; set; }
}

/// <summary>Escritório de contabilidade da empresa e regras de envio.</summary>
public class AccountantSettings : ITenantScoped
{
    public Guid TenantId { get; set; }
    public string OfficeName { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>E-mails adicionais autorizados, separados por vírgula.</summary>
    public string ExtraEmails { get; set; } = "";
    public bool AutoSend { get; set; }
    /// <summary>Dia do mês (1–28) em que o pacote do mês anterior é enviado.</summary>
    public int DayOfMonth { get; set; } = 5;
    public int Hour { get; set; } = 8;
    public bool IncludeXml { get; set; } = true;
    public bool IncludeReports { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class DeliveryStatus
{
    /// <summary>Na fila de e-mail.</summary>
    public const string Queued = "na_fila";
    /// <summary>O servidor de e-mail aceitou a mensagem (entrega na caixa do contador não é confirmável por SMTP).</summary>
    public const string Accepted = "aceito_pelo_servidor";
    public const string Failed = "falhou";
    /// <summary>Envio automático suspenso porque as verificações do fechamento encontraram pendências.</summary>
    public const string Blocked = "bloqueado";
}

/// <summary>Envio de um pacote ao contador (manual, automático ou reenvio do mesmo pacote).</summary>
public class AccountantDelivery : TenantEntity
{
    public Guid? FiscalExportId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public string Trigger { get; set; } = "manual";
    public string Recipients { get; set; } = "";
    public string Status { get; set; } = DeliveryStatus.Queued;
    public string? Detail { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Ids das mensagens na fila de e-mail, separados por vírgula.</summary>
    public string EmailMessageIds { get; set; } = "";
}

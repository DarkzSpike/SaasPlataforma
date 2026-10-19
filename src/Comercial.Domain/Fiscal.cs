namespace Comercial.Domain;

/// <summary>Regime tributário do emitente (CRT da NF-e).</summary>
public enum TaxRegime { SimplesNacional = 1, SimplesExcessoSublimite = 2, RegimeNormal = 3, Mei = 4 }

/// <summary>
/// Perfil fiscal da loja (emitente da NFC-e). O certificado A1 fica com o provedor fiscal (ex.: Focus NFe);
/// aqui guardamos só o token de acesso ao provedor, criptografado.
/// Padrões tributários usados quando o produto não tem valor próprio.
/// </summary>
public class TenantFiscalProfile : ITenantScoped
{
    public Guid TenantId { get; set; }
    public string Cnpj { get; set; } = "";
    public string? StateRegistration { get; set; }
    public TaxRegime Regime { get; set; } = TaxRegime.SimplesNacional;
    public string Uf { get; set; } = "";
    /// <summary>1 = produção, 2 = homologação (testes, sem valor fiscal).</summary>
    public int Environment { get; set; } = 2;
    public string Provider { get; set; } = "none";
    public string? ProviderTokenProtected { get; set; }
    public int NfceSeries { get; set; } = 1;
    public bool AutoEmit { get; set; } = true;
    // Padrões de tributação (Simples Nacional: CSOSN 102, PIS/COFINS 49).
    public string DefaultCfop { get; set; } = "5102";
    public string DefaultIcmsCst { get; set; } = "102";
    public string DefaultPisCst { get; set; } = "49";
    public string DefaultCofinsCst { get; set; } = "49";
    // Reforma tributária (IBS/CBS): CST e classificação tributária; alíquotas de teste de 2026 por padrão.
    public string DefaultIbsCbsCst { get; set; } = "000";
    public string DefaultIbsCbsClass { get; set; } = "000001";
    public decimal IbsRate { get; set; } = 0.1m;
    public decimal CbsRate { get; set; } = 0.9m;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Configuração fiscal da PLATAFORMA (você, para emitir NFS-e das mensalidades). Desligada enquanto não houver CNPJ.</summary>
public class NfseDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public Guid TenantId { get; set; }
    public long AmountCents { get; set; }
    public string Status { get; set; } = "pending";
    public string? Number { get; set; }
    public string? VerificationCode { get; set; }
    public string? PdfUrl { get; set; }
    public string? LastMessage { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? IssuedAt { get; set; }
}

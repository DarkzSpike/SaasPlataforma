namespace Comercial.Domain;

/// <summary>
/// Marca entidades pertencentes a uma empresa (tenant). O TenantId é sempre
/// preenchido pelo servidor a partir do contexto autenticado — nunca pelo cliente.
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; set; }
}

public abstract class TenantEntity : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
}

/// <summary>Erro de regra de negócio, convertido em HTTP 400/409 sem detalhes internos.</summary>
public class DomainException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

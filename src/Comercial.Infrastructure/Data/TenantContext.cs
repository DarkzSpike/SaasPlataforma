namespace Comercial.Infrastructure.Data;

/// <summary>
/// Escopo da requisição atual, preenchido SOMENTE pelo servidor a partir da identidade
/// autenticada (claim do token) ou de uma sessão de suporte validada.
/// </summary>
public interface ITenantContext
{
    Guid TenantId { get; }
    /// <summary>Contexto administrativo do SaaS (sem tenant): consultas a dados de lojas exigem IgnoreQueryFilters explícito.</summary>
    bool IsPlatformScope { get; }
}

public sealed class TenantContext : ITenantContext
{
    public Guid TenantId { get; private set; }
    public bool IsPlatformScope { get; private set; }

    public void SetTenant(Guid tenantId)
    {
        if (TenantId != Guid.Empty && TenantId != tenantId)
            throw new InvalidOperationException("O escopo de tenant já foi definido para esta requisição.");
        TenantId = tenantId;
    }

    public void SetPlatformScope() => IsPlatformScope = true;

    /// <summary>
    /// Escopo de plataforma TEMPORÁRIO para buscas que antecedem a identificação do tenant
    /// (sessão por hash do refresh token, sessão de suporte, webhook). Necessário com RLS no PostgreSQL.
    /// </summary>
    public IDisposable PlatformLookup()
    {
        var previous = IsPlatformScope;
        IsPlatformScope = true;
        return new Restore(() => IsPlatformScope = previous);
    }

    private sealed class Restore(Action a) : IDisposable { public void Dispose() => a(); }
}

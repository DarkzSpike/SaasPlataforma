using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record FiscalProviderResult(FiscalStatus Status, string Message, string? Protocol = null, string? AccessKey = null);

/// <summary>
/// Abstração do provedor fiscal (SEFAZ direto ou intermediário homologado).
/// Uma implementação real deve: assinar o XML com o certificado A1 do contribuinte,
/// transmitir ao webservice da UF, tratar rejeições e contingência conforme o MOC vigente.
/// </summary>
public interface IFiscalProvider
{
    string Name { get; }
    Task<FiscalProviderResult> TransmitAsync(FiscalDocument doc, CancellationToken ct);
}

/// <summary>
/// Provedor padrão: NÃO transmite e NUNCA autoriza. Mantém o documento como pendente,
/// para que nada seja apresentado como autorizado sem resposta real da SEFAZ.
/// </summary>
public sealed class NotConfiguredFiscalProvider : IFiscalProvider
{
    public string Name => "nao_configurado";
    public Task<FiscalProviderResult> TransmitAsync(FiscalDocument doc, CancellationToken ct) =>
        Task.FromResult(new FiscalProviderResult(FiscalStatus.PendingTransmission,
            "Provedor fiscal não configurado para esta empresa. Documento permanece pendente (não autorizado)."));
}

public sealed class FiscalService(AppDbContext db, IFiscalProvider provider, RequestActor actor, AuditService audit, ScopedLookup lookup)
{
    public async Task<FiscalDocument> TransmitAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await lookup.GetAsync<FiscalDocument>(documentId);
        if (doc.Status is FiscalStatus.Authorized or FiscalStatus.Cancelled)
            throw new DomainException("fiscal_final_state", "Documento já está em estado final.", 409);

        var result = await provider.TransmitAsync(doc, ct);
        doc.Attempts++;
        doc.ProviderName = provider.Name;
        doc.LastMessage = result.Message;
        if (result.Status == FiscalStatus.Authorized)
        {
            // Defesa: autorização sem protocolo e chave é tratada como falha do provedor.
            if (string.IsNullOrWhiteSpace(result.Protocol) || string.IsNullOrWhiteSpace(result.AccessKey))
                throw new DomainException("fiscal_provider_invalid", "Resposta de autorização sem protocolo.", 502);
            doc.Protocol = result.Protocol;
            doc.AccessKey = result.AccessKey;
            doc.AuthorizedAt = DateTimeOffset.UtcNow;
        }
        doc.Status = result.Status;
        db.FiscalEvents.Add(new FiscalEvent { FiscalDocumentId = doc.Id, Type = "transmit", Message = result.Message });
        audit.Add("fiscal.transmit", actor.TenantId, nameof(FiscalDocument), doc.Id, new { status = doc.Status.ToString(), provider = provider.Name });
        await db.SaveChangesAsync(ct);
        return doc;
    }

    public Task<List<FiscalDocument>> ListAsync(FiscalStatus? status) =>
        db.FiscalDocuments.AsNoTracking().Where(d => status == null || d.Status == status).OrderByDescending(d => d.CreatedAt).Take(500).ToListAsync();
}

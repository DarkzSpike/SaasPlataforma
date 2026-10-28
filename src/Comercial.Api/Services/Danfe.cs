using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record DanfeItem(string Code, string Name, decimal Quantity, string Unit, long UnitCents, long TotalCents);
public sealed record DanfePayment(string Name, long Cents);
/// <summary>Dados do DANFE NFC-e (cupom impresso no caixa). Só existe quando a nota foi autorizada (ou denegada) pela SEFAZ.</summary>
public sealed record DanfeData(string Status, string Message, int? Number, int? Series, string? AccessKey, string? Protocol, DateTimeOffset? AuthorizedAt,
    DateTimeOffset IssuedAt, bool Homologation, string? QrCode, string? ConsultUrl, string EmitterName, string EmitterCnpj, string? EmitterIe, string EmitterAddress,
    List<DanfeItem> Items, long SubtotalCents, long DiscountCents, long TotalCents, List<DanfePayment> Payments, long ChangeCents);

/// <summary>
/// Depois que o caixa sincroniza a venda: emite a NFC-e se ainda estiver pendente e devolve o que imprimir.
/// Status "sem_nfce" = a loja não emite NFC-e (o caixa imprime o comprovante não fiscal de sempre).
/// </summary>
public sealed class DanfeService(AppDbContext db, FiscalService fiscal)
{
    private static string PayName(PaymentMethod m) => m switch
    {
        PaymentMethod.Cash => "Dinheiro", PaymentMethod.Pix => "PIX", PaymentMethod.DebitCard => "Cartão de Débito", PaymentMethod.CreditCard => "Cartão de Crédito",
        PaymentMethod.StoreCredit => "Crédito Loja", _ => "Outros",
    };

    public async Task<DanfeData> ForSaleAsync(string saleKey, bool emit, CancellationToken ct)
    {
        var sale = await db.Sales.AsNoTracking().Include(s => s.Items).Include(s => s.Payments).FirstOrDefaultAsync(s => s.IdempotencyKey == saleKey, ct)
            ?? throw new DomainException("not_found", "Venda ainda não chegou ao servidor.", 404);
        var profile = await db.FiscalProfiles.AsNoTracking().FirstOrDefaultAsync(ct);
        var doc = await db.FiscalDocuments.FirstOrDefaultAsync(d => d.SaleId == sale.Id, ct);
        string status;
        if (doc is null || profile is null || profile.Provider == "none") status = "sem_nfce";
        else
        {
            // Emite agora (não espera o serviço automático) para o cupom sair na hora da venda.
            if (emit && doc.Status is FiscalStatus.PendingTransmission && (doc.NextAttemptAt is null || doc.Attempts == 0))
                doc = await fiscal.TransmitDocAsync(doc, ct);
            status = FiscalArchiveService.StatusKey(doc.Status, doc.AuthorizedAt);
        }
        var skus = await db.Products.AsNoTracking().Where(p => sale.Items.Select(i => i.ProductId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => (p.Sku, p.Unit), ct);
        var address = profile is null ? "" : $"{profile.Street}, {profile.AddressNumber} - {profile.District} - {profile.CityName}/{profile.Uf}";
        return new(status, doc?.LastMessage ?? "", doc?.Number, doc?.Series, doc?.AccessKey, doc?.Protocol, doc?.AuthorizedAt, doc?.EmissionAt ?? sale.ReceivedAt,
            (profile?.Environment ?? 2) != 1, doc?.QrCodeUrl, doc?.ConsultUrl, profile?.LegalName is { Length: > 0 } ln ? ln : (profile?.TradeName ?? ""), profile?.Cnpj ?? "",
            profile?.StateRegistration, address,
            sale.Items.Select(i => new DanfeItem(skus.GetValueOrDefault(i.ProductId).Sku ?? "", i.ProductName, i.Quantity, skus.GetValueOrDefault(i.ProductId).Unit ?? "UN",
                i.UnitPriceCents, i.TotalCents)).ToList(),
            sale.SubtotalCents, sale.DiscountCents, sale.TotalCents, sale.Payments.Select(p => new DanfePayment(PayName(p.Method), p.AmountCents)).ToList(), sale.ChangeCents);
    }
}

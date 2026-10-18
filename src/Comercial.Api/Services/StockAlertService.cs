using Comercial.Api.Security;
using Comercial.Domain.Modules;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

/// <summary>
/// Notificações inteligentes (módulo 42): avisa o dono quando um produto chega ao estoque mínimo.
/// Regra de frequência: no máximo um aviso por produto a cada 24 horas. Nunca bloqueia a venda.
/// </summary>
public sealed class StockAlertService(AppDbContext db, RequestActor actor, FeatureAuthorization features, NotificationService notifications, ILogger<StockAlertService> log)
{
    public async Task CheckAsync(Guid storeId, IEnumerable<Guid> productIds)
    {
        try
        {
            if (!(await features.ModulesAsync()).Contains(ModuleKeys.Notifications)) return;
            var ids = productIds.Distinct().ToList();
            var low = await (from b in db.StockBalances.AsNoTracking()
                             join p in db.Products.AsNoTracking() on b.ProductId equals p.Id
                             where b.StoreId == storeId && ids.Contains(b.ProductId) && p.MinStock > 0 && b.Quantity <= p.MinStock
                             select new { p.Id, p.Name, b.Quantity, p.MinStock }).ToListAsync();
            var since = DateTimeOffset.UtcNow.AddDays(-1);
            foreach (var p in low)
            {
                var key = p.Id.ToString();
                if (await db.Notifications.AnyAsync(n => n.Type == "stock.low" && n.CreatedAt > since && n.DataJson!.Contains(key))) continue;
                await notifications.CreateAsync(actor.TenantId, "stock.low", $"Estoque baixo: {p.Name}",
                    $"Restam {p.Quantity:0.###} unidade(s) de {p.Name} (mínimo {p.MinStock:0.###}). Considere repor.",
                    new { productId = p.Id, quantity = p.Quantity, min = p.MinStock, storeId });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Falha ao verificar estoque baixo");
        }
    }
}

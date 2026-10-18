using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

/// <summary>
/// Busca por Id respeitando o filtro de tenant. Se o registro existir em OUTRO tenant,
/// registra tentativa de acesso cruzado e responde exatamente como "não encontrado".
/// </summary>
public sealed class ScopedLookup(AppDbContext db, AuditService audit, RequestActor actor, TenantContext tenant)
{
    public async Task<T> GetAsync<T>(Guid id, Func<IQueryable<T>, IQueryable<T>>? shape = null) where T : TenantEntity
    {
        IQueryable<T> q = db.Set<T>();
        if (shape is not null) q = shape(q);
        var e = await q.FirstOrDefaultAsync(x => x.Id == id);
        if (e is not null) return e;

        bool foreign;
        using (tenant.PlatformLookup()) // somente verifica existência; nenhum dado é retornado
            foreign = await db.Set<T>().IgnoreQueryFilters().AnyAsync(x => x.Id == id && x.TenantId != actor.TenantId);
        if (foreign)
            await audit.WriteNowAsync("security.cross_tenant_probe", actor.TenantId, typeof(T).Name, id);
        throw new DomainException("not_found", "Registro não encontrado.", 404);
    }
}

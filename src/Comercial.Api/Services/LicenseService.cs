using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Modules;
using Comercial.Infrastructure.Data;
using Comercial.Shared.Licensing;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

/// <summary>
/// Fonte única de verdade para estado de licença e módulos efetivos.
/// Estado da assinatura, tolerância de pagamento, validade offline e módulos são conceitos separados.
/// </summary>
public sealed class LicenseService(AppDbContext db, KeyMaterial keys, IConfiguration config, TimeProvider clock)
{
    public int OfflineHours => config.GetValue("License:OfflineValidityHours", 72);
    public const int ExpiringWindowDays = 5;

    public static string ComputeState(Tenant tenant, Subscription? sub, DateTimeOffset now)
    {
        if (tenant.Status == TenantStatus.Cancelled) return LicenseStates.Cancelled;
        if (tenant.Status == TenantStatus.Suspended) return LicenseStates.Suspended;
        if (sub is null) return LicenseStates.Restricted;
        if (sub.Status == SubscriptionStatus.Cancelled) return LicenseStates.Cancelled;
        if (sub.Status == SubscriptionStatus.Suspended) return LicenseStates.Suspended;
        if (now <= sub.CurrentPeriodEnd)
            return sub.CurrentPeriodEnd - now <= TimeSpan.FromDays(ExpiringWindowDays) ? LicenseStates.Expiring : LicenseStates.Active;
        if (now <= sub.CurrentPeriodEnd.AddDays(sub.GraceDays)) return LicenseStates.Grace;
        return LicenseStates.Restricted;
    }

    public async Task<(Tenant Tenant, Subscription? Sub, string State)> GetStateAsync(Guid tenantId)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantId);
        var sub = await db.Subscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Status != SubscriptionStatus.Cancelled)
            .OrderByDescending(s => s.StartedAt).FirstOrDefaultAsync();
        return (tenant, sub, ComputeState(tenant, sub, clock.GetUtcNow()));
    }

    /// <summary>
    /// Módulos efetivos: habilitados para o tenant, dentro da vigência, não "Planned"
    /// e com todas as dependências também efetivas. "core" é sempre incluído.
    /// </summary>
    public async Task<HashSet<string>> GetEffectiveModulesAsync(Guid tenantId)
    {
        var now = clock.GetUtcNow();
        var enabled = (await db.TenantModules.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.Enabled)
            .Select(m => new { m.ModuleKey, m.ValidUntil }).ToListAsync())
            .Where(m => m.ValidUntil is null || m.ValidUntil > now)
            .Select(m => m.ModuleKey).ToHashSet();
        var catalog = await db.ModuleCatalog.AsNoTracking().Include(m => m.Dependencies).ToDictionaryAsync(m => m.Key);
        return Resolve(enabled, catalog);
    }

    public static HashSet<string> Resolve(HashSet<string> enabled, IReadOnlyDictionary<string, ModuleDefinition> catalog)
    {
        var result = new HashSet<string> { ModuleKeys.Core };
        var path = new HashSet<string>(); // caminho atual da busca (detecta ciclo sem confundir com dependência compartilhada)
        bool Ok(string key)
        {
            if (!enabled.Contains(key) || !catalog.TryGetValue(key, out var def) || def.Status == ModuleStatus.Planned) return false;
            if (!path.Add(key)) return false; // ciclo
            var ok = def.Dependencies.All(d => d.DependsOnKey == ModuleKeys.Core || Ok(d.DependsOnKey));
            path.Remove(key);
            return ok;
        }
        foreach (var k in enabled) if (Ok(k)) result.Add(k);
        return result;
    }

    public async Task<string> IssueGrantAsync(Guid tenantId, Guid deviceId)
    {
        var (tenant, sub, state) = await GetStateAsync(tenantId);
        var modules = await GetEffectiveModulesAsync(tenantId);
        var branding = await db.Brandings.AsNoTracking().Where(b => b.TenantId == tenantId).Select(b => b.PublishedVersion).FirstOrDefaultAsync();
        var now = clock.GetUtcNow();
        var grant = new LicenseGrant
        {
            TenantId = tenantId,
            TenantCode = tenant.Code,
            DeviceId = deviceId,
            State = state,
            Modules = modules.Order().ToArray(),
            IssuedAt = now,
            OfflineValidUntil = now.AddHours(OfflineHours),
            SubscriptionPeriodEnd = sub?.CurrentPeriodEnd ?? now,
            BrandingVersion = branding,
            KeyId = keys.LicenseKeyId,
        };
        return LicenseGrantCodec.Sign(grant, keys.LicenseSigningKey);
    }
}

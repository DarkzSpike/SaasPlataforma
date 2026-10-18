using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Modules;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record ModuleChangeRequest(string ConfirmTenantCode, string Reason, DateTimeOffset? ValidUntil = null);

public sealed record ModuleImpact(
    string ModuleKey, string Action, bool Allowed, string? BlockReason,
    List<string> MissingDependencies, List<string> DependentModulesEnabled,
    Dictionary<string, int> PendingOperations, string Explanation);

/// <summary>
/// Ativação/desativação de módulos por empresa, a partir do painel. Desativar NUNCA apaga dados:
/// apenas bloqueia as funcionalidades no servidor (o histórico continua consultável).
/// </summary>
public sealed class ModuleAdminService(AppDbContext db, AuditService audit, TimeProvider clock)
{
    public async Task<ModuleImpact> ImpactAsync(Guid tenantId, string key, bool enable)
    {
        var catalog = await db.ModuleCatalog.AsNoTracking().Include(m => m.Dependencies).ToDictionaryAsync(m => m.Key);
        if (!catalog.TryGetValue(key, out var def)) throw new DomainException("module_not_found", "Módulo inexistente.", 404);
        var enabled = await db.TenantModules.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.Enabled).Select(m => m.ModuleKey).ToHashSetAsync();
        var pending = new Dictionary<string, int>();

        if (def.IsCore)
            return new(key, enable ? "enable" : "disable", false, "Módulo central não pode ser alterado.", [], [], pending, "");

        if (enable)
        {
            if (def.Status == ModuleStatus.Planned)
                return new(key, "enable", false, "Módulo ainda não implementado (status Planned).", [], [], pending, "");
            var missing = def.Dependencies.Select(d => d.DependsOnKey).Where(d => d != ModuleKeys.Core && !enabled.Contains(d)).ToList();
            return new(key, "enable", missing.Count == 0, missing.Count == 0 ? null : "Habilite antes as dependências.", missing, [], pending,
                $"Libera '{def.Name}' para a empresa. Usuários ainda precisam de permissão individual.");
        }

        var dependents = catalog.Values.Where(m => enabled.Contains(m.Key) && m.Dependencies.Any(d => d.DependsOnKey == key)).Select(m => m.Key).ToList();
        if (key == ModuleKeys.Cash || key == ModuleKeys.Pdv)
            pending["caixas_abertos"] = await db.CashSessions.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenantId && c.Status == CashSessionStatus.Open);
        if (key == ModuleKeys.Fiscal || key == ModuleKeys.Pdv)
            pending["documentos_fiscais_pendentes"] = await db.FiscalDocuments.IgnoreQueryFilters()
                .CountAsync(f => f.TenantId == tenantId && (f.Status == FiscalStatus.PendingTransmission || f.Status == FiscalStatus.Contingency));
        var blockers = new List<string>();
        if (dependents.Count > 0) blockers.Add("Há módulos habilitados que dependem deste: " + string.Join(", ", dependents));
        if (pending.Values.Any(v => v > 0)) blockers.Add("Há operações pendentes: " + string.Join(", ", pending.Where(p => p.Value > 0).Select(p => $"{p.Key}={p.Value}")));
        return new(key, "disable", blockers.Count == 0, blockers.Count == 0 ? null : string.Join(" | ", blockers), [], dependents, pending,
            $"Bloqueia novas operações de '{def.Name}'. Registros históricos são preservados e continuam consultáveis.");
    }

    public async Task<TenantModule> SetAsync(Guid tenantId, string key, bool enable, ModuleChangeRequest r)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId) ?? throw new DomainException("tenant_not_found", "Empresa não encontrada.", 404);
        if (!string.Equals(r.ConfirmTenantCode, tenant.Code, StringComparison.Ordinal))
            throw new DomainException("confirmation_mismatch", "Código de confirmação da empresa não confere.", 409);
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length < 5) throw new DomainException("reason_required", "Informe o motivo.");

        var impact = await ImpactAsync(tenantId, key, enable);
        if (!impact.Allowed) throw new DomainException("module_change_blocked", impact.BlockReason ?? "Alteração bloqueada.", 409);

        var tm = await db.TenantModules.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.TenantId == tenantId && m.ModuleKey == key);
        var before = tm?.Enabled;
        if (tm is null) { tm = new TenantModule { TenantId = tenantId, ModuleKey = key, Source = ModuleSource.Manual }; db.TenantModules.Add(tm); }
        tm.Enabled = enable;
        tm.ValidUntil = enable ? r.ValidUntil : tm.ValidUntil;
        tm.ChangedAt = clock.GetUtcNow();
        tm.Reason = r.Reason;
        audit.Add(enable ? "module.enabled" : "module.disabled", tenantId, "module", key, new { before, after = enable, r.Reason, r.ValidUntil });
        await db.SaveChangesAsync();
        return tm;
    }
}

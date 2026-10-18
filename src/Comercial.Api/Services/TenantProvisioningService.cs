using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Comercial.Shared.Branding;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Comercial.Api.Services;

public sealed record OwnerInput(string Email, string DisplayName, string Password);
public sealed record CreateTenantRequest(string Code, string LegalName, string TradeName, string? Document, string PlanCode, OwnerInput Owner, string StoreName);
public sealed record PlanInput(string Code, string Name, long PriceCents, int Period, int MaxUsers, int MaxDevices, int MaxStores, int GraceDays, List<string> Modules);

public sealed partial class TenantProvisioningService(AppDbContext db, PasswordHasher hasher, AuditService audit, TimeProvider clock)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{2,39}$")]
    private static partial Regex CodeRx();

    public async Task<Tenant> CreateAsync(CreateTenantRequest r)
    {
        var code = (r.Code ?? "").Trim().ToLowerInvariant();
        if (!CodeRx().IsMatch(code)) throw new DomainException("invalid_code", "Código deve ter 3-40 caracteres: letras minúsculas, números e hífen.");
        if (await db.Tenants.AnyAsync(t => t.Code == code)) throw new DomainException("code_taken", "Código já utilizado.", 409);
        if (string.IsNullOrWhiteSpace(r.LegalName) || r.LegalName.Length > 200) throw new DomainException("invalid_name", "Razão social inválida.");
        if (PasswordHasher.ValidatePolicy(r.Owner.Password) is { } pe) throw new DomainException("weak_password", pe);
        var plan = await db.Plans.Include(p => p.Modules).FirstOrDefaultAsync(p => p.Code == r.PlanCode && p.IsActive)
                   ?? throw new DomainException("plan_not_found", "Plano não encontrado ou inativo.", 404);

        var now = clock.GetUtcNow();
        var tenant = new Tenant { Code = code, LegalName = r.LegalName.Trim(), TradeName = (r.TradeName ?? r.LegalName).Trim(), Document = r.Document };
        db.Tenants.Add(tenant);
        db.Stores.Add(new Store { TenantId = tenant.Id, Code = "matriz", Name = string.IsNullOrWhiteSpace(r.StoreName) ? "Matriz" : r.StoreName.Trim() });
        var owner = new User
        {
            TenantId = tenant.Id, Email = r.Owner.Email.Trim().ToLowerInvariant(), DisplayName = r.Owner.DisplayName.Trim(),
            PasswordHash = hasher.Hash(r.Owner.Password),
        };
        owner.Roles.Add(new UserRole { TenantId = tenant.Id, UserId = owner.Id, Role = Roles.Owner });
        db.Users.Add(owner);
        var sub = SubscriptionFrom(plan, tenant.Id, now);
        db.Subscriptions.Add(sub);
        foreach (var m in plan.Modules)
            db.TenantModules.Add(new TenantModule { TenantId = tenant.Id, ModuleKey = m.ModuleKey, Source = ModuleSource.Plan, Reason = "plano " + plan.Code });
        var branding = BrandingRules.Preset("moderno");
        branding.CompanyName = tenant.TradeName;
        db.Brandings.Add(new TenantBranding { TenantId = tenant.Id, DraftJson = JsonSerializer.Serialize(branding), PublishedVersion = 0 });
        audit.Add("tenant.created", tenant.Id, nameof(Tenant), tenant.Id, new { code, plan = plan.Code });
        await db.SaveChangesAsync();
        return tenant;
    }

    /// <summary>Snapshot do plano: mudanças futuras no plano não alteram esta assinatura.</summary>
    public static Subscription SubscriptionFrom(SubscriptionPlan plan, Guid tenantId, DateTimeOffset now) => new()
    {
        TenantId = tenantId, PlanId = plan.Id, PlanCode = plan.Code, PriceCents = plan.PriceCents, Period = plan.Period,
        MaxUsers = plan.MaxUsers, MaxDevices = plan.MaxDevices, MaxStores = plan.MaxStores, GraceDays = plan.GraceDays,
        StartedAt = now, CurrentPeriodEnd = now.AddMonths((int)plan.Period),
    };

    public async Task<SubscriptionPlan> UpsertPlanAsync(PlanInput r)
    {
        if (string.IsNullOrWhiteSpace(r.Code) || r.PriceCents < 0 || r.MaxUsers < 1 || r.MaxDevices < 1 || r.MaxStores < 1 || r.GraceDays is < 0 or > 60)
            throw new DomainException("invalid_plan", "Dados do plano inválidos.");
        if (!Enum.IsDefined(typeof(BillingPeriod), r.Period)) throw new DomainException("invalid_period", "Periodicidade inválida.");
        var catalog = await db.ModuleCatalog.AsNoTracking().Include(m => m.Dependencies).ToDictionaryAsync(m => m.Key);
        foreach (var k in r.Modules)
        {
            if (!catalog.TryGetValue(k, out var def)) throw new DomainException("module_not_found", $"Módulo {k} inexistente.");
            if (def.IsCore) throw new DomainException("core_module", "O módulo central é implícito.");
            var missing = def.Dependencies.Select(d => d.DependsOnKey).Where(d => !r.Modules.Contains(d)).ToList();
            if (missing.Count > 0) throw new DomainException("plan_dependency", $"Módulo {k} depende de: {string.Join(", ", missing)}");
        }
        var plan = await db.Plans.Include(p => p.Modules).FirstOrDefaultAsync(p => p.Code == r.Code);
        var isNew = plan is null;
        plan ??= new SubscriptionPlan { Code = r.Code };
        plan.Name = r.Name; plan.PriceCents = r.PriceCents; plan.Period = (BillingPeriod)r.Period;
        plan.MaxUsers = r.MaxUsers; plan.MaxDevices = r.MaxDevices; plan.MaxStores = r.MaxStores; plan.GraceDays = r.GraceDays;
        plan.Modules.Clear();
        foreach (var k in r.Modules.Distinct()) plan.Modules.Add(new PlanModule { PlanId = plan.Id, ModuleKey = k });
        if (isNew) db.Plans.Add(plan);
        audit.Add(isNew ? "plan.created" : "plan.updated", null, nameof(SubscriptionPlan), plan.Id, r);
        await db.SaveChangesAsync();
        return plan;
    }
}

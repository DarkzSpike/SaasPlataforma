using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Comercial.Domain;
using Comercial.Domain.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Comercial.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant) : DbContext(options)
{
    /// <summary>Usado pelo filtro global. Guid.Empty (sem tenant) não retorna nenhum registro — falha fechada.</summary>
    public Guid CurrentTenantId => tenant.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<UserPermissionGrant> UserPermissionGrants => Set<UserPermissionGrant>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<ModuleDefinition> ModuleCatalog => Set<ModuleDefinition>();
    public DbSet<ModuleDependency> ModuleDependencies => Set<ModuleDependency>();
    public DbSet<SubscriptionPlan> Plans => Set<SubscriptionPlan>();
    public DbSet<PlanModule> PlanModules => Set<PlanModule>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<TenantModule> TenantModules => Set<TenantModule>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();
    public DbSet<SupportSession> SupportSessions => Set<SupportSession>();
    public DbSet<SupportElevationRequest> SupportElevationRequests => Set<SupportElevationRequest>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<TenantBranding> Brandings => Set<TenantBranding>();
    public DbSet<BrandingVersion> BrandingVersions => Set<BrandingVersion>();
    public DbSet<BrandingAsset> BrandingAssets => Set<BrandingAsset>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleItem> SaleItems => Set<SaleItem>();
    public DbSet<SalePayment> SalePayments => Set<SalePayment>();
    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<StoreHours> StoreHours => Set<StoreHours>();
    public DbSet<OperatingEvent> OperatingEvents => Set<OperatingEvent>();
    public DbSet<FiscalDocument> FiscalDocuments => Set<FiscalDocument>();
    public DbSet<FiscalEvent> FiscalEvents => Set<FiscalEvent>();
    public DbSet<SyncEvent> SyncEvents => Set<SyncEvent>();

    /// <summary>Entidades que nunca podem ser alteradas ou excluídas depois de gravadas.</summary>
    private static readonly HashSet<Type> Immutable =
    [
        typeof(AuditLog), typeof(StockMovement), typeof(CashMovement), typeof(OperatingEvent), typeof(SaleItem),
        typeof(SalePayment), typeof(BrandingVersion), typeof(PaymentEvent), typeof(FiscalEvent), typeof(SyncEvent)
    ];

    /// <summary>Entidades que podem mudar de estado, mas nunca ser excluídas.</summary>
    private static readonly HashSet<Type> NoDelete =
    [
        typeof(Sale), typeof(FiscalDocument), typeof(CashSession), typeof(Invoice), typeof(Tenant), typeof(Subscription),
        typeof(Product), typeof(User), typeof(Device), typeof(SupportSession), typeof(StockBalance)
    ];

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        if (Database.IsSqlite())
        {
            // SQLite não ordena/compara DateTimeOffset nem soma decimal nativamente.
            b.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            b.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            b.Properties<decimal>().HaveConversion<double>();
        }
        else
        {
            b.Properties<decimal>().HavePrecision(18, 3);
            // timestamptz só aceita offset 0: clientes no Brasil enviam -03:00, então normalizamos para UTC.
            b.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
            b.Properties<DateTimeOffset?>().HaveConversion<UtcDateTimeOffsetConverter>();
        }
    }

    private sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v);

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(40);
            e.Property(x => x.LegalName).HasMaxLength(200);
            e.Property(x => x.TradeName).HasMaxLength(200);
        });
        m.Entity<Store>(e =>
        {
            e.ToTable("stores");
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });
        m.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();
            e.Property(x => x.Email).HasMaxLength(200);
            e.HasMany(x => x.Roles).WithOne().HasForeignKey(r => new { r.TenantId, r.UserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<UserRole>(e => { e.ToTable("user_roles"); e.HasIndex(x => new { x.TenantId, x.UserId, x.Role }).IsUnique(); });
        m.Entity<UserPermissionGrant>(e => { e.ToTable("user_permissions"); e.HasIndex(x => new { x.TenantId, x.UserId, x.Permission }).IsUnique(); });
        m.Entity<UserSession>(e =>
        {
            e.ToTable("user_sessions");
            e.HasIndex(x => x.RefreshTokenHash).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.UserId });
            e.HasIndex(x => x.FamilyId);
        });
        m.Entity<Device>(e => { e.ToTable("devices"); e.HasIndex(x => new { x.TenantId, x.Status }); });
        m.Entity<AdminUser>(e => { e.ToTable("admin_users"); e.HasIndex(x => x.Email).IsUnique(); });

        m.Entity<ModuleDefinition>(e =>
        {
            e.ToTable("module_catalog");
            e.HasKey(x => x.Key);
            e.HasMany(x => x.Dependencies).WithOne().HasForeignKey(d => d.ModuleKey);
        });
        m.Entity<ModuleDependency>(e =>
        {
            e.ToTable("module_dependencies");
            e.HasKey(x => new { x.ModuleKey, x.DependsOnKey });
            e.HasOne<ModuleDefinition>().WithMany().HasForeignKey(x => x.DependsOnKey).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<SubscriptionPlan>(e =>
        {
            e.ToTable("subscription_plans");
            e.HasIndex(x => x.Code).IsUnique();
            e.HasMany(x => x.Modules).WithOne().HasForeignKey(x => x.PlanId);
        });
        m.Entity<PlanModule>(e =>
        {
            e.ToTable("plan_modules");
            e.HasKey(x => new { x.PlanId, x.ModuleKey });
            e.HasOne<ModuleDefinition>().WithMany().HasForeignKey(x => x.ModuleKey).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Subscription>(e => { e.ToTable("subscriptions"); e.HasIndex(x => x.TenantId); });
        m.Entity<TenantModule>(e =>
        {
            e.ToTable("tenant_modules");
            e.HasKey(x => new { x.TenantId, x.ModuleKey });
            e.HasOne<ModuleDefinition>().WithMany().HasForeignKey(x => x.ModuleKey).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Invoice>(e => { e.ToTable("invoices"); e.HasIndex(x => new { x.TenantId, x.Status }); });
        m.Entity<PaymentEvent>(e => { e.ToTable("payment_events"); e.HasIndex(x => new { x.Provider, x.ProviderEventId }).IsUnique(); });
        m.Entity<SupportSession>(e => { e.ToTable("support_sessions"); e.HasIndex(x => new { x.TenantId, x.StartedAt }); });
        m.Entity<SupportElevationRequest>(e => e.ToTable("support_elevation_requests"));
        m.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            e.HasIndex(x => x.Action);
        });
        m.Entity<TenantBranding>(e => { e.ToTable("tenant_branding"); e.HasKey(x => x.TenantId); });
        m.Entity<BrandingVersion>(e => { e.ToTable("branding_versions"); e.HasIndex(x => new { x.TenantId, x.Version }).IsUnique(); });
        m.Entity<BrandingAsset>(e => { e.ToTable("branding_assets"); e.HasIndex(x => x.TenantId); });

        m.Entity<Category>(e => e.ToTable("categories"));
        m.Entity<Product>(e =>
        {
            e.ToTable("products");
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.Sku }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Barcode });
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        m.Entity<StockMovement>(e =>
        {
            e.ToTable("stock_movements");
            e.HasIndex(x => new { x.TenantId, x.ProductId, x.CreatedAt });
            e.HasOne<Product>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProductId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Store>().WithMany().HasForeignKey(x => new { x.TenantId, x.StoreId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<StockBalance>(e =>
        {
            e.ToTable("stock_balances");
            e.HasKey(x => new { x.TenantId, x.StoreId, x.ProductId });
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<Product>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProductId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Store>().WithMany().HasForeignKey(x => new { x.TenantId, x.StoreId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Sale>(e =>
        {
            e.ToTable("sales");
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.ReceivedAt });
            e.HasOne<Store>().WithMany().HasForeignKey(x => new { x.TenantId, x.StoreId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CashSession>().WithMany().HasForeignKey(x => new { x.TenantId, x.CashSessionId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => new { i.TenantId, i.SaleId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Payments).WithOne().HasForeignKey(i => new { i.TenantId, i.SaleId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<SaleItem>(e =>
        {
            e.ToTable("sale_items");
            e.HasOne<Product>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProductId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<SalePayment>(e => e.ToTable("payments"));
        m.Entity<CashSession>(e =>
        {
            e.ToTable("cash_sessions");
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            // Um operador só pode ter um caixa aberto por vez.
            e.HasIndex(x => new { x.TenantId, x.OperatorUserId }).IsUnique().HasFilter("\"Status\" = 1");
            e.HasOne<Store>().WithMany().HasForeignKey(x => new { x.TenantId, x.StoreId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<CashMovement>(e =>
        {
            e.ToTable("cash_movements");
            e.HasOne<CashSession>().WithMany().HasForeignKey(x => new { x.TenantId, x.CashSessionId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<StoreHours>(e => { e.ToTable("store_hours"); e.HasIndex(x => new { x.TenantId, x.StoreId, x.DayOfWeek }).IsUnique(); });
        m.Entity<OperatingEvent>(e =>
        {
            e.ToTable("operating_events");
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.StoreId, x.LocalTime });
            e.HasOne<Store>().WithMany().HasForeignKey(x => new { x.TenantId, x.StoreId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<FiscalDocument>(e =>
        {
            e.ToTable("fiscal_documents");
            e.HasIndex(x => new { x.TenantId, x.SaleId, x.Model }).IsUnique();
            e.HasOne<Sale>().WithMany().HasForeignKey(x => new { x.TenantId, x.SaleId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<FiscalEvent>(e => e.ToTable("fiscal_events"));
        m.Entity<SyncEvent>(e => { e.ToTable("sync_events"); e.HasIndex(x => new { x.TenantId, x.DeviceId, x.IdempotencyKey }).IsUnique(); });

        // FK para tenants em todas as entidades com escopo + filtro global de isolamento.
        foreach (var et in m.Model.GetEntityTypes().Where(t => typeof(ITenantScoped).IsAssignableFrom(t.ClrType)).ToList())
        {
            m.Entity(et.ClrType).HasOne(typeof(Tenant)).WithMany().HasForeignKey(nameof(ITenantScoped.TenantId)).OnDelete(DeleteBehavior.Restrict);
            GetType().GetMethod(nameof(ApplyTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(et.ClrType).Invoke(this, [m]);
        }
    }

    private void ApplyTenantFilter<T>(ModelBuilder m) where T : class, ITenantScoped
    {
        Expression<Func<T, bool>> filter = e => e.TenantId == CurrentTenantId;
        m.Entity<T>().HasQueryFilter(filter);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        EnforceRules();
        var audits = ChangeTracker.Entries<AuditLog>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        if (audits.Count == 0) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);

        // Encadeamento de hash precisa ser serializado entre transações concorrentes.
        var own = Database.CurrentTransaction is null;
        var tx = own ? await Database.BeginTransactionAsync(ct) : null;
        try
        {
            if (Database.IsNpgsql())
                await Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(727274)", ct);
            var prev = await AuditLogs.OrderByDescending(a => a.Id).Select(a => a.Hash).FirstOrDefaultAsync(ct) ?? "GENESIS";
            foreach (var a in audits)
            {
                a.OccurredAt = TruncateMs(a.OccurredAt == default ? DateTimeOffset.UtcNow : a.OccurredAt);
                a.PreviousHash = prev;
                a.Hash = ComputeAuditHash(a);
                prev = a.Hash;
            }
            var n = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return n;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    private void EnforceRules()
    {
        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            var type = entry.Entity.GetType();
            if (entry.State is EntityState.Modified or EntityState.Deleted && Immutable.Contains(type))
                throw new InvalidOperationException($"{type.Name} é imutável: alteração/exclusão recusada.");
            if (entry.State == EntityState.Deleted && NoDelete.Contains(type))
                throw new InvalidOperationException($"{type.Name} não pode ser excluído (use inativação/cancelamento auditado).");

            if (entry.Entity is not ITenantScoped scoped) continue;
            if (entry.State == EntityState.Added)
            {
                if (scoped.TenantId == Guid.Empty)
                {
                    if (CurrentTenantId == Guid.Empty) throw new InvalidOperationException("Registro sem tenant definido.");
                    scoped.TenantId = CurrentTenantId;
                }
                else if (CurrentTenantId != Guid.Empty && scoped.TenantId != CurrentTenantId)
                {
                    throw new InvalidOperationException("Tentativa de gravar registro em outro tenant.");
                }
                else if (CurrentTenantId == Guid.Empty && !tenant.IsPlatformScope)
                {
                    throw new InvalidOperationException("Gravação sem escopo autorizado.");
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                var prop = entry.Property(nameof(ITenantScoped.TenantId));
                if (prop.IsModified && !Equals(prop.OriginalValue, prop.CurrentValue))
                    throw new InvalidOperationException("TenantId não pode ser alterado.");
                if (CurrentTenantId != Guid.Empty && scoped.TenantId != CurrentTenantId)
                    throw new InvalidOperationException("Tentativa de alterar registro de outro tenant.");
            }
        }
    }

    public static DateTimeOffset TruncateMs(DateTimeOffset d) =>
        DateTimeOffset.FromUnixTimeMilliseconds(d.ToUniversalTime().ToUnixTimeMilliseconds());

    public static string ComputeAuditHash(AuditLog a)
    {
        var s = string.Join("|", a.PreviousHash, a.TenantId, (int)a.ActorType, a.ActorId, a.SupportSessionId, a.Action,
            a.EntityType, a.EntityId, a.DataJson, a.Outcome, a.IpAddress, a.OccurredAt.ToUnixTimeMilliseconds());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    }
}

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
    public DbSet<TenantStorageSettings> StorageSettings => Set<TenantStorageSettings>();
    public DbSet<PlatformSetting> PlatformSettings => Set<PlatformSetting>();
    public DbSet<DiscountAuthorization> DiscountAuthorizations => Set<DiscountAuthorization>();
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();
    public DbSet<TenantPaymentSettings> TenantPaymentSettings => Set<TenantPaymentSettings>();
    public DbSet<PosPixCharge> PosPixCharges => Set<PosPixCharge>();
    public DbSet<TenantFiscalProfile> FiscalProfiles => Set<TenantFiscalProfile>();
    public DbSet<ProductBarcode> ProductBarcodes => Set<ProductBarcode>();
    public DbSet<ProductBatch> ProductBatches => Set<ProductBatch>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<StockTransferItem> StockTransferItems => Set<StockTransferItem>();
    public DbSet<ImportedInvoice> ImportedInvoices => Set<ImportedInvoice>();
    public DbSet<ProductComponent> ProductComponents => Set<ProductComponent>();
    public DbSet<Promotion> Promotions => Set<Promotion>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<QuoteItem> QuoteItems => Set<QuoteItem>();
    public DbSet<FiscalNumberVoid> FiscalNumberVoids => Set<FiscalNumberVoid>();
    public DbSet<NfseDocument> NfseDocuments => Set<NfseDocument>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<PendingSignup> PendingSignups => Set<PendingSignup>();
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
    public DbSet<FiscalXmlFile> FiscalXmlFiles => Set<FiscalXmlFile>();
    public DbSet<FiscalExport> FiscalExports => Set<FiscalExport>();
    public DbSet<FiscalMonthClosing> FiscalMonthClosings => Set<FiscalMonthClosing>();
    public DbSet<AccountantSettings> AccountantSettings => Set<AccountantSettings>();
    public DbSet<AccountantDelivery> AccountantDeliveries => Set<AccountantDelivery>();
    public DbSet<SyncEvent> SyncEvents => Set<SyncEvent>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PushToken> PushTokens => Set<PushToken>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();
    public DbSet<PurchaseReceipt> PurchaseReceipts => Set<PurchaseReceipt>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<LoyaltyProgram> LoyaltyPrograms => Set<LoyaltyProgram>();
    public DbSet<LoyaltyTransaction> LoyaltyTransactions => Set<LoyaltyTransaction>();
    public DbSet<FinancialTitle> FinancialTitles => Set<FinancialTitle>();
    public DbSet<SaleReturn> SaleReturns => Set<SaleReturn>();
    public DbSet<SaleReturnItem> SaleReturnItems => Set<SaleReturnItem>();

    /// <summary>Entidades que nunca podem ser alteradas ou excluídas depois de gravadas.</summary>
    private static readonly HashSet<Type> Immutable =
    [
        typeof(AuditLog), typeof(StockMovement), typeof(CashMovement), typeof(OperatingEvent), typeof(SaleItem),
        typeof(SalePayment), typeof(BrandingVersion), typeof(PaymentEvent), typeof(FiscalEvent), typeof(SyncEvent),
        typeof(LoyaltyTransaction), typeof(PurchaseReceipt), typeof(SaleReturn), typeof(SaleReturnItem), typeof(FiscalXmlFile)
    ];

    /// <summary>Entidades que podem mudar de estado, mas nunca ser excluídas.</summary>
    private static readonly HashSet<Type> NoDelete =
    [
        typeof(Sale), typeof(FiscalDocument), typeof(CashSession), typeof(Invoice), typeof(Tenant), typeof(Subscription),
        typeof(Product), typeof(User), typeof(Device), typeof(SupportSession), typeof(StockBalance),
        typeof(Supplier), typeof(PurchaseOrder), typeof(PurchaseItem), typeof(Customer), typeof(FinancialTitle),
        typeof(FiscalExport), typeof(FiscalMonthClosing), typeof(AccountantDelivery)
    ];

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        if (Database.IsSqlite())
        {
            // SQLite não ordena/compara DateTimeOffset nativamente. Gravamos SEMPRE em UTC: com fusos misturados
            // (ex.: filtro "hoje" vindo do PDV em -03:00) a comparação binária ficaria errada.
            b.Properties<DateTimeOffset>().HaveConversion<UtcBinaryConverter>();
            b.Properties<DateTimeOffset?>().HaveConversion<UtcBinaryConverter>();
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

    /// <summary>
    /// Mesmo formato do DateTimeOffsetToBinaryConverter ((ticks/1000) &lt;&lt; 11 | offset em minutos), mas sempre em UTC,
    /// o que torna a ordenação/comparação numérica correta. Lê registros antigos gravados com outro offset.
    /// </summary>
    public sealed class UtcBinaryConverter() : ValueConverter<DateTimeOffset, long>(v => (v.UtcTicks / 1000) << 11, v => FromBinary(v))
    {
        public static DateTimeOffset FromBinary(long v)
        {
            var offsetMinutes = (v << 53) >> 53; // 11 bits com sinal
            return new DateTimeOffset(new DateTime((v >> 11) * 1000), TimeSpan.FromMinutes(offsetMinutes)).ToUniversalTime();
        }
    }

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
        m.Entity<Invoice>(e =>
        {
            e.ToTable("invoices");
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => new { x.Status, x.DueAt });
            // Uma fatura válida por período da assinatura: a geração automática nunca duplica cobrança.
            e.HasIndex(x => new { x.TenantId, x.SubscriptionId, x.PeriodStart }).IsUnique().HasFilter("\"Status\" <> 3");
            e.Property(x => x.Origin).HasMaxLength(10);
            e.Property(x => x.PaidMethod).HasMaxLength(20);
            e.Property(x => x.MpPaymentId).HasMaxLength(40);
            e.Property(x => x.CheckoutPreferenceId).HasMaxLength(80);
            e.Property(x => x.CheckoutUrl).HasMaxLength(500);
        });
        m.Entity<DiscountAuthorization>(e =>
        {
            e.ToTable("discount_authorizations");
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.Property(x => x.Reason).HasMaxLength(200);
            // Uso único: a gravação só passa se ninguém tiver usado a autorização antes (concorrência otimista).
            e.Property(x => x.UsedBySaleId).IsConcurrencyToken();
        });
        m.Entity<User>().Property(x => x.CommissionPercent).HasPrecision(5, 2);
        m.Entity<ProductBatch>(e =>
        {
            e.ToTable("product_batches");
            e.HasIndex(x => new { x.TenantId, x.StoreId, x.ProductId, x.ExpiresOn });
            e.Property(x => x.BatchCode).HasMaxLength(40);
            e.Property(x => x.QuantityReceived).HasPrecision(18, 3);
            e.Property(x => x.QuantityRemaining).HasPrecision(18, 3);
        });
        m.Entity<StockTransfer>(e =>
        {
            e.ToTable("stock_transfers");
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.Property(x => x.Notes).HasMaxLength(300);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<StockTransferItem>(e => { e.ToTable("stock_transfer_items"); e.Property(x => x.Quantity).HasPrecision(18, 3); });
        m.Entity<ImportedInvoice>(e =>
        {
            e.ToTable("imported_invoices");
            e.HasIndex(x => new { x.TenantId, x.AccessKey }).IsUnique();
            e.Property(x => x.AccessKey).HasMaxLength(44);
            e.Property(x => x.Number).HasMaxLength(20);
            e.Property(x => x.SupplierName).HasMaxLength(200);
        });
        m.Entity<ProductBarcode>(e =>
        {
            e.ToTable("product_barcodes");
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.Property(x => x.Code).HasMaxLength(32);
            e.HasOne<Product>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProductId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ProductComponent>(e =>
        {
            e.ToTable("product_components");
            e.HasIndex(x => new { x.TenantId, x.KitProductId, x.ComponentProductId }).IsUnique();
            e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.HasOne<Product>().WithMany().HasForeignKey(x => new { x.TenantId, x.KitProductId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Product>().WithMany().HasForeignKey(x => new { x.TenantId, x.ComponentProductId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Promotion>(e =>
        {
            e.ToTable("promotions");
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.CouponCode).HasMaxLength(30);
            e.Property(x => x.MinQty).HasPrecision(18, 3);
            e.Property(x => x.Percent).HasPrecision(7, 3);
        });
        m.Entity<Quote>(e =>
        {
            e.ToTable("quotes");
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.Property(x => x.CustomerName).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(500);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.QuoteId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<QuoteItem>(e => { e.ToTable("quote_items"); e.Property(x => x.Quantity).HasPrecision(18, 3); e.Property(x => x.ProductName).HasMaxLength(200); });
        m.Entity<TenantFiscalProfile>(e =>
        {
            e.ToTable("tenant_fiscal_profiles");
            e.HasKey(x => x.TenantId);
            e.Property(x => x.Cnpj).HasMaxLength(14);
            e.Property(x => x.StateRegistration).HasMaxLength(20);
            e.Property(x => x.Uf).HasMaxLength(2);
            e.Property(x => x.Provider).HasMaxLength(20);
            // Lojas que já existiam continuam emitindo sempre.
            e.Property(x => x.EmissionMode).HasMaxLength(20).HasDefaultValue(NfceModes.Always);
            e.Property(x => x.IbsRate).HasPrecision(7, 4);
            e.Property(x => x.CbsRate).HasPrecision(7, 4);
        });
        m.Entity<FiscalNumberVoid>(e => e.ToTable("fiscal_number_voids"));
        m.Entity<NfseDocument>(e => { e.ToTable("nfse_documents"); e.HasIndex(x => x.InvoiceId).IsUnique(); });
        m.Entity<TenantPaymentSettings>(e => { e.ToTable("tenant_payment_settings"); e.HasKey(x => x.TenantId); e.Property(x => x.MpAccountLabel).HasMaxLength(200); });
        m.Entity<PosPixCharge>(e =>
        {
            e.ToTable("pos_pix_charges");
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.Property(x => x.MpPaymentId).HasMaxLength(40);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.UsedBySaleId).IsConcurrencyToken(); // uma cobrança paga uma única venda
        });
        m.Entity<EmailMessage>(e =>
        {
            e.ToTable("email_messages");
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.Property(x => x.To).HasMaxLength(320);
            e.Property(x => x.Subject).HasMaxLength(300);
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.LastError).HasMaxLength(300);
            e.Property(x => x.AttachmentPath).HasMaxLength(400);
            e.Property(x => x.AttachmentName).HasMaxLength(200);
        });
        m.Entity<PendingSignup>(e =>
        {
            e.ToTable("pending_signups");
            e.HasIndex(x => new { x.Email, x.CreatedAt });
            e.HasIndex(x => x.TenantCode);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.TenantCode).HasMaxLength(40);
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.Property(x => x.Ip).HasMaxLength(60);
        });
        m.Entity<PasswordResetToken>(e =>
        {
            e.ToTable("password_reset_tokens");
            e.HasIndex(x => new { x.TenantId, x.UserId, x.CreatedAt });
            e.Property(x => x.CodeHash).HasMaxLength(64);
        });
        m.Entity<PlatformSetting>(e => { e.ToTable("platform_settings"); e.HasKey(x => x.Key); e.Property(x => x.Key).HasMaxLength(100); });
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
        m.Entity<TenantStorageSettings>(e =>
        {
            e.ToTable("tenant_storage_settings");
            e.HasKey(x => x.TenantId);
            e.Property(x => x.Provider).HasMaxLength(20);
            e.Property(x => x.DriveFolderId).HasMaxLength(200);
            e.Property(x => x.ImpersonateUser).HasMaxLength(200);
            e.Property(x => x.CredentialsClientEmail).HasMaxLength(320);
            e.Property(x => x.LastTestMessage).HasMaxLength(500);
        });
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
            // Número da nota nunca se repete na mesma série (emissão direta reserva a numeração).
            e.HasIndex(x => new { x.TenantId, x.Model, x.Series, x.Number }).IsUnique().HasFilter("\"Number\" IS NOT NULL");
            e.HasOne<Sale>().WithMany().HasForeignKey(x => new { x.TenantId, x.SaleId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<FiscalEvent>(e => { e.ToTable("fiscal_events"); e.HasIndex(x => new { x.TenantId, x.FiscalDocumentId }); });
        // ----- Fiscal e contabilidade -----
        m.Entity<FiscalXmlFile>(e =>
        {
            e.ToTable("fiscal_xml_files");
            // Um original por documento e tipo: arquivar de novo não duplica.
            e.HasIndex(x => new { x.TenantId, x.FiscalDocumentId, x.Kind }).IsUnique().HasFilter("\"FiscalDocumentId\" IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.NumberVoidId }).IsUnique().HasFilter("\"NumberVoidId\" IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.ReferenceAt });
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.AccessKey).HasMaxLength(44);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.Source).HasMaxLength(40);
        });
        m.Entity<FiscalExport>(e =>
        {
            e.ToTable("fiscal_exports");
            e.HasIndex(x => new { x.TenantId, x.Year, x.Month, x.CreatedAt });
            e.Property(x => x.FileName).HasMaxLength(200);
            e.Property(x => x.StorageName).HasMaxLength(80);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.Trigger).HasMaxLength(20);
        });
        m.Entity<FiscalMonthClosing>(e =>
        {
            e.ToTable("fiscal_month_closings");
            e.HasIndex(x => new { x.TenantId, x.Year, x.Month }).IsUnique();
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Notes).HasMaxLength(2000);
        });
        m.Entity<AccountantSettings>(e =>
        {
            e.ToTable("accountant_settings");
            e.HasKey(x => x.TenantId);
            e.Property(x => x.OfficeName).HasMaxLength(120);
            e.Property(x => x.ContactName).HasMaxLength(120);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.ExtraEmails).HasMaxLength(1000);
        });
        m.Entity<AccountantDelivery>(e =>
        {
            e.ToTable("accountant_deliveries");
            e.HasIndex(x => new { x.TenantId, x.Year, x.Month, x.Trigger });
            e.Property(x => x.Trigger).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.Recipients).HasMaxLength(1500);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.Property(x => x.EmailMessageIds).HasMaxLength(1000);
        });
        m.Entity<SyncEvent>(e => { e.ToTable("sync_events"); e.HasIndex(x => new { x.TenantId, x.DeviceId, x.IdempotencyKey }).IsUnique(); });
        m.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.HasIndex(x => x.PushStatus);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Body).HasMaxLength(1000);
        });
        m.Entity<PushToken>(e =>
        {
            e.ToTable("push_tokens");
            e.HasIndex(x => new { x.TenantId, x.Token }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.UserId });
            e.Property(x => x.Token).HasMaxLength(512);
        });
        m.Entity<Product>(e => { e.Property(x => x.ImageKey).HasMaxLength(300); e.Property(x => x.ImageContentType).HasMaxLength(40); });

        m.Entity<Supplier>(e =>
        {
            e.ToTable("suppliers");
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.Name });
            e.Property(x => x.Name).HasMaxLength(200);
        });
        m.Entity<PurchaseOrder>(e =>
        {
            e.ToTable("purchase_orders");
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.TenantId, x.SupplierId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Store>().WithMany().HasForeignKey(x => new { x.TenantId, x.StoreId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => new { i.TenantId, i.PurchaseOrderId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<PurchaseItem>(e =>
        {
            e.ToTable("purchase_items");
            e.HasOne<Product>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProductId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<PurchaseReceipt>(e => { e.ToTable("purchase_receipts"); e.HasIndex(x => new { x.TenantId, x.PurchaseOrderId }); });
        m.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.Document }).IsUnique().HasFilter("\"Document\" IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.Name });
            e.Property(x => x.Name).HasMaxLength(200);
        });
        m.Entity<LoyaltyProgram>(e => { e.ToTable("loyalty_programs"); e.HasKey(x => x.TenantId); });
        m.Entity<LoyaltyTransaction>(e =>
        {
            e.ToTable("loyalty_transactions");
            e.HasIndex(x => new { x.TenantId, x.UniqueKey }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CustomerId });
            e.HasOne<Customer>().WithMany().HasForeignKey(x => new { x.TenantId, x.CustomerId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<FinancialTitle>(e =>
        {
            e.ToTable("financial_titles");
            e.HasIndex(x => new { x.TenantId, x.Kind, x.Status, x.DueDate });
            e.HasIndex(x => new { x.TenantId, x.GroupId });
            e.Property(x => x.Description).HasMaxLength(200);
        });
        m.Entity<SaleReturn>(e =>
        {
            e.ToTable("sale_returns");
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.SaleId });
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.HasOne<Sale>().WithMany().HasForeignKey(x => new { x.TenantId, x.SaleId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => new { i.TenantId, i.SaleReturnId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<SaleReturnItem>(e => e.ToTable("sale_return_items"));
        m.Entity<Sale>(e => e.HasOne<Customer>().WithMany().HasForeignKey(x => new { x.TenantId, x.CustomerId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict));

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

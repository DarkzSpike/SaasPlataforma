using System.Threading.RateLimiting;
using Comercial.Api;
using Comercial.Api.Endpoints;
using Comercial.Api.Security;
using Comercial.Api.Services;
using Comercial.Infrastructure;
using Comercial.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 1 * 1024 * 1024; // uploads têm limite próprio
    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    k.AddServerHeader = false;
});

builder.Services.AddInfrastructure(config);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<KeyMaterial>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddHttpClient<FocusNfeProvider>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddScoped<IFiscalProvider>(sp => sp.GetRequiredService<FocusNfeProvider>());
// Emissão direta na SEFAZ (sem provedor pago), com o certificado A1 da loja.
builder.Services.AddSingleton<ISefazTransport, UnimakeSefazTransport>();
builder.Services.AddScoped<SefazNfceProvider>();
builder.Services.AddScoped<IFiscalProvider>(sp => sp.GetRequiredService<SefazNfceProvider>());
builder.Services.AddHttpClient<INfseProvider, FocusNfseProvider>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddScoped<RequestActor>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<LicenseService>();
builder.Services.AddScoped<FeatureAuthorization>();
builder.Services.AddScoped<ScopedLookup>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<StockService>();
builder.Services.AddScoped<CashService>();
builder.Services.AddScoped<SalesService>();
builder.Services.AddScoped<OperatingService>();
builder.Services.AddScoped<FiscalService>();
builder.Services.AddScoped<FiscalArchiveService>();
builder.Services.AddScoped<DanfeService>();
builder.Services.AddScoped<AccountingReportService>();
builder.Services.AddScoped<MonthClosingService>();
builder.Services.AddScoped<FiscalExportService>();
builder.Services.AddScoped<AccountantService>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<ModuleAdminService>();
builder.Services.AddScoped<TenantProvisioningService>();
builder.Services.AddScoped<BrandingService>();
builder.Services.AddScoped<SupportService>();
builder.Services.AddScoped<PaymentWebhookService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<StockAlertService>();
builder.Services.AddScoped<PurchasingService>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<LoyaltyService>();
builder.Services.AddScoped<FinanceService>();
builder.Services.AddScoped<ReturnService>();
builder.Services.AddScoped<ProductImageService>();
builder.Services.AddScoped<TenantStorageService>();
builder.Services.AddScoped<PlatformSettingsService>();
builder.Services.AddScoped<BillingService>();
builder.Services.AddScoped<DiscountAuthorizationService>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<EmailSender>();
builder.Services.AddScoped<PasswordResetService>();
builder.Services.AddScoped<StorePixService>();
builder.Services.AddScoped<WhatsAppService>();
builder.Services.AddScoped<NfseService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<PromotionService>();
builder.Services.AddScoped<QuoteService>();
builder.Services.AddScoped<CreditService>();
builder.Services.AddScoped<NfeImportService>();
builder.Services.AddScoped<ProductCsvImportService>();
builder.Services.AddScoped<BatchService>();
builder.Services.AddScoped<TransferService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<SignupService>();
builder.Services.AddScoped<DesktopReleaseService>();
builder.Services.AddHttpClient<IGoogleOAuthClient, GoogleOAuthClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddScoped<GoogleOAuthService>();
builder.Services.AddScoped<UserTwoFactorService>();
builder.Services.AddHostedService<ExpiryJob>();
builder.Services.AddHostedService<FiscalJob>();
builder.Services.AddHostedService<AccountingJob>();
builder.Services.AddHostedService<NfseJob>();
builder.Services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
builder.Services.AddHostedService<EmailJob>();
builder.Services.AddHttpClient<IMercadoPagoClient, MercadoPagoClient>(c => { c.BaseAddress = new Uri("https://api.mercadopago.com"); c.Timeout = TimeSpan.FromSeconds(20); });
builder.Services.AddHostedService<BillingJob>();
// WhatsApp da loja (API oficial da Meta): cobrança do fiado no dia escolhido e campanhas com limite anti-spam.
builder.Services.AddHttpClient<IWhatsAppClient, WhatsAppCloudClient>(c =>
{
    c.BaseAddress = new Uri($"https://graph.facebook.com/{builder.Configuration["WhatsApp:GraphVersion"] ?? "v23.0"}/");
    c.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddHostedService<WhatsAppJob>();
builder.Services.AddSingleton<IDriveGateway, GoogleDriveGateway>();
builder.Services.AddSingleton<IProductImageStore, LocalProductImageStore>();
if (config["ProductImages:Provider"] == "GoogleDrive")
    builder.Services.AddSingleton<IProductImageStore, GoogleDriveProductImageStore>();
builder.Services.AddHttpClient("fcm", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<FcmPushSender>();
builder.Services.AddSingleton<IPushSender>(sp =>
{
    var fcm = sp.GetRequiredService<FcmPushSender>();
    return fcm.Configured ? fcm : new NotConfiguredPushSender();
});
builder.Services.AddHostedService<PushRetryService>();

var dp = builder.Services.AddDataProtection().SetApplicationName("comercial-saas");
if (config["DataProtection:KeysPath"] is { Length: > 0 } keysPath) dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// Dois esquemas independentes: tokens de loja não valem no painel e vice-versa.
builder.Services.AddAuthentication()
    .AddJwtBearer(AuthSchemes.Tenant, _ => { })
    .AddJwtBearer(AuthSchemes.Admin, _ => { });
builder.Services.AddOptions<JwtBearerOptions>(AuthSchemes.Tenant).Configure<KeyMaterial>((o, k) =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = TokenService.Validation(TokenService.TenantAudience, k.TenantJwtKey);
});
builder.Services.AddOptions<JwtBearerOptions>(AuthSchemes.Admin).Configure<KeyMaterial>((o, k) =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = TokenService.Validation(TokenService.AdminAudience, k.AdminJwtKey);
});
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    var perMinute = config.GetValue("RateLimit:AuthPerMinute", 10);
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("webhook", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimit:WebhookPerMinute", 120), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Request.Headers.Authorization.ToString() is { Length: > 20 } a ? a[^20..] : ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimit:GlobalPerMinute", 600), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddCors(o => o.AddPolicy("admin", p => p
    .WithOrigins(config.GetSection("Cors:AdminOrigins").Get<string[]>() ?? [])
    .WithHeaders("Authorization", "Content-Type", AdminEndpoints.ReauthHeader, ActorResolutionMiddleware.SupportHeader)
    .WithMethods("GET", "POST", "PUT")));

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.MaxDepth = 32);

var app = builder.Build();

// Atrás do proxy HTTPS (Caddy/Nginx): IP real do cliente (limites por IP) e esquema https. Só confia nas redes informadas.
if (config.GetValue("Proxy:Enabled", false))
{
    var fo = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1,
    };
    fo.KnownIPNetworks.Clear(); fo.KnownProxies.Clear();
    foreach (var n in config.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? []) fo.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(n));
    app.UseForwardedHeaders(fo);
}

app.UseMiddleware<ErrorHandlingMiddleware>();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h.XFrameOptions = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    h.CacheControl = "no-store";
    await next();
});
if (!app.Environment.IsDevelopment() && app.Environment.EnvironmentName != "Testing")
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseCors("admin");
app.UseRateLimiter();
app.UseMiddleware<ActorResolutionMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Retorno do Google depois de "Conectar minha conta Google" (sem login: a segurança está no state assinado e de uso único).
app.MapGet(GoogleOAuthService.CallbackPath, async (string? code, string? state, string? error, GoogleOAuthService s, CancellationToken ct) =>
{
    var (ok, message) = await s.CallbackAsync(code, state, error, ct);
    return Results.Content(GoogleOAuthService.Page(ok, message), "text/html; charset=utf-8", statusCode: ok ? 200 : 400);
}).RequireRateLimiting("auth");

// Arquivos públicos para download (APK do app). Só nomes simples terminados em .apk, de uma pasta própria.
app.MapGet("/downloads/{name}", (string name, IConfiguration cfg, IHostEnvironment env) =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9._-]{1,80}\.apk$")) return Results.NotFound();
    var path = Path.Combine(Comercial.Api.Services.DownloadsFolder.Dir(cfg, env), name); // Storage:Root pode ser relativo (dev-data/storage)
    return File.Exists(path) ? Results.File(path, "application/vnd.android.package-archive", name) : Results.NotFound();
});
// Avisos do WhatsApp da loja (Meta): confirmação do endereço e descadastro de quem responde SAIR. Assinado com a chave do app.
app.MapGet("/webhooks/whatsapp/{tenantId:guid}", async (Guid tenantId, HttpRequest req, TenantContext tenant, RequestActor actor, WhatsAppService s) =>
{
    tenant.SetTenant(tenantId); actor.TenantId = tenantId;
    var challenge = await s.VerifyWebhookAsync(req.Query["hub.mode"], req.Query["hub.verify_token"], req.Query["hub.challenge"]);
    return challenge is null ? Results.StatusCode(403) : Results.Text(challenge);
}).RequireRateLimiting("webhook");
app.MapPost("/webhooks/whatsapp/{tenantId:guid}", async (Guid tenantId, HttpRequest req, TenantContext tenant, RequestActor actor, WhatsAppService s) =>
{
    tenant.SetTenant(tenantId); actor.TenantId = tenantId;
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    return await s.HandleWebhookAsync(body, req.Headers["X-Hub-Signature-256"]) ? Results.Ok() : Results.StatusCode(401);
}).RequireRateLimiting("webhook");
app.MapTenantApi();
app.MapBusinessApi();
app.MapFiscalAccountingApi();
app.MapAdminApi();
// Mercado Pago: valida x-signature e confirma o pagamento consultando a API do Mercado Pago (nunca confia só no aviso).
app.MapPost("/webhooks/mercadopago", async (HttpRequest req, BillingService s, TenantContext tenant, CancellationToken ct) =>
{
    tenant.SetPlatformScope();
    string? dataId = req.Query["data.id"], type = req.Query["type"];
    if (string.IsNullOrEmpty(dataId))
    {
        try
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            dataId = doc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("id", out var id) ? id.ToString() : null;
            type ??= doc.RootElement.TryGetProperty("type", out var tp) ? tp.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { }
    }
    var (status, result) = await s.HandleWebhookAsync(type, dataId, req.Headers["x-signature"], req.Headers["x-request-id"], ct);
    return Results.Json(new { result }, statusCode: status);
}).RequireRateLimiting("webhook");

app.MapPost("/webhooks/payments/{provider}", async (string provider, HttpRequest req, PaymentWebhookService s, TenantContext tenant) =>
{
    tenant.SetPlatformScope(); // webhook autenticado por assinatura HMAC; localiza a fatura de qualquer empresa
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    var (status, result) = await s.HandleAsync(provider, body, req.Headers["X-Webhook-Timestamp"], req.Headers["X-Webhook-Signature"]);
    return Results.Json(new { result }, statusCode: status);
});

await DatabaseInitializer.InitializeAsync(app.Services, app.Environment);
app.Run();

public partial class Program;

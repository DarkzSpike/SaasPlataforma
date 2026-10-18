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
builder.Services.AddSingleton<IFiscalProvider, NotConfiguredFiscalProvider>();
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
app.MapTenantApi();
app.MapBusinessApi();
app.MapAdminApi();
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

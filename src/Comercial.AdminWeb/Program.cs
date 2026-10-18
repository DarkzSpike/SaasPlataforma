// Painel do desenvolvedor: aplicação web independente (somente arquivos estáticos).
// Toda autorização acontece na API; este host só serve a interface com cabeçalhos de segurança.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);
var app = builder.Build();

var apiBase = app.Configuration["ApiBaseUrl"] ?? "http://localhost:5080";

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.ContentSecurityPolicy = $"default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; connect-src 'self' {apiBase}; font-src 'self'; form-action 'none'; frame-ancestors 'none'; base-uri 'none'";
    h.XContentTypeOptions = "nosniff";
    h.XFrameOptions = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});
if (!app.Environment.IsDevelopment()) { app.UseHsts(); app.UseHttpsRedirection(); }

app.MapGet("/config.json", () => Results.Json(new { apiBaseUrl = apiBase }));
app.UseDefaultFiles();
app.UseStaticFiles();
app.Run();

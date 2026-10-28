using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record GoogleOAuthSettingsRequest(string? ClientId, string? ClientSecret);
public sealed record GoogleTokens(string? RefreshToken, string AccessToken);

/// <summary>Conversa com o Google (trocar o código por tokens e ler o e-mail). Substituível nos testes.</summary>
public interface IGoogleOAuthClient
{
    Task<GoogleTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct);
    Task<string> GetEmailAsync(string accessToken, CancellationToken ct);
}

public sealed class GoogleOAuthClient(HttpClient http) : IGoogleOAuthClient
{
    public async Task<GoogleTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct)
    {
        using var res = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code, ["client_id"] = clientId, ["client_secret"] = clientSecret, ["redirect_uri"] = redirectUri, ["grant_type"] = "authorization_code",
        }), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new DomainException("google_oauth_failed", "O Google recusou a autorização (confira o Client ID, o segredo e o endereço de retorno).", 502);
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        return new GoogleTokens(r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null, r.GetProperty("access_token").GetString()!);
    }

    public async Task<string> GetEmailAsync(string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return "conta Google";
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() ?? "conta Google" : "conta Google";
    }
}

/// <summary>
/// "Conectar minha conta Google": o administrador autoriza no Google e as fotos da empresa passam a ser salvas numa
/// pasta criada no Drive pessoal dele. Escopo drive.file: o sistema só enxerga os arquivos e pastas que ele mesmo criou.
/// O "state" é assinado, vale 15 minutos e só pode ser usado uma vez.
/// </summary>
public sealed class GoogleOAuthService(PlatformSettingsService settings, IDataProtectionProvider dp, IGoogleOAuthClient google, IDriveGateway drive,
    TenantStorageService storage, AppDbContext db, TenantContext tenantCtx, RequestActor actor)
{
    public const string CallbackPath = "/oauth/google/callback";
    private static readonly ConcurrentDictionary<string, DateTimeOffset> UsedStates = new();
    private ITimeLimitedDataProtector StateProtector => dp.CreateProtector("Comercial.GoogleOAuth.State.v1").ToTimeLimitedDataProtector();

    public static string RedirectUri(HttpRequest req) => $"{req.Scheme}://{req.Host}{CallbackPath}";

    public async Task<object> SettingsViewAsync(HttpRequest req)
    {
        var c = await settings.GoogleOAuthAsync();
        return new { configured = c is not null, clientId = c?.ClientId, hasSecret = c?.ClientSecret is not null, redirectUri = RedirectUri(req) };
    }

    /// <summary>Monta o endereço de autorização do Google para conectar a conta à empresa.</summary>
    public async Task<string> StartAsync(Guid tenantId, HttpRequest req)
    {
        var c = await settings.GoogleOAuthAsync() ?? throw new DomainException("google_oauth_not_configured", "Configure primeiro o app do Google (aba Google) com o Client ID e o segredo.", 409);
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId)) throw new DomainException("not_found", "Empresa não encontrada.", 404);
        var redirect = RedirectUri(req);
        var state = StateProtector.Protect(JsonSerializer.Serialize(new StatePayload(tenantId, actor.AdminId, redirect, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)))), TimeSpan.FromMinutes(15));
        var q = new Dictionary<string, string>
        {
            ["client_id"] = c.ClientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = "openid email https://www.googleapis.com/auth/drive.file",
            ["access_type"] = "offline", ["prompt"] = "consent", ["include_granted_scopes"] = "true", ["state"] = state,
        };
        return "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    private sealed record StatePayload(Guid TenantId, Guid? AdminId, string RedirectUri, string Nonce);

    /// <summary>Retorno do Google: valida o state, troca o código, cria a pasta e salva a configuração da empresa.</summary>
    public async Task<(bool Ok, string Message)> CallbackAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error)) return (false, error == "access_denied" ? "Autorização cancelada no Google. Nada foi alterado." : "O Google devolveu um erro: " + error);
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state)) return (false, "Retorno do Google incompleto.");
        StatePayload p;
        try { p = JsonSerializer.Deserialize<StatePayload>(StateProtector.Unprotect(state))!; }
        catch (CryptographicException) { return (false, "Link expirado ou inválido. Volte ao painel e clique em \"Conectar minha conta Google\" de novo."); }
        foreach (var old in UsedStates.Where(x => x.Value < DateTimeOffset.UtcNow.AddMinutes(-20)).Select(x => x.Key).ToList()) UsedStates.TryRemove(old, out _);
        if (!UsedStates.TryAdd(p.Nonce, DateTimeOffset.UtcNow)) return (false, "Este link de autorização já foi usado.");

        tenantCtx.SetPlatformScope(); // retorno anônimo do Google: a gravação é da plataforma (o state assinado diz qual empresa)
        var c = await settings.GoogleOAuthAsync() ?? throw new DomainException("google_oauth_not_configured", "App do Google não configurado.", 409);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == p.TenantId, ct);
        if (tenant is null) return (false, "Empresa não encontrada.");
        var tokens = await google.ExchangeCodeAsync(c.ClientId, c.ClientSecret, code, p.RedirectUri, ct);
        if (string.IsNullOrEmpty(tokens.RefreshToken))
            return (false, "O Google não enviou a permissão permanente. Remova o acesso do app em myaccount.google.com/permissions e conecte de novo.");
        var email = await google.GetEmailAsync(tokens.AccessToken, ct);
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "authorized_user", ["client_id"] = c.ClientId, ["client_secret"] = c.ClientSecret, ["refresh_token"] = tokens.RefreshToken, ["email"] = email,
        });
        string folder;
        try { folder = await drive.CreateFolderAsync(json, $"Sistema Comercial - {tenant.TradeName} ({tenant.Code})", ct); }
        catch (Exception ex) when (ex is not OperationCanceledException and not DomainException)
        { return (false, "A conta foi autorizada, mas não foi possível criar a pasta no Google Drive. Confira se a Google Drive API está ativada no projeto."); }
        await storage.ConnectGoogleAccountAsync(tenant.Id, p.AdminId, json, email, folder);
        return (true, $"Pronto! As fotos de {tenant.TradeName} agora vão para a pasta \"Sistema Comercial - {tenant.TradeName} ({tenant.Code})\" no Google Drive de {email}.");
    }

    /// <summary>Página simples mostrada depois do retorno do Google (sem scripts).</summary>
    public static string Page(bool ok, string message) =>
        "<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>Google Drive</title></head><body>" +
        $"<h2>{(ok ? "Conta Google conectada" : "Não foi possível conectar")}</h2><p>{Html(message)}</p>" +
        "<p>Pode fechar esta aba e voltar ao painel (clique em Atualizar na empresa).</p></body></html>";

    /// <summary>Escapa só o necessário (a página é UTF-8: acentos ficam legíveis).</summary>
    private static string Html(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}

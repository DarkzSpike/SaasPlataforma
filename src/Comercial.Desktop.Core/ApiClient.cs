using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Comercial.Shared.Contracts;

namespace Comercial.Desktop.Core;

public sealed class ApiException(int status, string code) : Exception($"{status} {code}")
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Cliente HTTP da API de loja. Nunca envia tenant_id: o servidor deriva do token.</summary>
public sealed class ApiClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? _access;
    public string? RefreshToken { get; private set; }

    public async Task LoginAsync(string tenantCode, string email, string password, Guid? deviceId)
    {
        var t = await Send<TokenResponse>(HttpMethod.Post, "/api/v1/auth/login", new LoginRequest(tenantCode, email, password, deviceId), auth: false);
        _access = t.AccessToken; RefreshToken = t.RefreshToken;
    }

    public async Task RefreshAsync()
    {
        var t = await Send<TokenResponse>(HttpMethod.Post, "/api/v1/auth/refresh", new RefreshRequest(RefreshToken!), auth: false);
        _access = t.AccessToken; RefreshToken = t.RefreshToken;
    }

    public void UseTokens(string access, string refresh) { _access = access; RefreshToken = refresh; }

    public async Task<string> GetLicenseGrantAsync() => (await Send<JsonElement>(HttpMethod.Get, "/api/v1/license/grant")).GetProperty("grant").GetString()!;
    public Task<SyncBatchResponse> SyncAsync(SyncBatchRequest r) => Send<SyncBatchResponse>(HttpMethod.Post, "/api/v1/sync/batch", r);
    public Task<JsonElement> GetAsync(string path) => Send<JsonElement>(HttpMethod.Get, path);
    public Task<JsonElement> PostAsync(string path, object body) => Send<JsonElement>(HttpMethod.Post, path, body);

    private async Task<T> Send<T>(HttpMethod method, string path, object? body = null, bool auth = true)
    {
        using var req = new HttpRequestMessage(method, path);
        if (auth && _access is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _access);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        using var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            var code = "http_error";
            try { code = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString() ?? code; } catch { /* corpo não JSON */ }
            throw new ApiException((int)res.StatusCode, code);
        }
        if (res.StatusCode == System.Net.HttpStatusCode.NoContent) return default!;
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }
}

/// <summary>Armazena o refresh token protegido pela DPAPI do Windows (escopo do usuário).</summary>
[SupportedOSPlatform("windows")]
public static class TokenVault
{
    private static readonly byte[] Entropy = "comercial-desktop-v1"u8.ToArray();

    public static void Save(string path, string refreshToken) =>
        File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken), Entropy, DataProtectionScope.CurrentUser));

    public static string? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }
}

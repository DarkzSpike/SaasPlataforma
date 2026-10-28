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

    public async Task LoginAsync(string tenantCode, string email, string password, Guid? deviceId, string? totpCode = null)
    {
        var t = await Send<TokenResponse>(HttpMethod.Post, "/api/v1/auth/login", new LoginRequest(tenantCode, email, password, deviceId,
            string.IsNullOrWhiteSpace(totpCode) ? null : totpCode.Trim()), auth: false);
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
    public Task<JsonElement> PutAsync(string path, object body) => Send<JsonElement>(HttpMethod.Put, path, body);

    /// <summary>Chamada sem login (ex.: "esqueci minha senha"); ignora o corpo da resposta.</summary>
    public async Task PostAnonymousAsync(string path, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        using var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode, await CodeOf(res));
    }

    /// <summary>POST sem login que devolve JSON (cadastro com teste grátis).</summary>
    public async Task<JsonElement> PostAnonymousJsonAsync(string path, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        using var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode, await CodeOf(res));
        return await res.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    /// <summary>Envia um arquivo (multipart, campo "file"). O servidor valida o conteúdo real.</summary>
    public async Task<JsonElement> UploadAsync(string path, byte[] bytes, string fileName, string contentType, IReadOnlyDictionary<string, string>? fields = null)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (k, v) in fields ?? new Dictionary<string, string>()) form.Add(new StringContent(v), k);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        if (_access is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _access);
        using var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode, await CodeOf(res));
        return await res.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    /// <summary>Baixa bytes (ex.: foto do produto). Retorna null se não existir.</summary>
    public async Task<byte[]?> GetBytesAsync(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (_access is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _access);
        using var res = await http.SendAsync(req);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode, await CodeOf(res));
        return await res.Content.ReadAsByteArrayAsync();
    }

    private static async Task<string> CodeOf(HttpResponseMessage res)
    {
        try { return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString() ?? "http_error"; }
        catch { return "http_error"; }
    }

    private async Task<T> Send<T>(HttpMethod method, string path, object? body = null, bool auth = true)
    {
        using var req = new HttpRequestMessage(method, path);
        if (auth && _access is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _access);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        using var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode, await CodeOf(res));
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

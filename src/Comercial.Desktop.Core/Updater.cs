using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace Comercial.Desktop.Core;

/// <summary>Versão do caixa publicada no painel. A assinatura é feita com a mesma chave das licenças.</summary>
public sealed record UpdateInfo(string Version, string Url, string Sha256, string? Notes, bool Mandatory, string Signature);

/// <summary>
/// Atualização automática segura: o caixa só instala um arquivo se (1) os dados da versão estiverem assinados pela
/// chave do servidor, (2) o endereço for HTTPS e (3) o SHA-256 do arquivo baixado bater com o assinado.
/// Assim, nem um servidor de arquivos invadido nem uma rede adulterada conseguem instalar outro programa.
/// </summary>
public static class Updater
{
    /// <summary>Texto assinado (mesma regra do servidor).</summary>
    public static string Canonical(string version, string url, string sha256, bool mandatory) =>
        $"comercial-desktop|{version}|{url}|{sha256.ToLowerInvariant()}|{(mandatory ? 1 : 0)}";

    public static bool VerifySignature(UpdateInfo u, string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(Encoding.UTF8.GetBytes(Canonical(u.Version, u.Url, u.Sha256, u.Mandatory)), Convert.FromBase64String(u.Signature), HashAlgorithmName.SHA256);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    public static bool IsNewer(string latest, Version current) => System.Version.TryParse(latest, out var v) && Normalize(v) > Normalize(current);

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public static bool AllowedUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.IsLoopback);

    /// <summary>Consulta a última versão (sem login). Devolve null se não houver versão nova, válida e assinada.</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, Version current, string publicKeyPem, CancellationToken ct = default)
    {
        using var res = await http.GetAsync("/api/v1/public/desktop/latest", ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NoContent || !res.IsSuccessStatusCode) return null;
        var u = await res.Content.ReadFromJsonAsync<UpdateInfo>(ApiClient.Json, ct);
        if (u is null || !IsNewer(u.Version, current) || !AllowedUrl(u.Url) || !VerifySignature(u, publicKeyPem)) return null;
        return u;
    }

    /// <summary>Baixa o instalador para a pasta indicada e confere o SHA-256. Arquivo diferente é apagado.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo u, string dir, CancellationToken ct = default)
    {
        if (!AllowedUrl(u.Url)) throw new InvalidOperationException("Endereço de atualização não permitido.");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"Comercial-{u.Version}-setup.exe");
        using (var res = await http.GetAsync(u.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            await using var f = File.Create(path);
            await res.Content.CopyToAsync(f, ct);
        }
        string hash;
        await using (var f = File.OpenRead(path)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
        if (!string.Equals(hash, u.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException("O arquivo baixado não confere com a versão publicada (SHA-256 diferente). Atualização cancelada.");
        }
        return path;
    }
}

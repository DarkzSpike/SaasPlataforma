using System.Security.Cryptography;
using System.Text;
using Comercial.Api.Security;

namespace Comercial.Api.Services;

public sealed record MobileReleaseRequest(int VersionCode, string? VersionName, string? Url, string? Notes, bool Mandatory);
public sealed record SupportContactRequest(string? Whatsapp, string? Message);
public sealed record DesktopReleaseRequest(string? Version, string? Url, string? Sha256, string? Notes, bool Mandatory);

/// <summary>
/// Publica a versão atual do caixa para a atualização automática. Os dados são assinados com a chave das licenças
/// (o caixa já confia nela), e o caixa ainda confere o SHA-256 do instalador baixado.
/// </summary>
public sealed class DesktopReleaseService(PlatformSettingsService settings, KeyMaterial keys)
{
    public static string Canonical(string version, string url, string sha256, bool mandatory) =>
        $"comercial-desktop|{version}|{url}|{sha256.ToLowerInvariant()}|{(mandatory ? 1 : 0)}";

    public async Task<object?> LatestAsync()
    {
        var r = await settings.DesktopReleaseAsync();
        if (r is null) return null;
        var sig = keys.LicenseSigningKey.SignData(Encoding.UTF8.GetBytes(Canonical(r.Version, r.Url, r.Sha256, r.Mandatory)), HashAlgorithmName.SHA256);
        return new { version = r.Version, url = r.Url, sha256 = r.Sha256, notes = r.Notes, mandatory = r.Mandatory, signature = Convert.ToBase64String(sig) };
    }
}

/// <summary>
/// Pasta de downloads do próprio servidor (APK do app). O dev/build-apk.ps1 copia o APK para lá e grava o
/// mobile-latest.json: assim o app avisa a versão nova mesmo sem publicar pelo painel admin.
/// </summary>
public static class DownloadsFolder
{
    public static string Dir(IConfiguration cfg, IHostEnvironment env) =>
        Path.GetFullPath(Path.Combine(env.ContentRootPath,
            cfg["Downloads:Root"] ?? Path.Combine(cfg["Storage:Root"] ?? Path.Combine(env.ContentRootPath, "dev-data"), "downloads")));

    private sealed record Manifest(int VersionCode, string? VersionName, string? File, string? Notes, bool Mandatory);

    /// <summary>APK descrito no mobile-latest.json, com endereço relativo (o app baixa do servidor em que está conectado).</summary>
    public static PlatformSettingsService.MobileRelease? LatestApk(IConfiguration cfg, IHostEnvironment env)
    {
        var dir = Dir(cfg, env);
        var path = Path.Combine(dir, "mobile-latest.json");
        if (!File.Exists(path)) return null;
        try
        {
            var m = System.Text.Json.JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (m is null || m.VersionCode < 1 || m.File is null || !System.Text.RegularExpressions.Regex.IsMatch(m.File, @"^[A-Za-z0-9._-]{1,80}\.apk$")
                || !File.Exists(Path.Combine(dir, m.File))) return null;
            return new(m.VersionCode, m.VersionName ?? m.VersionCode.ToString(), "/downloads/" + m.File, string.IsNullOrWhiteSpace(m.Notes) ? null : m.Notes, m.Mandatory);
        }
        catch (System.Text.Json.JsonException) { return null; }
        catch (IOException) { return null; }
    }
}

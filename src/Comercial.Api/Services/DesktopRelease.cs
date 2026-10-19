using System.Security.Cryptography;
using System.Text;
using Comercial.Api.Security;

namespace Comercial.Api.Services;

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

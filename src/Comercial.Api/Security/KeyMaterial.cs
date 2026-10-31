using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Comercial.Api.Security;

/// <summary>
/// Chaves do servidor, lidas de configuração/segredos (variáveis de ambiente, cofre de segredos).
/// Em Development, se ausentes, são geradas em memória (efêmeras) com aviso no log.
/// Em qualquer outro ambiente a ausência impede a inicialização.
/// </summary>
public sealed class KeyMaterial
{
    public SymmetricSecurityKey TenantJwtKey { get; }
    public SymmetricSecurityKey AdminJwtKey { get; }
    public ECDsa LicenseSigningKey { get; }
    public string LicenseKeyId { get; }
    public string LicensePublicKeyPem { get; }

    public KeyMaterial(IConfiguration config, IHostEnvironment env, ILogger<KeyMaterial> log)
    {
        var allowEphemeral = env.IsDevelopment() || env.EnvironmentName == "Testing";
        // Desenvolvimento: chaves "efêmeras" gravadas num arquivo local (fora do git), para que reiniciar a API
        // não derrube o login dos caixas/app nem invalide as licenças já emitidas.
        if (env.IsDevelopment() && config["Dev:KeysFile"] is { Length: > 0 } devFile) DevKeys.Fill(config, devFile);
        TenantJwtKey = new SymmetricSecurityKey(ReadKey(config["Jwt:TenantSigningKey"], "Jwt:TenantSigningKey", allowEphemeral, log));
        AdminJwtKey = new SymmetricSecurityKey(ReadKey(config["Jwt:AdminSigningKey"], "Jwt:AdminSigningKey", allowEphemeral, log));

        LicenseSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = config["License:PrivateKeyPem"];
        // Em contêiner a chave PEM (várias linhas) vem de um arquivo de segredo montado.
        if (string.IsNullOrWhiteSpace(pem) && config["License:PrivateKeyPemFile"] is { Length: > 0 } pemFile) pem = File.ReadAllText(pemFile);
        if (!string.IsNullOrWhiteSpace(pem)) LicenseSigningKey.ImportFromPem(pem);
        else if (!allowEphemeral) throw new InvalidOperationException("License:PrivateKeyPem não configurada.");
        else log.LogWarning("Chave de licença efêmera gerada (somente desenvolvimento).");

        LicensePublicKeyPem = LicenseSigningKey.ExportSubjectPublicKeyInfoPem();
        LicenseKeyId = Hashing.Sha256Hex(LicenseSigningKey.ExportSubjectPublicKeyInfo())[..16];
    }

    private static byte[] ReadKey(string? b64, string name, bool allowEphemeral, ILogger log)
    {
        if (!string.IsNullOrWhiteSpace(b64))
        {
            var k = Convert.FromBase64String(b64);
            if (k.Length < 32) throw new InvalidOperationException($"{name} deve ter pelo menos 256 bits.");
            return k;
        }
        if (!allowEphemeral) throw new InvalidOperationException($"{name} não configurada.");
        log.LogWarning("{Name} efêmera gerada (somente desenvolvimento).", name);
        return RandomNumberGenerator.GetBytes(32);
    }
}

/// <summary>Gera uma vez e reaproveita as chaves de desenvolvimento (somente ambiente Development).</summary>
internal static class DevKeys
{
    private sealed record Saved(string Tenant, string Admin, string LicensePem);

    public static void Fill(IConfiguration config, string path)
    {
        Saved? s = null;
        try { if (File.Exists(path)) s = System.Text.Json.JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)); }
        catch (System.Text.Json.JsonException) { }
        if (s is null)
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            s = new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), ec.ExportPkcs8PrivateKeyPem());
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(s));
        }
        if (string.IsNullOrWhiteSpace(config["Jwt:TenantSigningKey"])) config["Jwt:TenantSigningKey"] = s.Tenant;
        if (string.IsNullOrWhiteSpace(config["Jwt:AdminSigningKey"])) config["Jwt:AdminSigningKey"] = s.Admin;
        if (string.IsNullOrWhiteSpace(config["License:PrivateKeyPem"]) && string.IsNullOrWhiteSpace(config["License:PrivateKeyPemFile"])) config["License:PrivateKeyPem"] = s.LicensePem;
    }
}

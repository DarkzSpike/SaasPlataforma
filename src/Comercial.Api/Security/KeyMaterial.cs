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
        TenantJwtKey = new SymmetricSecurityKey(ReadKey(config["Jwt:TenantSigningKey"], "Jwt:TenantSigningKey", allowEphemeral, log));
        AdminJwtKey = new SymmetricSecurityKey(ReadKey(config["Jwt:AdminSigningKey"], "Jwt:AdminSigningKey", allowEphemeral, log));

        LicenseSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = config["License:PrivateKeyPem"];
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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Comercial.Shared.Licensing;

public static class LicenseStates
{
    public const string Active = "active";
    /// <summary>Ativa, mas o período termina em breve.</summary>
    public const string Expiring = "expiring";
    /// <summary>Pagamento em atraso, dentro da tolerância — operação normal com aviso.</summary>
    public const string Grace = "grace";
    /// <summary>Atraso após tolerância — bloqueia novas operações comerciais, mantém consulta/exportação.</summary>
    public const string Restricted = "restricted";
    public const string Suspended = "suspended";
    public const string Cancelled = "cancelled";

    public static bool AllowsNewOperations(string state) => state is Active or Expiring or Grace;
}

/// <summary>
/// Concessão de licença emitida pelo servidor e assinada com ECDSA P-256.
/// O cliente possui apenas a chave pública — alterar o banco local não altera módulos.
/// </summary>
public sealed record LicenseGrant
{
    public Guid TenantId { get; init; }
    public string TenantCode { get; init; } = "";
    public Guid DeviceId { get; init; }
    public string State { get; init; } = "";
    public string[] Modules { get; init; } = [];
    public DateTimeOffset IssuedAt { get; init; }
    /// <summary>Validade da licença offline — independente do prazo de tolerância de pagamento.</summary>
    public DateTimeOffset OfflineValidUntil { get; init; }
    public DateTimeOffset SubscriptionPeriodEnd { get; init; }
    public int BrandingVersion { get; init; }
    public string KeyId { get; init; } = "";
}

public static class LicenseGrantCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Sign(LicenseGrant grant, ECDsa privateKey)
    {
        var payload = B64Url(JsonSerializer.SerializeToUtf8Bytes(grant, Json));
        var sig = privateKey.SignData(Encoding.ASCII.GetBytes(payload), HashAlgorithmName.SHA256);
        return payload + "." + B64Url(sig);
    }

    /// <summary>Verifica assinatura e decodifica. Retorna null se inválido ou adulterado.</summary>
    public static LicenseGrant? Verify(string token, ECDsa publicKey)
    {
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        try
        {
            var sig = FromB64Url(parts[1]);
            if (!publicKey.VerifyData(Encoding.ASCII.GetBytes(parts[0]), sig, HashAlgorithmName.SHA256)) return null;
            return JsonSerializer.Deserialize<LicenseGrant>(FromB64Url(parts[0]), Json);
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
        catch (CryptographicException) { return null; }
    }

    public static string B64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t + new string('=', (4 - t.Length % 4) % 4));
    }
}

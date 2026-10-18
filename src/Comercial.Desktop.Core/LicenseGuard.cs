using System.Globalization;
using System.Security.Cryptography;
using Comercial.Shared.Licensing;

namespace Comercial.Desktop.Core;

public sealed record LicenseCheck(bool CanOperate, string Reason, LicenseGrant? Grant)
{
    public bool HasModule(string key) => CanOperate && Grant is not null && Grant.Modules.Contains(key);
}

/// <summary>
/// Validação offline da licença: assinatura ECDSA (chave pública embarcada/configurada),
/// vínculo com o dispositivo, validade offline e detecção de relógio retrocedido.
/// </summary>
public sealed class LicenseGuard(LocalDatabase db, string publicKeyPem, Guid deviceId)
{
    public const string GrantKey = "license.grant";
    public const string LastSeenKey = "clock.last_seen_utc";
    public static readonly TimeSpan RollbackTolerance = TimeSpan.FromMinutes(10);

    public void Store(string token) => db.Set(GrantKey, token);

    public LicenseCheck Evaluate(DateTimeOffset now)
    {
        // Relógio retrocedido além da tolerância indica possível manipulação para estender a licença.
        DateTimeOffset? lastSeen = DateTimeOffset.TryParse(db.Get(LastSeenKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ls) ? ls : null;
        if (lastSeen is { } seen && now < seen - RollbackTolerance)
            return new(false, "clock_rollback", null);
        if (lastSeen is null || now > lastSeen) db.Set(LastSeenKey, now.ToString("O"));

        var token = db.Get(GrantKey);
        if (token is null) return new(false, "no_license", null);
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        var grant = LicenseGrantCodec.Verify(token, key);
        if (grant is null) return new(false, "invalid_signature", null);
        if (grant.DeviceId != deviceId) return new(false, "device_mismatch", grant);
        if (now < grant.IssuedAt - RollbackTolerance) return new(false, "clock_rollback", grant);
        if (now > grant.OfflineValidUntil) return new(false, "offline_expired", grant);
        if (!LicenseStates.AllowsNewOperations(grant.State)) return new(false, "license_" + grant.State, grant);
        return new(true, "ok", grant);
    }
}

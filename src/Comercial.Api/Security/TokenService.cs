using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Comercial.Api.Security;

public static class AuthSchemes
{
    public const string Tenant = "TenantBearer";
    public const string Admin = "AdminBearer";
}

public static class ClaimNames
{
    public const string TenantId = "tid";
    public const string SessionId = "sid";
    public const string DeviceId = "did";
    public const string AdminRole = "arole";
}

public sealed class TokenService(KeyMaterial keys, IConfiguration config)
{
    public const string Issuer = "comercial-saas";
    public const string TenantAudience = "comercial-tenant-api";
    public const string AdminAudience = "comercial-admin-api";

    public int TenantAccessMinutes => config.GetValue("Jwt:TenantAccessMinutes", 10);
    public int AdminAccessMinutes => config.GetValue("Jwt:AdminAccessMinutes", 15);
    public int RefreshDays => config.GetValue("Jwt:RefreshDays", 30);

    public string IssueTenantAccess(Guid userId, Guid tenantId, Guid sessionId, Guid? deviceId)
    {
        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = userId.ToString(),
            [ClaimNames.TenantId] = tenantId.ToString(),
            [ClaimNames.SessionId] = sessionId.ToString(),
        };
        if (deviceId is { } d) claims[ClaimNames.DeviceId] = d.ToString();
        return Create(claims, TenantAudience, keys.TenantJwtKey, TenantAccessMinutes);
    }

    public string IssueAdminAccess(Guid adminId, string role) => Create(new Dictionary<string, object>
    {
        [JwtRegisteredClaimNames.Sub] = adminId.ToString(),
        [ClaimNames.AdminRole] = role,
    }, AdminAudience, keys.AdminJwtKey, AdminAccessMinutes);

    private static string Create(Dictionary<string, object> claims, string audience, SecurityKey key, int minutes)
    {
        claims[JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString();
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(minutes),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
    }

    public static TokenValidationParameters Validation(string audience, SecurityKey key) => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = audience,
        IssuerSigningKey = key,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = JwtRegisteredClaimNames.Sub,
    };
}

public static class ClaimsExt
{
    public static Guid? GuidClaim(this ClaimsPrincipal p, string type) =>
        Guid.TryParse(p.FindFirstValue(type), out var g) ? g : null;
}

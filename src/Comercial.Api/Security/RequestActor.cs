using Comercial.Domain;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Comercial.Api.Security;

/// <summary>Quem está executando a requisição, resolvido no servidor a partir do token validado.</summary>
public sealed class RequestActor
{
    public ActorType Type { get; set; } = ActorType.System;
    public Guid? UserId { get; set; }
    public Guid? AdminId { get; set; }
    public string? AdminRole { get; set; }
    public Guid TenantId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? UserStoreId { get; set; }
    public SupportSession? Support { get; set; }
    public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>();
    public IReadOnlyList<string> Roles { get; set; } = [];
    public string? Ip { get; set; }

    public bool IsTenantUser => Type == ActorType.User && UserId is not null;
    public bool IsSupport => Type == ActorType.Support && Support is not null;
    public bool IsAdmin => Type == ActorType.Admin && AdminId is not null;
    public Guid? ActorId => UserId ?? AdminId;
    public int MaxRoleLevel => Roles.Count == 0 ? 0 : Roles.Max(Domain.Security.Roles.Level);
}

/// <summary>
/// Autentica o token (loja ou admin), confere sessão/dispositivo/usuário no banco a cada requisição
/// (revogação imediata) e fixa o escopo do tenant. Nenhum tenant_id vindo do cliente é aceito.
/// </summary>
public sealed class ActorResolutionMiddleware(RequestDelegate next)
{
    public const string SupportHeader = "X-Support-Session";

    public async Task InvokeAsync(HttpContext ctx, RequestActor actor, TenantContext tenant, AppDbContext db)
    {
        actor.Ip = ctx.Connection.RemoteIpAddress?.ToString();
        var path = ctx.Request.Path;
        var hasBearer = ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        if (!hasBearer) { await next(ctx); return; }

        if (path.StartsWithSegments("/api"))
        {
            var t = await ctx.AuthenticateAsync(AuthSchemes.Tenant);
            if (t.Succeeded)
            {
                if (!await ResolveTenantUser(t.Principal!, actor, tenant, db)) { await Reject(ctx); return; }
            }
            else
            {
                var a = await ctx.AuthenticateAsync(AuthSchemes.Admin);
                if (!a.Succeeded || !await ResolveAdmin(a.Principal!, actor, db)) { await Reject(ctx); return; }
                // Admin só acessa dados de loja via sessão de suporte válida.
                if (!Guid.TryParse(ctx.Request.Headers[SupportHeader], out var sid) || !await ResolveSupport(sid, actor, tenant, db))
                { await Reject(ctx, 403, "support_session_required"); return; }
            }
        }
        else if (path.StartsWithSegments("/admin"))
        {
            var a = await ctx.AuthenticateAsync(AuthSchemes.Admin);
            if (!a.Succeeded || !await ResolveAdmin(a.Principal!, actor, db)) { await Reject(ctx); return; }
            tenant.SetPlatformScope();
        }
        await next(ctx);
    }

    private static async Task<bool> ResolveTenantUser(System.Security.Claims.ClaimsPrincipal p, RequestActor actor, TenantContext tenant, AppDbContext db)
    {
        var uid = p.GuidClaim(JwtRegisteredClaimNames.Sub);
        var tid = p.GuidClaim(ClaimNames.TenantId);
        var sid = p.GuidClaim(ClaimNames.SessionId);
        if (uid is null || tid is null || sid is null) return false;

        var now = DateTimeOffset.UtcNow;
        UserSession? session;
        using (tenant.PlatformLookup())
            session = await db.UserSessions.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid);
        if (session is null || session.TenantId != tid || session.UserId != uid || session.RevokedAt is not null || session.ExpiresAt < now)
            return false;

        tenant.SetTenant(tid.Value);
        var user = await db.Users.AsNoTracking().Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == uid);
        if (user is null || !user.IsActive) return false;

        var did = p.GuidClaim(ClaimNames.DeviceId);
        if (did is not null)
        {
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == did);
            if (device is null || device.Status != DeviceStatus.Active) return false;
        }

        var grants = await db.UserPermissionGrants.AsNoTracking().Where(g => g.UserId == uid).Select(g => new { g.Permission, g.Granted }).ToListAsync();
        actor.Type = ActorType.User;
        actor.UserId = uid;
        actor.TenantId = tid.Value;
        actor.SessionId = sid;
        actor.DeviceId = did;
        actor.UserStoreId = user.StoreId;
        actor.Roles = user.Roles.Select(r => r.Role).ToList();
        actor.Permissions = Domain.Security.Permissions.Resolve(actor.Roles, grants.Select(g => (g.Permission, g.Granted)));
        return true;
    }

    private static async Task<bool> ResolveAdmin(System.Security.Claims.ClaimsPrincipal p, RequestActor actor, AppDbContext db)
    {
        var aid = p.GuidClaim(JwtRegisteredClaimNames.Sub);
        if (aid is null) return false;
        var admin = await db.AdminUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid);
        if (admin is null || !admin.IsActive) return false;
        actor.Type = ActorType.Admin;
        actor.AdminId = admin.Id;
        actor.AdminRole = admin.Role;
        return true;
    }

    private static async Task<bool> ResolveSupport(Guid sessionId, RequestActor actor, TenantContext tenant, AppDbContext db)
    {
        SupportSession? s;
        using (tenant.PlatformLookup())
            s = await db.SupportSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId);
        if (s is null || s.AdminUserId != actor.AdminId || !s.IsActive(DateTimeOffset.UtcNow)) return false;
        tenant.SetTenant(s.TenantId);
        actor.Type = ActorType.Support;
        actor.TenantId = s.TenantId;
        actor.Support = s;
        return true;
    }

    private static Task Reject(HttpContext ctx, int status = 401, string code = "unauthorized")
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { status, code, title = status == 401 ? "Não autenticado." : "Acesso negado." });
    }
}

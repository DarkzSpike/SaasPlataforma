using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Security;
using Comercial.Shared.Licensing;

namespace Comercial.Api.Services;

/// <summary>
/// Serviço CENTRAL de autorização de funcionalidades: combina usuário, tenant, licença,
/// módulo contratado, permissão individual e (quando aplicável) escopo da sessão de suporte.
/// Nenhum endpoint protegido faz verificação de módulo improvisada fora daqui.
/// </summary>
public sealed class FeatureAuthorization(RequestActor actor, LicenseService license, AuditService audit, TimeProvider clock)
{
    /// <summary>Permissões que o suporte pode exercer em modo consulta (padrão).</summary>
    public static readonly HashSet<string> SupportReadable = [Permissions.ProductsRead, Permissions.StockRead, Permissions.FiscalRead];

    /// <summary>Permissões que podem ser liberadas em elevação temporária aprovada. Vendas, caixa e emissão fiscal nunca.</summary>
    public static readonly HashSet<string> SupportElevatable = [Permissions.ProductsWrite, Permissions.StockAdjust, Permissions.DevicesManage, Permissions.UsersManage, Permissions.SalesRead];

    private HashSet<string>? _modules;
    private string? _state;

    public async Task<string> LicenseStateAsync()
    {
        _state ??= (await license.GetStateAsync(actor.TenantId)).State;
        return _state;
    }

    public async Task<HashSet<string>> ModulesAsync() => _modules ??= await license.GetEffectiveModulesAsync(actor.TenantId);

    public async Task<FeatureDecision> AuthorizeAsync(string module, string? permission, bool write)
    {
        if (actor.TenantId == Guid.Empty || !(actor.IsTenantUser || actor.IsSupport))
            return FeatureDecision.Deny(401, "unauthorized", "Não autenticado.");

        if (write && !LicenseStates.AllowsNewOperations(await LicenseStateAsync()))
            return await Denied(403, "license_restricted", "Licença não permite novas operações. Consulta e exportação continuam disponíveis.", module, permission);

        if (!(await ModulesAsync()).Contains(module))
            return await Denied(403, "module_not_enabled", "Módulo não contratado para esta empresa.", module, permission);

        if (permission is null) return FeatureDecision.Allow;

        if (actor.IsSupport)
        {
            var s = actor.Support!;
            var now = clock.GetUtcNow();
            if (!write && SupportReadable.Contains(permission)) return FeatureDecision.Allow;
            if (s.IsElevated(now) && SupportElevatable.Contains(permission) &&
                s.AllowedOperations.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(permission))
                return FeatureDecision.Allow;
            return await Denied(403, "support_scope", "Operação fora do escopo da sessão de suporte.", module, permission);
        }

        if (!actor.Permissions.Contains(permission))
            return await Denied(403, "permission_denied", "Usuário sem permissão para esta operação.", module, permission);

        return FeatureDecision.Allow;
    }

    private async Task<FeatureDecision> Denied(int status, string code, string msg, string module, string? permission)
    {
        await audit.WriteNowAsync("security.feature_denied", actor.TenantId, "module", module, new { permission, code });
        return FeatureDecision.Deny(status, code, msg);
    }
}

public sealed record FeatureDecision(bool Allowed, int Status, string Code, string Message)
{
    public static readonly FeatureDecision Allow = new(true, 200, "", "");
    public static FeatureDecision Deny(int status, string code, string message) => new(false, status, code, message);
}

public static class FeatureEndpointExtensions
{
    /// <summary>Exige módulo + permissão no servidor. write=true também exige licença que permita novas operações.</summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder b, string module, string? permission, bool write = false)
        where TBuilder : IEndpointConventionBuilder
    {
        b.AddEndpointFilter(async (ctx, next) =>
        {
            var fa = ctx.HttpContext.RequestServices.GetRequiredService<FeatureAuthorization>();
            var d = await fa.AuthorizeAsync(module, permission, write);
            return d.Allowed ? await next(ctx) : Results.Json(new { status = d.Status, code = d.Code, title = d.Message }, statusCode: d.Status);
        });
        return b;
    }
}

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Comercial.Infrastructure.Data;

/// <summary>
/// Camada adicional (defesa em profundidade) para PostgreSQL: a cada abertura de conexão,
/// grava o tenant atual em "app.tenant_id", usado pelas políticas de Row-Level Security
/// (ver deploy/postgres/rls.sql). O filtro do EF continua sendo a primeira barreira.
/// </summary>
public sealed class RlsConnectionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        Apply(connection).GetAwaiter().GetResult();

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default) =>
        Apply(connection, ct);

    private async Task Apply(DbConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.tenant_id', @t, false), set_config('app.platform_scope', @p, false)";
        var t = cmd.CreateParameter(); t.ParameterName = "t"; t.Value = tenant.TenantId == Guid.Empty ? "" : tenant.TenantId.ToString();
        var p = cmd.CreateParameter(); p.ParameterName = "p"; p.Value = tenant.IsPlatformScope ? "on" : "off";
        cmd.Parameters.Add(t); cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

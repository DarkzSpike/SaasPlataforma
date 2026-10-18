using Npgsql;

namespace Comercial.Tests;

/// <summary>
/// Prova que o isolamento no PostgreSQL vale MESMO sem o filtro do EF (SQL direto como app_comercial)
/// e que o usuário da aplicação não tem privilégios de dono/DDL/exclusão.
/// </summary>
public class PostgresSecurityTests(ApiFactory f) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly Api api = new(f);
    private TenantInfo A = null!, B = null!;

    public async Task InitializeAsync()
    {
        if (f.Postgres is null) return;
        A = await api.CreateTenant("rls-a-" + Guid.NewGuid().ToString("N")[..6]);
        B = await api.CreateTenant("rls-b-" + Guid.NewGuid().ToString("N")[..6]);
        await api.CreateProduct(A.OwnerToken, "RA1");
        await api.CreateProduct(B.OwnerToken, "RB1");
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<NpgsqlConnection> AppConnection(Guid? tenant, bool platform = false)
    {
        var c = new NpgsqlConnection(f.Postgres!.AppConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false), set_config('app.platform_scope', @p, false)", c);
        cmd.Parameters.AddWithValue("t", tenant?.ToString() ?? "");
        cmd.Parameters.AddWithValue("p", platform ? "on" : "off");
        await cmd.ExecuteNonQueryAsync();
        return c;
    }

    private static async Task<long> Scalar(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task RLS_isola_tenants_em_SQL_direto_sem_filtro_do_EF()
    {
        await using (var a = await AppConnection(A.Id))
        {
            Assert.Equal(0, await Scalar(a, $"SELECT count(*) FROM products WHERE \"TenantId\" <> '{A.Id}'"));
            Assert.Equal(1, await Scalar(a, "SELECT count(*) FROM products"));
            Assert.Equal(0, await Scalar(a, $"SELECT count(*) FROM users WHERE \"TenantId\" = '{B.Id}'"));
            Assert.Equal(0, await Scalar(a, $"SELECT count(*) FROM user_sessions WHERE \"TenantId\" = '{B.Id}'"));
        }
        await using (var none = await AppConnection(null))
        {
            // Sem tenant e sem escopo de plataforma: falha fechada.
            Assert.Equal(0, await Scalar(none, "SELECT count(*) FROM products"));
            Assert.Equal(0, await Scalar(none, "SELECT count(*) FROM users"));
        }
        await using (var platform = await AppConnection(null, platform: true))
            Assert.True(await Scalar(platform, "SELECT count(*) FROM products") >= 2);
    }

    [PostgresFact]
    public async Task RLS_impede_gravar_ou_alterar_linha_de_outro_tenant()
    {
        await using var a = await AppConnection(A.Id);
        var insert = new NpgsqlCommand(
            $"INSERT INTO categories (\"Id\", \"TenantId\", \"Name\") VALUES ('{Guid.NewGuid()}', '{B.Id}', 'invasao')", a);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState); // violação da política (WITH CHECK)

        // UPDATE em linha de B: a linha é invisível, nada é alterado.
        var upd = new NpgsqlCommand($"UPDATE products SET \"PriceCents\" = 1 WHERE \"TenantId\" = '{B.Id}'", a);
        Assert.Equal(0, await upd.ExecuteNonQueryAsync());
    }

    [PostgresFact]
    public async Task Usuario_da_aplicacao_nao_tem_privilegios_de_dono_DDL_ou_exclusao()
    {
        await using var c = await AppConnection(A.Id);
        Assert.Equal(0, await Scalar(c, "SELECT count(*) FROM pg_roles WHERE rolname = current_user AND (rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole)"));
        Assert.Equal(0, await Scalar(c, "SELECT count(*) FROM pg_tables WHERE schemaname='public' AND tableowner = current_user"));
        foreach (var sql in new[]
        {
            "DELETE FROM sales",
            "DELETE FROM products",
            "UPDATE stock_movements SET \"Quantity\" = 0",
            "TRUNCATE audit_logs",
            "DROP TABLE sales",
            "CREATE TABLE invasao (id int)",
            "ALTER TABLE products DISABLE ROW LEVEL SECURITY",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand(sql, c).ExecuteNonQueryAsync());
            Assert.True(ex.SqlState is "42501" or "42P01", $"{sql}: {ex.SqlState} {ex.MessageText}");
        }
    }
}

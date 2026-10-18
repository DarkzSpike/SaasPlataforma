using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Comercial.Tests;

/// <summary>
/// Banco PostgreSQL descartável por fábrica de testes. Ativado pela variável de ambiente
/// COMERCIAL_TEST_PG (string de conexão de um superusuário, ex.: Host=localhost;Port=55432;Username=postgres;Password=...).
/// A migração e o RLS são aplicados como superusuário; a API conecta como app_comercial.
/// </summary>
public sealed class PostgresTestDb : IDisposable
{
    public static string? AdminConnectionString => Environment.GetEnvironmentVariable("COMERCIAL_TEST_PG");
    public static bool Enabled => !string.IsNullOrWhiteSpace(AdminConnectionString);
    public const string AppRole = "app_comercial";
    private const string AppPassword = "senha-somente-testes-locais";
    private static readonly object RoleLock = new();

    public string Database { get; } = "ct_" + Guid.NewGuid().ToString("N")[..12];
    public string AppConnectionString { get; }
    public string OwnerConnectionString { get; }

    public PostgresTestDb()
    {
        var admin = new NpgsqlConnectionStringBuilder(AdminConnectionString);
        Exec(admin.ConnectionString, $"CREATE DATABASE \"{Database}\"");
        OwnerConnectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = Database }.ConnectionString;

        var tc = new TenantContext();
        tc.SetPlatformScope();
        using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                   .UseNpgsql(OwnerConnectionString, n => n.MigrationsHistoryTable("__ef_migrations")).Options, tc))
            db.Database.Migrate();

        // Papéis são globais no cluster: serializa entre as fábricas de teste que rodam em paralelo.
        lock (RoleLock)
        {
            Exec(OwnerConnectionString, File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "postgres", "002_seguranca_rls.sql")));
            Exec(OwnerConnectionString, $"ALTER ROLE {AppRole} PASSWORD '{AppPassword}'");
        }

        AppConnectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
        {
            Database = Database, Username = AppRole, Password = AppPassword, MaxPoolSize = 50,
        }.ConnectionString;
    }

    public static void Exec(string cs, string sql)
    {
        using var c = new NpgsqlConnection(cs);
        c.Open();
        using var cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    public static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Comercial.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }

    public void Dispose()
    {
        NpgsqlConnection.ClearAllPools();
        try { Exec(AdminConnectionString!, $"DROP DATABASE IF EXISTS \"{Database}\" WITH (FORCE)"); } catch { /* limpeza best-effort */ }
    }
}

/// <summary>Teste que só faz sentido com PostgreSQL (RLS, permissões, pg_dump).</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresTestDb.Enabled) Skip = "Defina COMERCIAL_TEST_PG para rodar contra PostgreSQL.";
    }
}

/// <summary>Teste específico do SQLite (ex.: API de backup do SQLite).</summary>
public sealed class SqliteFactAttribute : FactAttribute
{
    public SqliteFactAttribute()
    {
        if (PostgresTestDb.Enabled) Skip = "Teste específico de SQLite; executando contra PostgreSQL.";
    }
}

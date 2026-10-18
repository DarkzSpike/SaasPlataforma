using System.Diagnostics;
using System.Net;
using Comercial.Api.Services;
using Comercial.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

namespace Comercial.Tests;

/// <summary>Backup e restauração: SQLite (terminal/desenvolvimento) e PostgreSQL (pg_dump/pg_restore).</summary>
public class BackupTests(ApiFactory f, ITestOutputHelper output) : IClassFixture<ApiFactory>
{
    private async Task<(TenantInfo Tenant, Guid Product)> SeedAsync()
    {
        var api = new Api(f);
        var t = await api.CreateTenant("bkp-" + Guid.NewGuid().ToString("N")[..6]);
        var p = await api.CreateProduct(t.OwnerToken, "B1");
        var c = await api.OpenCash(t.OwnerToken, t.StoreId);
        await Api.Expect(await api.Post("/api/v1/sales", t.OwnerToken, Api.Sale(t.StoreId, c, p)), HttpStatusCode.Created);
        return (t, p);
    }

    private static async Task AssertRestoredAsync(AppDbContext restored, TenantInfo t, Guid p)
    {
        Assert.Equal(1, await restored.Sales.IgnoreQueryFilters().CountAsync(s => s.TenantId == t.Id));
        Assert.True(await restored.Products.IgnoreQueryFilters().AnyAsync(x => x.Id == p));
        var (ok, broken, count) = await AuditService.VerifyChainAsync(restored);
        Assert.True(ok, $"cadeia quebrada em {broken}");
        Assert.True(count > 0);
    }

    [SqliteFact]
    public async Task Backup_online_e_restauracao_preservam_dados_e_cadeia_de_auditoria()
    {
        var (t, p) = await SeedAsync();
        var backupPath = Path.Combine(Path.GetTempPath(), $"comercial-backup-{Guid.NewGuid():N}.db");
        using (var src = new SqliteConnection($"Data Source={f.DbPath}"))
        using (var dst = new SqliteConnection($"Data Source={backupPath}"))
        {
            src.Open(); dst.Open();
            src.BackupDatabase(dst); // API de backup online do SQLite (consistente com escritas concorrentes)
        }
        var tc = new TenantContext(); tc.SetPlatformScope();
        await using (var restored = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={backupPath}").Options, tc))
            await AssertRestoredAsync(restored, t, p);
        SqliteConnection.ClearAllPools();
        File.Delete(backupPath);
    }

    [PostgresFact]
    public async Task PgDump_e_pgRestore_preservam_dados_RLS_e_cadeia_de_auditoria()
    {
        var pg = f.Postgres!;
        var (t, p) = await SeedAsync();
        var dump = Path.Combine(Path.GetTempPath(), $"comercial-{Guid.NewGuid():N}.dump");
        var target = pg.Database + "_restore";
        var sw = Stopwatch.StartNew();
        try
        {
            Run("pg_dump", $"--format=custom --no-owner --file \"{dump}\" --dbname={pg.Database}");
            Run("pg_restore", $"--list \"{dump}\"");
            var dumpTime = sw.Elapsed;
            PostgresTestDb.Exec(PostgresTestDb.AdminConnectionString!, $"CREATE DATABASE \"{target}\"");
            Run("pg_restore", $"--no-owner --exit-on-error --dbname={target} \"{dump}\"");
            output.WriteLine($"Backup: {dumpTime.TotalSeconds:F1}s; restauração total: {sw.Elapsed.TotalSeconds:F1}s; tamanho: {new FileInfo(dump).Length / 1024} KB");

            var cs = new NpgsqlConnectionStringBuilder(PostgresTestDb.AdminConnectionString) { Database = target }.ConnectionString;
            var tc = new TenantContext(); tc.SetPlatformScope();
            await using (var restored = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options, tc))
                await AssertRestoredAsync(restored, t, p);

            // Políticas de RLS e o trigger da auditoria vêm junto no backup.
            await using var c = new NpgsqlConnection(cs);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT (SELECT count(*) FROM pg_policies WHERE policyname='tenant_isolation'), (SELECT count(*) FROM pg_trigger WHERE tgname='trg_audit_logs_immutable')", c);
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            Assert.True(r.GetInt64(0) >= 28, "políticas RLS ausentes no banco restaurado");
            Assert.Equal(1, r.GetInt64(1));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            PostgresTestDb.Exec(PostgresTestDb.AdminConnectionString!, $"DROP DATABASE IF EXISTS \"{target}\" WITH (FORCE)");
            File.Delete(dump);
        }
    }

    /// <summary>Executa pg_dump/pg_restore (pasta em COMERCIAL_TEST_PG_BIN ou PATH) com as credenciais do superusuário de teste.</summary>
    private static void Run(string tool, string args)
    {
        var b = new NpgsqlConnectionStringBuilder(PostgresTestDb.AdminConnectionString);
        var bin = Environment.GetEnvironmentVariable("COMERCIAL_TEST_PG_BIN");
        var psi = new ProcessStartInfo(bin is null ? tool : Path.Combine(bin, tool), $"--host={b.Host} --port={b.Port} --username={b.Username} --no-password {args}")
        {
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
        };
        psi.Environment["PGPASSWORD"] = b.Password ?? "";
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        _ = stdout.Result;
        Assert.True(proc.ExitCode == 0, $"{tool} falhou ({proc.ExitCode}): {err}");
    }
}

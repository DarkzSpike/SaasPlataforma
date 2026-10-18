using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Comercial.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Database:Provider = "Postgres" (produção/homologação) ou "Sqlite" (desenvolvimento local e testes).
    /// A connection string vem de configuração/variável de ambiente — nunca do código-fonte.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        var provider = config["Database:Provider"] ?? "Sqlite";
        var cs = config.GetConnectionString("Main") ?? throw new InvalidOperationException("ConnectionStrings:Main não configurada.");
        if (!provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(cs).DataSource) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);
        services.AddDbContext<AppDbContext>((sp, o) =>
        {
            if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
            {
                o.UseNpgsql(cs, n => n.MigrationsHistoryTable("__ef_migrations"));
                o.AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<ITenantContext>()));
            }
            else
            {
                o.UseSqlite(cs);
            }
        });
        return services;
    }
}

/// <summary>Usado apenas por "dotnet ef migrations" (gera migrações PostgreSQL sem conexão real).</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var o = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", n => n.MigrationsHistoryTable("__ef_migrations")).Options;
        var t = new TenantContext(); t.SetPlatformScope();
        return new AppDbContext(o, t);
    }
}

using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Modules;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api;

/// <summary>Converte erros em respostas padronizadas sem stack trace, SQL ou detalhes internos.</summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (DomainException ex)
        {
            if (ctx.Response.HasStarted) throw;
            ctx.Response.StatusCode = ex.Status;
            await ctx.Response.WriteAsJsonAsync(new { status = ex.Status, code = ex.Code, title = ex.Message });
        }
        catch (BadHttpRequestException ex)
        {
            if (ctx.Response.HasStarted) throw;
            ctx.Response.StatusCode = ex.StatusCode;
            await ctx.Response.WriteAsJsonAsync(new { status = ex.StatusCode, code = "bad_request", title = "Requisição inválida." });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Erro não tratado em {Path}", ctx.Request.Path);
            if (ctx.Response.HasStarted) throw;
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new { status = 500, code = "internal_error", title = "Erro interno.", traceId = ctx.TraceIdentifier });
        }
    }
}

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider root, IHostEnvironment env)
    {
        using var scope = root.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var log = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();

        if (db.Database.IsNpgsql())
        {
            // Produção: migrações rodam em etapa de deploy com usuário dono do schema; a API usa app_comercial
            // (sem permissão de DDL) e apenas confere se o banco está atualizado.
            if (config.GetValue("Database:ApplyMigrationsOnStartup", false)) await db.Database.MigrateAsync();
            else if ((await db.Database.GetPendingMigrationsAsync()).ToList() is { Count: > 0 } pending)
                throw new InvalidOperationException("Migrações pendentes no banco: " + string.Join(", ", pending));
        }
        else await db.Database.EnsureCreatedAsync();

        await SeedCatalogAsync(db);
        await SeedPlansAsync(db);
        await SeedAdminAsync(scope.ServiceProvider, db, config, env, log);
    }

    /// <summary>Sincroniza o catálogo do código com o banco (status, nome, dependências).</summary>
    public static async Task SeedCatalogAsync(AppDbContext db)
    {
        var existing = await db.ModuleCatalog.Include(m => m.Dependencies).ToDictionaryAsync(m => m.Key);
        var defs = ModuleCatalogSeed.Build();
        foreach (var d in defs.Where(d => !existing.ContainsKey(d.Key)))
            db.ModuleCatalog.Add(new ModuleDefinition { Key = d.Key, Name = d.Name, Description = d.Description, Category = d.Category, Status = d.Status, IsCore = d.IsCore, AvailableAsAddOn = d.AvailableAsAddOn, Version = d.Version });
        await db.SaveChangesAsync();
        existing = await db.ModuleCatalog.Include(m => m.Dependencies).ToDictionaryAsync(m => m.Key);
        foreach (var d in defs)
        {
            var e = existing[d.Key];
            e.Name = d.Name; e.Description = d.Description; e.Category = d.Category; e.Status = d.Status; e.Version = d.Version;
            foreach (var dep in d.Dependencies.Where(dep => e.Dependencies.All(x => x.DependsOnKey != dep.DependsOnKey)))
                e.Dependencies.Add(new ModuleDependency { ModuleKey = d.Key, DependsOnKey = dep.DependsOnKey });
        }
        await db.SaveChangesAsync();
    }

    private static async Task SeedPlansAsync(AppDbContext db)
    {
        if (await db.Plans.AnyAsync()) return;
        SubscriptionPlan P(string code, string name, long price, int users, int devices, int stores, params string[] modules)
        {
            var p = new SubscriptionPlan { Code = code, Name = name, PriceCents = price, MaxUsers = users, MaxDevices = devices, MaxStores = stores };
            p.Modules.AddRange(modules.Select(m => new PlanModule { PlanId = p.Id, ModuleKey = m }));
            return p;
        }
        // Preços de exemplo — editáveis no painel; não há preço fixo no código de cobrança.
        db.Plans.Add(P("basico", "Básico", 9900, 3, 1, 1, ModuleKeys.Pdv, ModuleKeys.Products, ModuleKeys.Stock, ModuleKeys.Cash, ModuleKeys.StoreHours));
        db.Plans.Add(P("profissional", "Profissional", 19900, 10, 3, 1, ModuleKeys.Pdv, ModuleKeys.Products, ModuleKeys.Stock, ModuleKeys.Cash, ModuleKeys.StoreHours,
            ModuleKeys.Fiscal, ModuleKeys.UsersAdvanced, ModuleKeys.OwnerApp, ModuleKeys.WhiteLabel, ModuleKeys.CashPerOperator));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Cria o primeiro administrador a partir de configuração/segredos (Bootstrap:*), somente se não houver nenhum.
    /// Não existe conta administrativa padrão embutida no código.
    /// </summary>
    private static async Task SeedAdminAsync(IServiceProvider sp, AppDbContext db, IConfiguration config, IHostEnvironment env, ILogger log)
    {
        if (await db.AdminUsers.AnyAsync()) return;
        var email = config["Bootstrap:AdminEmail"];
        var password = config["Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;
        if (PasswordHasher.ValidatePolicy(password) is { } err) throw new InvalidOperationException("Bootstrap:AdminPassword fraca: " + err);

        var secret = config["Bootstrap:AdminTotpSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            if (!env.IsDevelopment()) throw new InvalidOperationException("Bootstrap:AdminTotpSecret obrigatório fora de Development.");
            secret = Totp.NewSecret();
            log.LogWarning("Administrador inicial criado. Cadastre no autenticador o segredo TOTP: {Secret} (exibido apenas uma vez, somente em Development)", secret);
        }
        var protector = sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("admin-totp-v1");
        db.AdminUsers.Add(new AdminUser
        {
            Email = email.Trim().ToLowerInvariant(), DisplayName = "Administrador", Role = AdminRoles.SaasAdmin,
            PasswordHash = sp.GetRequiredService<PasswordHasher>().Hash(password), TotpSecretProtected = protector.Protect(secret),
        });
        await db.SaveChangesAsync();
    }
}

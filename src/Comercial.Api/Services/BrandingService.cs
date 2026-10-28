using System.Buffers.Binary;
using System.Text.Json;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Domain.Modules;
using Comercial.Infrastructure.Data;
using Comercial.Shared.Branding;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record PublishBrandingRequest(string ConfirmTenantCode, string Note);
public sealed record PublishedBranding(Guid TenantId, int Version, bool Custom, BrandingConfig Config, string CacheKey);

public sealed class BrandingService(AppDbContext db, AuditService audit, RequestActor actor, LicenseService license, IConfiguration config)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const long MaxAssetBytes = 2 * 1024 * 1024;
    public const int MaxDimension = 4096;
    public static readonly string[] AssetKinds = ["logo", "logo_dark", "icon", "splash", "login_image", "login_background"];

    private string StorageRoot => config["Storage:Root"] ?? Path.Combine(AppContext.BaseDirectory, "..", "storage");

    /// <summary>Identidade publicada do tenant. Sem o módulo White Label, devolve o padrão do produto.</summary>
    public async Task<PublishedBranding> GetPublishedAsync(Guid tenantId)
    {
        var modules = await license.GetEffectiveModulesAsync(tenantId);
        var b = await db.Brandings.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId);
        if (!modules.Contains(ModuleKeys.WhiteLabel) || b is null || b.PublishedVersion == 0)
            return new(tenantId, 0, false, new BrandingConfig(), $"{tenantId:N}:default");
        var v = await db.BrandingVersions.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.TenantId == tenantId && x.Version == b.PublishedVersion);
        return new(tenantId, v.Version, true, JsonSerializer.Deserialize<BrandingConfig>(v.ConfigJson, Json)!, $"{tenantId:N}:{v.Version}");
    }

    public async Task<BrandingConfig> GetDraftAsync(Guid tenantId)
    {
        var b = await Load(tenantId);
        return JsonSerializer.Deserialize<BrandingConfig>(b.DraftJson, Json) ?? new BrandingConfig();
    }

    public async Task<BrandingConfig> SaveDraftAsync(Guid tenantId, BrandingConfig cfg)
    {
        await ValidateAsync(tenantId, cfg);
        var b = await Load(tenantId);
        b.DraftJson = JsonSerializer.Serialize(cfg, Json);
        b.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add("branding.draft_saved", tenantId, nameof(TenantBranding), tenantId);
        await db.SaveChangesAsync();
        return cfg;
    }

    public async Task ValidateAsync(Guid tenantId, BrandingConfig cfg)
    {
        var errors = BrandingRules.Validate(cfg);
        // Assets referenciados precisam pertencer ao MESMO tenant.
        foreach (var id in new[] { cfg.LogoAssetId, cfg.LogoDarkAssetId, cfg.IconAssetId, cfg.SplashAssetId, cfg.LoginImageAssetId, cfg.LoginBackgroundAssetId }.OfType<Guid>())
            if (!await db.BrandingAssets.IgnoreQueryFilters().AnyAsync(a => a.Id == id && a.TenantId == tenantId))
                errors.Add($"Asset {id} não pertence a esta empresa.");
        if (errors.Count > 0) throw new DomainException("invalid_branding", string.Join("; ", errors));
    }

    public async Task<BrandingVersion> PublishAsync(Guid tenantId, PublishBrandingRequest r, int? restoreFrom = null)
    {
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId);
        if (r.ConfirmTenantCode != tenant.Code) throw new DomainException("confirmation_mismatch", "Código de confirmação da empresa não confere.", 409);
        var b = await Load(tenantId);
        BrandingConfig cfg;
        if (restoreFrom is { } rv)
        {
            var old = await db.BrandingVersions.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Version == rv)
                      ?? throw new DomainException("version_not_found", "Versão não encontrada.", 404);
            cfg = JsonSerializer.Deserialize<BrandingConfig>(old.ConfigJson, Json)!;
        }
        else cfg = JsonSerializer.Deserialize<BrandingConfig>(b.DraftJson, Json)!;
        await ValidateAsync(tenantId, cfg);

        var last = await db.BrandingVersions.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).MaxAsync(x => (int?)x.Version) ?? 0;
        var v = new BrandingVersion
        {
            TenantId = tenantId, Version = last + 1, ConfigJson = JsonSerializer.Serialize(cfg, Json),
            PublishedByAdminId = actor.AdminId!.Value, RestoredFromVersion = restoreFrom, Note = r.Note ?? "",
        };
        db.BrandingVersions.Add(v);
        var previous = b.PublishedVersion;
        b.PublishedVersion = v.Version;
        if (restoreFrom is not null) b.DraftJson = v.ConfigJson;
        audit.Add(restoreFrom is null ? "branding.published" : "branding.restored", tenantId, nameof(BrandingVersion), v.Version,
            new { previous, version = v.Version, restoreFrom, r.Note });
        await db.SaveChangesAsync();
        return v;
    }

    public async Task ResetToDefaultAsync(Guid tenantId, string confirmCode)
    {
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId);
        if (confirmCode != tenant.Code) throw new DomainException("confirmation_mismatch", "Código de confirmação não confere.", 409);
        var b = await Load(tenantId);
        var previous = b.PublishedVersion;
        b.PublishedVersion = 0; // versões anteriores permanecem no histórico
        audit.Add("branding.reset_default", tenantId, nameof(TenantBranding), tenantId, new { previous });
        await db.SaveChangesAsync();
    }

    public async Task<BrandingAsset> UploadAsync(Guid tenantId, string kind, IFormFile file)
    {
        if (!AssetKinds.Contains(kind)) throw new DomainException("invalid_kind", "Tipo de imagem inválido.");
        if (file.Length <= 0 || file.Length > MaxAssetBytes) throw new DomainException("file_too_large", "Imagem deve ter até 2 MB.", 413);
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        var info = ImageInspector.Inspect(bytes) ?? throw new DomainException("invalid_image", "Arquivo não é uma imagem PNG ou JPEG válida.", 415);
        if (info.Width < 16 || info.Height < 16 || info.Width > MaxDimension || info.Height > MaxDimension)
            throw new DomainException("invalid_dimensions", $"Dimensões devem estar entre 16 e {MaxDimension} px.");

        var asset = new BrandingAsset
        {
            TenantId = tenantId, Kind = kind, ContentType = info.ContentType, Sha256 = Hashing.Sha256Hex(bytes), SizeBytes = bytes.Length,
            Width = info.Width, Height = info.Height, UploadedByAdminId = actor.AdminId!.Value,
        };
        asset.StorageName = asset.Id.ToString("N") + ".bin"; // nunca usa o nome enviado
        var dir = Path.Combine(StorageRoot, "branding", tenantId.ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, asset.StorageName), bytes);
        db.BrandingAssets.Add(asset);
        audit.Add("branding.asset_uploaded", tenantId, nameof(BrandingAsset), asset.Id, new { kind, asset.SizeBytes, asset.Sha256, original = Path.GetFileName(file.FileName) });
        await db.SaveChangesAsync();
        return asset;
    }

    /// <summary>Lê o asset SOMENTE se pertencer ao tenant informado.</summary>
    public async Task<(BrandingAsset Asset, byte[] Bytes)?> ReadAssetAsync(Guid tenantId, Guid assetId)
    {
        var a = await db.BrandingAssets.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == assetId && x.TenantId == tenantId);
        if (a is null) return null;
        var path = Path.Combine(StorageRoot, "branding", tenantId.ToString("N"), a.StorageName);
        return File.Exists(path) ? (a, await File.ReadAllBytesAsync(path)) : null;
    }

    public Task<List<BrandingVersion>> VersionsAsync(Guid tenantId) =>
        db.BrandingVersions.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tenantId).OrderByDescending(x => x.Version).ToListAsync();

    private async Task<TenantBranding> Load(Guid tenantId) =>
        await db.Brandings.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TenantId == tenantId)
        ?? throw new DomainException("tenant_not_found", "Empresa não encontrada.", 404);
}

/// <summary>
/// Valida o conteúdo REAL da imagem (assinatura + cabeçalho), não a extensão nem o Content-Type enviado.
/// Aceita apenas PNG e JPEG (SVG é recusado por poder conter script).
/// </summary>
public static class ImageInspector
{
    public sealed record Info(string ContentType, int Width, int Height);

    public static Info? Inspect(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A
            && b[12] == 'I' && b[13] == 'H' && b[14] == 'D' && b[15] == 'R')
        {
            var w = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16, 4));
            var h = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20, 4));
            // Deve terminar com o chunk IEND (detecta arquivos truncados/poliglotas simples).
            var iend = b.Length >= 12 && b[^8] == 'I' && b[^7] == 'E' && b[^6] == 'N' && b[^5] == 'D';
            return w > 0 && h > 0 && iend ? new Info("image/png", w, h) : null;
        }
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            if (b[^2] != 0xFF || b[^1] != 0xD9) return null;
            var i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) return null;
                var marker = b[i + 1];
                var len = (b[i + 2] << 8) | b[i + 3];
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                {
                    var h = (b[i + 5] << 8) | b[i + 6];
                    var w = (b[i + 7] << 8) | b[i + 8];
                    return w > 0 && h > 0 ? new Info("image/jpeg", w, h) : null;
                }
                if (len < 2) return null;
                i += 2 + len;
            }
        }
        return null;
    }
}

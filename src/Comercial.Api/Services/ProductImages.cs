using System.Collections.Concurrent;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.EntityFrameworkCore;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace Comercial.Api.Services;

/// <summary>
/// Onde as fotos de produtos ficam guardadas. A chave salva no produto tem prefixo do provedor
/// ("local:", "gdrive:"), então trocar de provedor não quebra as fotos antigas.
/// O acesso é SEMPRE pela API (que confere a empresa); nenhum link público é gerado.
/// </summary>
public interface IProductImageStore
{
    string Prefix { get; }
    Task<string> SaveAsync(Tenant tenant, Product product, byte[] bytes, string contentType, CancellationToken ct);
    Task<byte[]?> ReadAsync(Guid tenantId, string key, CancellationToken ct);
}

public sealed class LocalProductImageStore(IConfiguration config) : IProductImageStore
{
    public string Prefix => "local:";
    private string Root => Path.Combine(config["Storage:Root"] ?? Path.Combine(AppContext.BaseDirectory, "..", "storage"), "products");

    public async Task<string> SaveAsync(Tenant tenant, Product product, byte[] bytes, string contentType, CancellationToken ct)
    {
        var name = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Root, tenant.Id.ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, name + ".bin"), bytes, ct);
        return Prefix + name;
    }

    public async Task<byte[]?> ReadAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var name = key[Prefix.Length..];
        if (name.Length != 32 || !name.All(Uri.IsHexDigit)) return null; // impede path traversal
        var path = Path.Combine(Root, tenantId.ToString("N"), name + ".bin");
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }
}

/// <summary>
/// Google Drive: uma pasta por empresa dentro de uma pasta raiz (de preferência num Drive compartilhado do
/// Google Workspace). Autentica com conta de serviço; opcionalmente com delegação de domínio (ImpersonateUser).
/// Configuração: ProductImages:GoogleDrive:CredentialsPath, RootFolderId e (opcional) ImpersonateUser.
/// STATUS: escrito, NÃO testado contra o Google Drive real (requer credenciais da empresa).
/// </summary>
public sealed class GoogleDriveProductImageStore : IProductImageStore
{
    private readonly DriveService _drive;
    private readonly string _rootFolderId;
    private readonly ConcurrentDictionary<Guid, string> _folders = new();
    public string Prefix => "gdrive:";

    public GoogleDriveProductImageStore(IConfiguration config)
    {
        var section = config.GetSection("ProductImages:GoogleDrive");
        var path = section["CredentialsPath"] ?? throw new InvalidOperationException("ProductImages:GoogleDrive:CredentialsPath não configurado.");
        _rootFolderId = section["RootFolderId"] ?? throw new InvalidOperationException("ProductImages:GoogleDrive:RootFolderId não configurado.");
#pragma warning disable CS0618 // FromFile: credencial vem de arquivo controlado pelo operador do servidor
        var credential = GoogleCredential.FromFile(path).CreateScoped(DriveService.Scope.Drive);
#pragma warning restore CS0618
        if (section["ImpersonateUser"] is { Length: > 0 } user) credential = credential.CreateWithUser(user);
        _drive = new DriveService(new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "comercial-saas" });
    }

    private async Task<string> FolderAsync(Tenant tenant, CancellationToken ct)
    {
        if (_folders.TryGetValue(tenant.Id, out var id)) return id;
        var name = $"{tenant.Code} ({tenant.Id:N})";
        var list = _drive.Files.List();
        list.Q = $"mimeType='application/vnd.google-apps.folder' and name='{name.Replace("'", "\\'")}' and '{_rootFolderId}' in parents and trashed=false";
        list.SupportsAllDrives = true;
        list.IncludeItemsFromAllDrives = true;
        list.Fields = "files(id)";
        var found = (await list.ExecuteAsync(ct)).Files.FirstOrDefault()?.Id;
        if (found is null)
        {
            var create = _drive.Files.Create(new DriveFile
            {
                Name = name, MimeType = "application/vnd.google-apps.folder", Parents = [_rootFolderId],
                AppProperties = new Dictionary<string, string> { ["tenantId"] = tenant.Id.ToString() },
            });
            create.SupportsAllDrives = true;
            create.Fields = "id";
            found = (await create.ExecuteAsync(ct)).Id;
        }
        _folders[tenant.Id] = found;
        return found;
    }

    public async Task<string> SaveAsync(Tenant tenant, Product product, byte[] bytes, string contentType, CancellationToken ct)
    {
        var folder = await FolderAsync(tenant, ct);
        var ext = contentType == "image/png" ? "png" : "jpg";
        var meta = new DriveFile
        {
            Name = $"{product.Sku}-{Guid.NewGuid():N}.{ext}", Parents = [folder],
            AppProperties = new Dictionary<string, string> { ["tenantId"] = tenant.Id.ToString(), ["productId"] = product.Id.ToString() },
        };
        using var ms = new MemoryStream(bytes);
        var upload = _drive.Files.Create(meta, ms, contentType);
        upload.SupportsAllDrives = true;
        upload.Fields = "id";
        var progress = await upload.UploadAsync(ct);
        if (progress.Exception is not null) throw new DomainException("image_storage_failed", "Falha ao enviar a foto ao Google Drive.", 502);
        return Prefix + upload.ResponseBody.Id;
    }

    public async Task<byte[]?> ReadAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var get = _drive.Files.Get(key[Prefix.Length..]);
        get.SupportsAllDrives = true;
        get.Fields = "id,appProperties";
        var meta = await get.ExecuteAsync(ct);
        // Defesa extra: o arquivo precisa ter sido gravado para ESTA empresa.
        if (meta.AppProperties is null || !meta.AppProperties.TryGetValue("tenantId", out var t) || t != tenantId.ToString()) return null;
        using var ms = new MemoryStream();
        await get.DownloadAsync(ms, ct);
        return ms.ToArray();
    }
}

public sealed class ProductImageService(
    AppDbContext db, IEnumerable<IProductImageStore> stores, IConfiguration config, AuditService audit, RequestActor actor, ScopedLookup lookup)
{
    public const long MaxBytes = 5 * 1024 * 1024;
    private IProductImageStore Active =>
        stores.FirstOrDefault(s => s.Prefix.TrimEnd(':').Equals(config["ProductImages:Provider"] == "GoogleDrive" ? "gdrive" : "local", StringComparison.OrdinalIgnoreCase))
        ?? stores.First(s => s is LocalProductImageStore);

    public async Task<Product> UploadAsync(Guid productId, IFormFile file, CancellationToken ct)
    {
        var product = await lookup.GetAsync<Product>(productId);
        if (file.Length <= 0 || file.Length > MaxBytes) throw new DomainException("file_too_large", "A foto deve ter até 5 MB.", 413);
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var info = ImageInspector.Inspect(bytes) ?? throw new DomainException("invalid_image", "Arquivo não é uma foto PNG ou JPEG válida.", 415);
        if (info.Width < 32 || info.Height < 32 || info.Width > 8000 || info.Height > 8000)
            throw new DomainException("invalid_dimensions", "A foto deve ter entre 32 e 8000 pixels de lado.");
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == actor.TenantId, ct);
        var store = Active;
        var key = await store.SaveAsync(tenant, product, bytes, info.ContentType, ct);
        var previous = product.ImageKey;
        product.ImageKey = key;
        product.ImageContentType = info.ContentType;
        product.ImageUpdatedAt = DateTimeOffset.UtcNow;
        product.Version++;
        audit.Add("product.image_uploaded", actor.TenantId, nameof(Product), product.Id,
            new { previous, key, info.Width, info.Height, size = bytes.Length, sha256 = Hashing.Sha256Hex(bytes), provider = store.Prefix.TrimEnd(':') });
        await db.SaveChangesAsync(ct);
        return product;
    }

    public async Task<(byte[] Bytes, string ContentType)?> ReadAsync(Guid productId, CancellationToken ct)
    {
        var product = await lookup.GetAsync<Product>(productId);
        if (product.ImageKey is null) return null;
        var store = stores.FirstOrDefault(s => product.ImageKey.StartsWith(s.Prefix, StringComparison.Ordinal));
        if (store is null) return null;
        var bytes = await store.ReadAsync(actor.TenantId, product.ImageKey, ct);
        return bytes is null ? null : (bytes, product.ImageContentType ?? "image/jpeg");
    }
}

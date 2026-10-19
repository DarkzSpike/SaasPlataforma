using System.Collections.Concurrent;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace Comercial.Api.Services;

public sealed record StorageSettingsRequest(string Provider, string? DriveFolder, string? ImpersonateUser, string? CredentialsJson,
    bool RemoveCredentials, string ConfirmTenantCode);

/// <summary>O que o painel vê. Nunca inclui a chave privada nem o JSON da credencial.</summary>
public sealed record StorageSettingsView(string Provider, string? DriveFolderId, string? ImpersonateUser, bool HasCredentials,
    string? CredentialsClientEmail, int Version, DateTimeOffset? UpdatedAt, DateTimeOffset? LastTestAt, bool? LastTestOk, string? LastTestMessage);

public sealed record DriveFolderInfo(string Id, string Name, bool IsFolder, bool CanAddChildren);

/// <summary>Credencial já descriptografada + pasta da empresa, só em memória durante a operação.</summary>
public sealed record DriveTarget(Guid TenantId, int Version, string CredentialsJson, string FolderId, string? ImpersonateUser);

/// <summary>Chamadas ao Google Drive (substituível nos testes).</summary>
public interface IDriveGateway
{
    Task<DriveFolderInfo> GetFolderAsync(DriveTarget target, CancellationToken ct);
    Task<string> UploadAsync(DriveTarget target, string name, byte[] bytes, string contentType, IDictionary<string, string> appProperties, CancellationToken ct);
    Task<(byte[] Bytes, IDictionary<string, string>? AppProperties)?> DownloadAsync(DriveTarget target, string fileId, CancellationToken ct);
}

public sealed class GoogleDriveGateway : IDriveGateway
{
    // Um cliente por empresa e versão da configuração: trocar a credencial no painel gera cliente novo.
    private readonly ConcurrentDictionary<(Guid, int), DriveService> _clients = new();

    private DriveService Client(DriveTarget t) => _clients.GetOrAdd((t.TenantId, t.Version), _ =>
    {
        var credential = CredentialFactory.FromJson<ServiceAccountCredential>(t.CredentialsJson).ToGoogleCredential().CreateScoped(DriveService.Scope.Drive);
        if (!string.IsNullOrWhiteSpace(t.ImpersonateUser)) credential = credential.CreateWithUser(t.ImpersonateUser);
        return new DriveService(new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "comercial-saas" });
    });

    public async Task<DriveFolderInfo> GetFolderAsync(DriveTarget target, CancellationToken ct)
    {
        var get = Client(target).Files.Get(target.FolderId);
        get.SupportsAllDrives = true;
        get.Fields = "id,name,mimeType,capabilities(canAddChildren)";
        var f = await get.ExecuteAsync(ct);
        return new DriveFolderInfo(f.Id, f.Name, f.MimeType == "application/vnd.google-apps.folder", f.Capabilities?.CanAddChildren ?? false);
    }

    public async Task<string> UploadAsync(DriveTarget target, string name, byte[] bytes, string contentType, IDictionary<string, string> appProperties, CancellationToken ct)
    {
        using var ms = new MemoryStream(bytes);
        var upload = Client(target).Files.Create(new DriveFile { Name = name, Parents = [target.FolderId], AppProperties = appProperties }, ms, contentType);
        upload.SupportsAllDrives = true;
        upload.Fields = "id";
        var progress = await upload.UploadAsync(ct);
        if (progress.Exception is not null) throw new DomainException("image_storage_failed", "Falha ao enviar a foto ao Google Drive da empresa.", 502);
        return upload.ResponseBody.Id;
    }

    public async Task<(byte[] Bytes, IDictionary<string, string>? AppProperties)?> DownloadAsync(DriveTarget target, string fileId, CancellationToken ct)
    {
        var get = Client(target).Files.Get(fileId);
        get.SupportsAllDrives = true;
        get.Fields = "id,appProperties";
        var meta = await get.ExecuteAsync(ct);
        using var ms = new MemoryStream();
        await get.DownloadAsync(ms, ct);
        return (ms.ToArray(), meta.AppProperties);
    }
}

/// <summary>
/// Configuração de armazenamento por empresa (painel administrativo) e acesso ao Drive de cada empresa.
/// A credencial é criptografada com Data Protection; o painel só vê o e-mail da conta de serviço.
/// </summary>
public sealed partial class TenantStorageService(AppDbContext db, AuditService audit, RequestActor actor, IDataProtectionProvider dp, IDriveGateway drive)
{
    public const string TenantDrivePrefix = "gdrive-t:";
    private const int MaxCredentialBytes = 20_000;
    private IDataProtector Protector => dp.CreateProtector("Comercial.TenantStorage.Credentials.v1");

    [GeneratedRegex(@"^[A-Za-z0-9_-]{10,200}$")] private static partial Regex FolderIdRx();
    [GeneratedRegex(@"(?:folders/|[?&]id=)([A-Za-z0-9_-]{10,200})")] private static partial Regex FolderUrlRx();

    /// <summary>Aceita o ID da pasta ou o link copiado do Google Drive.</summary>
    public static string? ExtractFolderId(string? input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return null;
        var m = FolderUrlRx().Match(s);
        if (m.Success) return m.Groups[1].Value;
        return FolderIdRx().IsMatch(s) ? s : throw new DomainException("invalid_folder", "Informe o link ou o ID da pasta do Google Drive.");
    }

    /// <summary>Confere que é uma conta de serviço do Google (recusa outros tipos de credencial) e devolve o e-mail.</summary>
    public static string ValidateCredentials(string json)
    {
        if (json.Length > MaxCredentialBytes) throw new DomainException("invalid_credentials", "Arquivo de credencial grande demais.");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Str("type") != "service_account")
                throw new DomainException("invalid_credentials", "Use o JSON de uma conta de serviço do Google (\"type\": \"service_account\").");
            var email = Str("client_email");
            var key = Str("private_key");
            if (string.IsNullOrWhiteSpace(email) || !MailAddress.TryCreate(email, out _) || string.IsNullOrWhiteSpace(key) || !key.Contains("PRIVATE KEY"))
                throw new DomainException("invalid_credentials", "O JSON da conta de serviço está incompleto (client_email e private_key).");
            return email;
        }
        catch (JsonException) { throw new DomainException("invalid_credentials", "O conteúdo informado não é um JSON válido."); }
    }

    public static StorageSettingsView View(TenantStorageSettings? s) => s is null
        ? new StorageSettingsView("local", null, null, false, null, 0, null, null, null, null)
        : new StorageSettingsView(s.Provider, s.DriveFolderId, s.ImpersonateUser, s.CredentialsProtected is not null, s.CredentialsClientEmail,
            s.Version, s.UpdatedAt, s.LastTestAt, s.LastTestOk, s.LastTestMessage);

    private Task<TenantStorageSettings?> FindAsync(Guid tenantId) => db.StorageSettings.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TenantId == tenantId);

    public async Task<StorageSettingsView> GetAsync(Guid tenantId)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId)) throw new DomainException("not_found", "Empresa não encontrada.", 404);
        return View(await FindAsync(tenantId));
    }

    public async Task<StorageSettingsView> SaveAsync(Guid tenantId, StorageSettingsRequest r)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId) ?? throw new DomainException("not_found", "Empresa não encontrada.", 404);
        if (r.ConfirmTenantCode != tenant.Code) throw new DomainException("confirmation_mismatch", "Código de confirmação não confere.", 409);
        var provider = (r.Provider ?? "").Trim().ToLowerInvariant();
        if (provider is not ("local" or "gdrive")) throw new DomainException("invalid_provider", "Escolha Servidor (local) ou Google Drive.");

        var s = await FindAsync(tenantId);
        if (s is null) { s = new TenantStorageSettings { TenantId = tenantId }; db.StorageSettings.Add(s); }

        var credentialsChanged = false;
        if (!string.IsNullOrWhiteSpace(r.CredentialsJson))
        {
            s.CredentialsClientEmail = ValidateCredentials(r.CredentialsJson.Trim());
            s.CredentialsProtected = Protector.Protect(r.CredentialsJson.Trim());
            credentialsChanged = true;
        }
        else if (r.RemoveCredentials)
        {
            s.CredentialsProtected = null; s.CredentialsClientEmail = null;
            credentialsChanged = true;
        }

        var folder = ExtractFolderId(r.DriveFolder);
        var impersonate = string.IsNullOrWhiteSpace(r.ImpersonateUser) ? null : r.ImpersonateUser.Trim();
        if (impersonate is not null && !MailAddress.TryCreate(impersonate, out _)) throw new DomainException("invalid_email", "E-mail de delegação inválido.");
        if (provider == "gdrive")
        {
            if (folder is null) throw new DomainException("invalid_folder", "Informe a pasta do Google Drive onde os arquivos serão salvos.");
            if (s.CredentialsProtected is null) throw new DomainException("credentials_required", "Envie o JSON da conta de serviço (API do Google) para usar o Google Drive.");
        }

        s.Provider = provider;
        s.DriveFolderId = folder;
        s.ImpersonateUser = impersonate;
        s.Version++;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        s.UpdatedByAdminId = actor.AdminId;
        s.LastTestAt = null; s.LastTestOk = null; s.LastTestMessage = null; // configuração nova precisa ser testada de novo
        audit.Add("tenant.storage_updated", tenantId, nameof(TenantStorageSettings), tenantId,
            new { provider, folder, impersonate, clientEmail = s.CredentialsClientEmail, credentialsChanged, version = s.Version });
        await db.SaveChangesAsync();
        return View(s);
    }

    /// <summary>Testa o acesso: a pasta existe, é uma pasta e a conta de serviço pode gravar nela.</summary>
    public async Task<StorageSettingsView> TestAsync(Guid tenantId, CancellationToken ct)
    {
        var s = await FindAsync(tenantId) ?? throw new DomainException("not_configured", "Configure o armazenamento antes de testar.", 409);
        bool ok; string message;
        if (s.Provider != "gdrive") { ok = true; message = "Servidor local: não há conexão externa para testar."; }
        else
        {
            try
            {
                var f = await drive.GetFolderAsync(TargetOf(s), ct);
                (ok, message) = !f.IsFolder ? (false, "O ID informado não é uma pasta.")
                    : !f.CanAddChildren ? (false, $"A conta de serviço não pode gravar na pasta \"{f.Name}\". Compartilhe a pasta com {s.CredentialsClientEmail} como Editor.")
                    : (true, $"Conectado à pasta \"{f.Name}\".");
            }
            catch (Exception ex) when (ex is not DomainException and not OperationCanceledException)
            {
                ok = false;
                message = ex is Google.GoogleApiException g && g.HttpStatusCode == System.Net.HttpStatusCode.NotFound
                    ? $"Pasta não encontrada ou não compartilhada com {s.CredentialsClientEmail}."
                    : "Não foi possível conectar ao Google Drive com esta credencial.";
            }
        }
        s.LastTestAt = DateTimeOffset.UtcNow; s.LastTestOk = ok; s.LastTestMessage = message;
        audit.Add("tenant.storage_tested", tenantId, nameof(TenantStorageSettings), tenantId, new { ok, message });
        await db.SaveChangesAsync(ct);
        return View(s);
    }

    private DriveTarget TargetOf(TenantStorageSettings s) =>
        new(s.TenantId, s.Version, Protector.Unprotect(s.CredentialsProtected ?? throw new DomainException("credentials_required", "Credencial não configurada.", 409)),
            s.DriveFolderId ?? throw new DomainException("invalid_folder", "Pasta não configurada.", 409), s.ImpersonateUser);

    /// <summary>Configuração ativa da empresa logada (null = usar o padrão do servidor).</summary>
    public async Task<TenantStorageSettings?> ActiveForCurrentTenantAsync(CancellationToken ct)
    {
        var s = await db.StorageSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == actor.TenantId, ct);
        return s is { Provider: "gdrive", CredentialsProtected: not null, DriveFolderId: not null } ? s : null;
    }

    public async Task<string> SaveImageAsync(TenantStorageSettings s, Tenant tenant, Product product, byte[] bytes, string contentType, CancellationToken ct)
    {
        var ext = contentType == "image/png" ? "png" : "jpg";
        var id = await drive.UploadAsync(TargetOf(s), $"{product.Sku}-{Guid.NewGuid():N}.{ext}", bytes, contentType,
            new Dictionary<string, string> { ["tenantId"] = tenant.Id.ToString(), ["productId"] = product.Id.ToString() }, ct);
        return TenantDrivePrefix + id;
    }

    public async Task<byte[]?> ReadImageAsync(string key, CancellationToken ct)
    {
        var s = await db.StorageSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == actor.TenantId, ct);
        if (s?.CredentialsProtected is null || s.DriveFolderId is null) return null;
        var r = await drive.DownloadAsync(TargetOf(s), key[TenantDrivePrefix.Length..], ct);
        // Defesa extra: o arquivo precisa ter sido gravado para ESTA empresa.
        if (r is null || r.Value.AppProperties is null || !r.Value.AppProperties.TryGetValue("tenantId", out var t) || t != actor.TenantId.ToString()) return null;
        return r.Value.Bytes;
    }
}

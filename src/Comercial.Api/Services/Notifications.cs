using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Comercial.Domain;
using Comercial.Domain.Security;
using Comercial.Infrastructure.Data;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record PushResult(bool Ok, bool TokenInvalid, string? Error);

/// <summary>Envio de push para um aparelho. Implementação real: Firebase Cloud Messaging (Android e iOS via APNs).</summary>
public interface IPushSender
{
    bool Configured { get; }
    Task<PushResult> SendAsync(string token, string title, string body, IReadOnlyDictionary<string, string> data, CancellationToken ct);
}

public sealed class NotConfiguredPushSender : IPushSender
{
    public bool Configured => false;
    public Task<PushResult> SendAsync(string token, string title, string body, IReadOnlyDictionary<string, string> data, CancellationToken ct) =>
        Task.FromResult(new PushResult(false, false, "push_not_configured"));
}

/// <summary>
/// FCM HTTP v1. Configuração: Push:Fcm:ProjectId e Push:Fcm:CredentialsPath (JSON da conta de serviço do Firebase).
/// No iOS, cadastre a chave APNs no projeto Firebase. STATUS: NÃO testado contra o Firebase real (requer o projeto).
/// </summary>
public sealed class FcmPushSender(IConfiguration config, IHttpClientFactory http, ILogger<FcmPushSender> log) : IPushSender
{
    private readonly string? _project = config["Push:Fcm:ProjectId"];
    private readonly Lazy<ITokenAccess> _credential = new(() =>
    {
#pragma warning disable CS0618 // credencial vem de arquivo controlado pelo operador do servidor
        return (ITokenAccess)GoogleCredential.FromFile(config["Push:Fcm:CredentialsPath"]!).CreateScoped("https://www.googleapis.com/auth/firebase.messaging");
#pragma warning restore CS0618
    });
    public bool Configured => !string.IsNullOrWhiteSpace(_project) && !string.IsNullOrWhiteSpace(config["Push:Fcm:CredentialsPath"]);

    public async Task<PushResult> SendAsync(string token, string title, string body, IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var access = await _credential.Value.GetAccessTokenForRequestAsync(cancellationToken: ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{_project}/messages:send");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        req.Content = JsonContent.Create(new
        {
            message = new
            {
                token,
                notification = new { title, body },
                data,
                android = new { priority = "high", notification = new { channel_id = "alertas" } },
                apns = new { payload = new { aps = new { sound = "default" } } },
            },
        });
        using var res = await http.CreateClient("fcm").SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return new PushResult(true, false, null);
        var text = await res.Content.ReadAsStringAsync(ct);
        log.LogWarning("FCM recusou o envio: {Status} {Body}", (int)res.StatusCode, text.Length > 300 ? text[..300] : text);
        var invalid = res.StatusCode == System.Net.HttpStatusCode.NotFound || text.Contains("UNREGISTERED") || text.Contains("INVALID_ARGUMENT");
        return new PushResult(false, invalid, $"fcm_{(int)res.StatusCode}");
    }
}

/// <summary>Cria avisos para o dono e tenta entregá-los por push. Falha no push nunca desfaz a operação de origem.</summary>
public sealed class NotificationService(AppDbContext db, IPushSender push, ILogger<NotificationService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Notification> CreateAsync(Guid tenantId, string type, string title, string body, object? data, CancellationToken ct = default)
    {
        var n = new Notification { TenantId = tenantId, Type = type, Title = title, Body = body, DataJson = data is null ? null : JsonSerializer.Serialize(data, Json) };
        db.Notifications.Add(n);
        await db.SaveChangesAsync(ct);
        try { await DispatchAsync(n, ct); }
        catch (Exception ex)
        {
            log.LogError(ex, "Falha ao enviar push da notificação {Id}", n.Id);
            n.PushStatus = PushStatus.Failed;
            n.LastPushError = "exception";
            await db.SaveChangesAsync(ct);
        }
        return n;
    }

    public async Task DispatchAsync(Notification n, CancellationToken ct = default)
    {
        n.PushAttempts++;
        if (!push.Configured)
        {
            n.PushStatus = PushStatus.NoProvider;
            await db.SaveChangesAsync(ct);
            return;
        }
        var recipients = db.UserRoles.IgnoreQueryFilters().Where(r => r.TenantId == n.TenantId && r.Role == n.TargetRole).Select(r => r.UserId);
        var tokens = await db.PushTokens.IgnoreQueryFilters().Where(t => t.TenantId == n.TenantId && recipients.Contains(t.UserId)).ToListAsync(ct);
        if (tokens.Count == 0)
        {
            n.PushStatus = PushStatus.NoDevice;
            await db.SaveChangesAsync(ct);
            return;
        }
        var data = new Dictionary<string, string> { ["type"] = n.Type, ["notificationId"] = n.Id.ToString() };
        var ok = false;
        string? lastError = null;
        foreach (var t in tokens)
        {
            var r = await push.SendAsync(t.Token, n.Title, n.Body, data, ct);
            ok |= r.Ok;
            lastError = r.Error ?? lastError;
            if (r.TokenInvalid) db.PushTokens.Remove(t); // aparelho desinstalou o app ou trocou o token
        }
        n.PushStatus = ok ? PushStatus.Sent : PushStatus.Failed;
        n.PushedAt = ok ? DateTimeOffset.UtcNow : null;
        n.LastPushError = ok ? null : lastError;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Reenvia, em segundo plano, avisos cujo push falhou (até 5 tentativas).</summary>
public sealed class PushRetryService(IServiceProvider sp, ILogger<PushRetryService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { return; }
            try
            {
                using var scope = sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var svc = scope.ServiceProvider.GetRequiredService<NotificationService>();
                var pending = await db.Notifications.IgnoreQueryFilters()
                    .Where(n => n.PushStatus == PushStatus.Failed && n.PushAttempts < 5).OrderBy(n => n.CreatedAt).Take(50).ToListAsync(ct);
                foreach (var n in pending) await svc.DispatchAsync(n, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Falha no reenvio de push"); }
        }
    }
}

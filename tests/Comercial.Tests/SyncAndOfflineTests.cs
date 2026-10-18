using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Comercial.Desktop.Core;
using Comercial.Shared.Contracts;
using Comercial.Shared.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Comercial.Tests;

/// <summary>Fila offline, sincronização idempotente, horários e validação de licença no terminal.</summary>
public class SyncAndOfflineTests(ApiFactory f) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly Api api = new(f);
    private TenantInfo T = null!;
    private Guid device, product;
    private string deviceToken = null!;
    private string tempDir = null!;

    public async Task InitializeAsync()
    {
        T = await api.CreateTenant("sync-" + Guid.NewGuid().ToString("N")[..6]);
        device = await api.RegisterDevice(T.OwnerToken);
        deviceToken = await api.Login(T.Code, T.OwnerEmail, T.OwnerPassword, device);
        product = await api.CreateProduct(T.OwnerToken, "S1", price: 1000);
        await api.StockEntry(T.OwnerToken, T.StoreId, product, 100);
        tempDir = Directory.CreateTempSubdirectory("comercial-desktop-").FullName;
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(tempDir, true); } catch { }
        return Task.CompletedTask;
    }

    private async Task<(LocalDatabase Db, LicenseGuard Guard, ApiClient Client, string Pem)> Terminal()
    {
        var db = new LocalDatabase(Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".db"));
        var client = new ApiClient(api.Factory.CreateClient());
        await client.LoginAsync(T.Code, T.OwnerEmail, T.OwnerPassword, device);
        var pem = (await client.GetAsync("/api/v1/license/public-key")).GetProperty("pem").GetString()!;
        var guard = new LicenseGuard(db, pem, device);
        guard.Store(await client.GetLicenseGrantAsync());
        return (db, guard, client, pem);
    }

    [Fact]
    public async Task Fluxo_offline_completo_venda_na_fila_sincroniza_uma_unica_vez()
    {
        var (db, guard, client, _) = await Terminal();
        var cashRes = await client.PostAsync("/api/v1/cash/sessions", new { storeId = T.StoreId, openingCents = 0 });
        var cash = cashRes.GetProperty("id").GetGuid();
        var pos = new PosEngine(db, guard, TimeProvider.System);

        // "Sem internet": as operações só vão para a fila local.
        var k1 = pos.RecordSale(T.StoreId, cash, [new CartLine(product, "S1", 2, 1000)], [new SalePaymentRequest(1, 2000)]);
        var k2 = pos.RecordStoreEvent(T.StoreId, open: true);
        Assert.Equal(2, db.Pending(10).Count);
        Assert.Equal(0, await f.WithDb(x => x.Sales.IgnoreQueryFilters().CountAsync(s => s.IdempotencyKey == k1)));

        // "Volta a internet".
        var agent = new SyncAgent(db, client, device, TimeProvider.System);
        var r1 = await agent.RunOnceAsync();
        Assert.Equal(2, r1.Applied);
        Assert.Empty(db.Pending(10));

        // Reenvio forçado (ex.: falha antes de gravar o retorno localmente) não duplica.
        var replay = await client.SyncAsync(new SyncBatchRequest(device, DateTimeOffset.UtcNow,
            db.All().Select(i => new SyncOperation(i.IdempotencyKey, i.Type, i.LocalTime, JsonSerializer.Deserialize<JsonElement>(i.PayloadJson))).ToList()));
        Assert.All(replay.Results, r => Assert.Equal(SyncStatus.Duplicate, r.Status));
        Assert.Equal(1, await f.WithDb(x => x.Sales.IgnoreQueryFilters().CountAsync(s => s.IdempotencyKey == k1)));
        Assert.True(await f.WithDb(x => x.Sales.IgnoreQueryFilters().Where(s => s.IdempotencyKey == k1).Select(s => s.CreatedOffline).FirstAsync()));
        Assert.Equal(98m, await api.Balance(T.OwnerToken, T.StoreId, product));
        Assert.NotNull(await f.WithDb(x => x.Devices.IgnoreQueryFilters().Where(d => d.Id == device).Select(d => d.LastSyncAt).FirstAsync()));
        _ = k2;
    }

    [Fact]
    public async Task Operacao_rejeitada_fica_guardada_para_revisao_e_nao_e_reenviada()
    {
        var (db, guard, client, _) = await Terminal();
        var cash = (await client.PostAsync("/api/v1/cash/sessions", new { storeId = T.StoreId, openingCents = 0 })).GetProperty("id").GetGuid();
        var pos = new PosEngine(db, guard, TimeProvider.System);
        // Preço local desatualizado (servidor mudou o preço depois do cache).
        var key = pos.RecordSale(T.StoreId, cash, [new CartLine(product, "S1", 1, 1)], [new SalePaymentRequest(1, 1)]);
        var r = await new SyncAgent(db, client, device, TimeProvider.System).RunOnceAsync();
        Assert.Equal(1, r.Rejected);
        var item = db.All().Single(i => i.IdempotencyKey == key);
        Assert.Equal(OutboxStatus.Rejected, item.Status);
        Assert.Equal("price_mismatch", item.LastError);
        Assert.True(await f.WithDb(x => x.AuditLogs.AnyAsync(a => a.Action == "security.price_mismatch" && a.TenantId == T.Id)));
    }

    [Fact]
    public async Task Sync_com_token_de_outro_dispositivo_e_recusado()
    {
        var other = await api.RegisterDevice(T.OwnerToken, "Caixa 2");
        var res = await api.Post("/api/v1/sync/batch", deviceToken, new SyncBatchRequest(other, DateTimeOffset.UtcNow, []));
        Assert.Equal("device_mismatch", await Api.Code(res));
    }

    [Fact]
    public async Task Horario_futuro_e_relogio_suspeito_sao_detectados_e_correcao_preserva_original()
    {
        var future = new OperatingEventRequest("fut-" + Guid.NewGuid().ToString("N"), T.StoreId, 1, DateTimeOffset.UtcNow.AddHours(3), true);
        var batch = await Api.Expect(await api.Post("/api/v1/sync/batch", deviceToken,
            new SyncBatchRequest(device, DateTimeOffset.UtcNow.AddHours(3), [new SyncOperation(future.IdempotencyKey, SyncTypes.OperatingEvent, future.LocalTime, JsonSerializer.SerializeToElement(future, Api.Json))])), HttpStatusCode.OK);
        Assert.True(batch.GetProperty("clockSkewDetected").GetBoolean());
        Assert.Equal("rejected", batch.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal("clock_in_future", batch.GetProperty("results")[0].GetProperty("error").GetString());

        // Evento online com relógio 2h atrasado: aceito, mas marcado como suspeito.
        var ev = await Api.Expect(await api.Post("/api/v1/operating/events", T.OwnerToken,
            new OperatingEventRequest("late-" + Guid.NewGuid().ToString("N"), T.StoreId, 1, DateTimeOffset.UtcNow.AddHours(-2))), HttpStatusCode.OK);
        Assert.True(ev.GetProperty("clockSuspect").GetBoolean());
        var id = ev.GetProperty("id").GetGuid();

        var (_, op, _) = await api.CreateUser(T, "operator");
        var correction = new { idempotencyKey = "cor-" + Guid.NewGuid().ToString("N"), correctedType = 1, correctedLocalTime = DateTimeOffset.UtcNow, justification = "relógio do caixa estava errado" };
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Post($"/api/v1/operating/events/{id}/correct", op, correction)).StatusCode);
        await Api.Expect(await api.Post($"/api/v1/operating/events/{id}/correct", T.OwnerToken, correction), HttpStatusCode.OK);
        var original = await f.WithDb(x => x.OperatingEvents.IgnoreQueryFilters().FirstAsync(e => e.Id == id));
        Assert.True(original.LocalTime < DateTimeOffset.UtcNow.AddHours(-1)); // original intacto
        var status = await Api.Expect(await api.Get($"/api/v1/operating/status?storeId={T.StoreId}", T.OwnerToken), HttpStatusCode.OK);
        Assert.Equal("open", status.GetProperty("state").GetString());
        Assert.Contains("recebidos pelo servidor", status.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Licenca_offline_assinada_resiste_a_adulteracao_do_banco_local()
    {
        var (db, guard, _, _) = await Terminal();
        var ok = guard.Evaluate(DateTimeOffset.UtcNow);
        Assert.True(ok.CanOperate, ok.Reason);
        Assert.Contains("pdv", ok.Grant!.Modules);
        Assert.DoesNotContain("crm", ok.Grant.Modules);

        // Atacante edita o banco local para incluir um módulo não contratado.
        var token = db.Get(LicenseGuard.GrantKey)!;
        var parts = token.Split('.');
        var json = System.Text.Encoding.UTF8.GetString(LicenseGrantCodec.FromB64Url(parts[0])).Replace("\"pdv\"", "\"pdv\",\"crm\"");
        db.Set(LicenseGuard.GrantKey, LicenseGrantCodec.B64Url(System.Text.Encoding.UTF8.GetBytes(json)) + "." + parts[1]);
        var tampered = guard.Evaluate(DateTimeOffset.UtcNow);
        Assert.False(tampered.CanOperate);
        Assert.Equal("invalid_signature", tampered.Reason);

        // Assinatura com chave própria do atacante também falha (cliente só confia na chave do servidor).
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = LicenseGrantCodec.Sign(ok.Grant with { Modules = [.. ok.Grant.Modules, "crm"], OfflineValidUntil = DateTimeOffset.UtcNow.AddYears(5) }, attacker);
        db.Set(LicenseGuard.GrantKey, forged);
        Assert.Equal("invalid_signature", guard.Evaluate(DateTimeOffset.UtcNow).Reason);
    }

    [Fact]
    public async Task Licenca_offline_expira_detecta_relogio_retrocedido_e_dispositivo_errado()
    {
        var (db, guard, _, pem) = await Terminal();
        var now = DateTimeOffset.UtcNow;
        Assert.True(guard.Evaluate(now).CanOperate);
        Assert.Equal("offline_expired", guard.Evaluate(now.AddHours(73)).Reason);
        // Depois de ver o relógio no futuro, voltar o relógio é detectado.
        Assert.Equal("clock_rollback", guard.Evaluate(now.AddHours(1)).Reason);

        var otherDb = new LocalDatabase(Path.Combine(tempDir, "outro.db"));
        otherDb.Set(LicenseGuard.GrantKey, db.Get(LicenseGuard.GrantKey)!);
        var otherDevice = new LicenseGuard(otherDb, pem, Guid.NewGuid());
        Assert.Equal("device_mismatch", otherDevice.Evaluate(now).Reason);

        var pos = new PosEngine(db, guard, new FakeTimeProvider(now.AddHours(73)));
        Assert.Throws<InvalidOperationException>(() => pos.RecordSale(T.StoreId, Guid.NewGuid(), [new CartLine(product, "x", 1, 1000)], [new SalePaymentRequest(1, 1000)]));
    }

    [Fact]
    public async Task Licenca_restrita_no_servidor_reflete_na_concessao_e_bloqueia_venda_local()
    {
        var t = await api.CreateTenant("syncr-" + Guid.NewGuid().ToString("N")[..6]);
        var d = await api.RegisterDevice(t.OwnerToken);
        await f.WithDb(async x =>
        {
            var sub = await x.Subscriptions.IgnoreQueryFilters().FirstAsync(s => s.TenantId == t.Id);
            sub.CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(-30);
            await x.SaveChangesAsync();
        });
        var client = new ApiClient(api.Factory.CreateClient());
        await client.LoginAsync(t.Code, t.OwnerEmail, t.OwnerPassword, d);
        var pem = (await client.GetAsync("/api/v1/license/public-key")).GetProperty("pem").GetString()!;
        var db = new LocalDatabase(Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".db"));
        var guard = new LicenseGuard(db, pem, d);
        guard.Store(await client.GetLicenseGrantAsync());
        Assert.Equal("license_restricted", guard.Evaluate(DateTimeOffset.UtcNow).Reason);
        // Banco local preservado: nada é apagado.
        Assert.NotNull(db.Get(LicenseGuard.GrantKey));
    }

    [Fact]
    public void Fila_local_persiste_apos_reinicio_e_nao_duplica_chave()
    {
        var path = Path.Combine(tempDir, "restart.db");
        var db = new LocalDatabase(path);
        Assert.True(db.Enqueue("k1", SyncTypes.SaleCreate, "{}", DateTimeOffset.UtcNow));
        Assert.False(db.Enqueue("k1", SyncTypes.SaleCreate, "{}", DateTimeOffset.UtcNow));
        var reopened = new LocalDatabase(path); // simula reinício do aplicativo
        Assert.Single(reopened.Pending(10));
    }
}

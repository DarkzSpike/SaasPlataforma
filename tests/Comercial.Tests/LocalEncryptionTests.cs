using Comercial.Desktop.Core;
using Comercial.Shared.Contracts;
using Microsoft.Data.Sqlite;

namespace Comercial.Tests;

/// <summary>Banco local do caixa criptografado (SQLCipher): sem a chave ninguém lê a fila de vendas.</summary>
public class LocalEncryptionTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "cipher-" + Guid.NewGuid().ToString("N"));
    public LocalEncryptionTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    private const string Key = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";

    [Fact]
    public void Banco_com_chave_fica_ilegivel_sem_ela()
    {
        var path = Path.Combine(dir, "c.db");
        var db = new LocalDatabase(path, Key);
        Assert.True(db.Enqueue("venda-1", SyncTypes.SaleCreate, "{\"segredo\":\"cliente\"}", DateTimeOffset.UtcNow));
        Assert.False(LocalDatabase.IsPlainSqlite(path));
        Assert.DoesNotContain("segredo", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.ThrowsAny<SqliteException>(() => new LocalDatabase(path, "chave-errada"));
        Assert.Single(new LocalDatabase(path, Key).Pending(10)); // reabre com a chave certa
    }

    [Fact]
    public void Banco_antigo_sem_criptografia_e_convertido_sem_perder_vendas()
    {
        var path = Path.Combine(dir, "antigo.db");
        var old = new LocalDatabase(path);
        old.Enqueue("venda-antiga", SyncTypes.SaleCreate, "{}", DateTimeOffset.UtcNow);
        Assert.True(LocalDatabase.IsPlainSqlite(path));
        var db = new LocalDatabase(path, Key);
        Assert.False(LocalDatabase.IsPlainSqlite(path));
        Assert.Equal("venda-antiga", Assert.Single(db.Pending(10)).IdempotencyKey);
        Assert.False(File.Exists(path + ".plain-old"));
    }

    [Fact]
    public void Chave_protegida_pelo_windows_e_estavel()
    {
        if (!OperatingSystem.IsWindows()) return;
        var keyPath = Path.Combine(dir, "local.key");
        var k1 = LocalDbKey.GetOrCreate(keyPath);
        Assert.Equal(64, k1!.Length);
        Assert.Equal(k1, LocalDbKey.GetOrCreate(keyPath));
        Assert.DoesNotContain(k1, Convert.ToHexString(File.ReadAllBytes(keyPath))); // não fica em texto puro
    }
}

using Microsoft.Data.Sqlite;

namespace Comercial.Desktop.Core;

public sealed record OutboxItem(long Id, string IdempotencyKey, string Type, string PayloadJson, DateTimeOffset LocalTime, string Status, int Attempts, string? LastError);

public static class OutboxStatus
{
    public const string Pending = "pending";
    public const string Synced = "synced";
    /// <summary>Recusada pelo servidor: fica guardada para revisão/conciliação, nunca é descartada.</summary>
    public const string Rejected = "rejected";
}

/// <summary>
/// Banco local SQLite do terminal. Guarda fila de operações (outbox), cache de catálogo,
/// licença assinada e identidade visual. NÃO é fonte de autorização: módulos e licença
/// só valem se a concessão assinada pelo servidor for verificada.
/// </summary>
public sealed class LocalDatabase
{
    private readonly string _cs;

    public LocalDatabase(string path)
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS outbox(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                idempotency_key TEXT NOT NULL UNIQUE,
                type TEXT NOT NULL,
                payload TEXT NOT NULL,
                local_time TEXT NOT NULL,
                status TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_outbox_status ON outbox(status, id);
            CREATE TABLE IF NOT EXISTS kv(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS products(id TEXT PRIMARY KEY, sku TEXT, barcode TEXT, name TEXT, price_cents INTEGER, unit TEXT);
            CREATE INDEX IF NOT EXISTS ix_products_barcode ON products(barcode);
            """);
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Insere na fila em transação. Chave repetida não duplica (INSERT OR IGNORE).</summary>
    public bool Enqueue(string idempotencyKey, string type, string payloadJson, DateTimeOffset localTime)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO outbox(idempotency_key,type,payload,local_time,status,created_at) VALUES($k,$t,$p,$l,$s,$c)";
        cmd.Parameters.AddWithValue("$k", idempotencyKey);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$p", payloadJson);
        cmd.Parameters.AddWithValue("$l", localTime.ToString("O"));
        cmd.Parameters.AddWithValue("$s", OutboxStatus.Pending);
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
        var n = cmd.ExecuteNonQuery();
        tx.Commit();
        return n == 1;
    }

    public List<OutboxItem> Pending(int max)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,idempotency_key,type,payload,local_time,status,attempts,last_error FROM outbox WHERE status=$s ORDER BY id LIMIT $m";
        cmd.Parameters.AddWithValue("$s", OutboxStatus.Pending);
        cmd.Parameters.AddWithValue("$m", max);
        return Read(cmd);
    }

    public List<OutboxItem> All()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,idempotency_key,type,payload,local_time,status,attempts,last_error FROM outbox ORDER BY id";
        return Read(cmd);
    }

    private static List<OutboxItem> Read(SqliteCommand cmd)
    {
        var list = new List<OutboxItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new OutboxItem(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), DateTimeOffset.Parse(r.GetString(4)),
                r.GetString(5), r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return list;
    }

    public void MarkResult(string key, string status, string? error)
    {
        using var c = Open();
        Exec(c, "UPDATE outbox SET status=$s, last_error=$e, attempts=attempts+1 WHERE idempotency_key=$k", null, ("$s", status), ("$e", error), ("$k", key));
    }

    public string? Get(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM kv WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var c = Open();
        Exec(c, "INSERT INTO kv(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", null, ("$k", key), ("$v", value));
    }

    public void ReplaceProducts(IEnumerable<(Guid Id, string Sku, string? Barcode, string Name, long PriceCents, string Unit)> items)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM products", tx);
        foreach (var p in items)
            Exec(c, "INSERT INTO products(id,sku,barcode,name,price_cents,unit) VALUES($i,$s,$b,$n,$p,$u)", tx,
                ("$i", p.Id.ToString()), ("$s", p.Sku), ("$b", p.Barcode), ("$n", p.Name), ("$p", p.PriceCents), ("$u", p.Unit));
        tx.Commit();
    }

    public (Guid Id, string Name, long PriceCents)? FindProduct(string code)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,price_cents FROM products WHERE barcode=$c OR sku=$c LIMIT 1";
        cmd.Parameters.AddWithValue("$c", code);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (Guid.Parse(r.GetString(0)), r.GetString(1), r.GetInt64(2)) : null;
    }
}

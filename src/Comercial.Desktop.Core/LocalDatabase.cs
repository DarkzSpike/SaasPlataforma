using Microsoft.Data.Sqlite;

namespace Comercial.Desktop.Core;

public sealed record OutboxItem(long Id, string IdempotencyKey, string Type, string PayloadJson, DateTimeOffset LocalTime, string Status, int Attempts, string? LastError);

public static class OutboxStatus
{
    public const string Pending = "pending";
    public const string Synced = "synced";
    /// <summary>Recusada pelo servidor: fica guardada para revisão/conciliação, nunca é descartada.</summary>
    public const string Rejected = "rejected";
    /// <summary>Recusada e depois registrada novamente (com preço atual) por um gerente.</summary>
    public const string Resolved = "resolved";
    /// <summary>Recusada e descartada por um gerente, com motivo registrado na auditoria do servidor.</summary>
    public const string Discarded = "discarded";
}

/// <summary>
/// Banco local SQLite do terminal. Guarda fila de operações (outbox), cache de catálogo,
/// licença assinada e identidade visual. NÃO é fonte de autorização: módulos e licença
/// só valem se a concessão assinada pelo servidor for verificada.
/// </summary>
public sealed class LocalDatabase
{
    private readonly string _cs;

    /// <summary>Indica se o arquivo está criptografado (SQLCipher) — usado no diagnóstico e nos testes.</summary>
    public bool Encrypted { get; }

    /// <param name="key">Chave do SQLCipher. Com chave, um banco antigo sem criptografia é convertido na primeira abertura.</param>
    public LocalDatabase(string path, string? key = null)
    {
        EnsureCipherProvider();
        if (!string.IsNullOrEmpty(key) && File.Exists(path) && IsPlainSqlite(path)) EncryptExisting(path, key);
        var b = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        if (!string.IsNullOrEmpty(key)) b.Password = key;
        _cs = b.ToString();
        Encrypted = !string.IsNullOrEmpty(key);
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
            CREATE TABLE IF NOT EXISTS product_codes(code TEXT PRIMARY KEY, product_id TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_products_barcode ON products(barcode);
            """);
    }

    private static readonly object ProviderLock = new();
    private static bool _providerSet;

    /// <summary>
    /// Usa o motor SQLCipher (SQLite com criptografia). O Microsoft.Data.Sqlite inicializa o provedor padrão no
    /// primeiro uso; por isso ele é tocado antes e o provedor é trocado em seguida (sem chave o SQLCipher se
    /// comporta como SQLite comum, então outros bancos do mesmo processo continuam funcionando).
    /// </summary>
    public static void EnsureCipherProvider()
    {
        lock (ProviderLock)
        {
            if (_providerSet) return;
            using (new SqliteConnection()) { }
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());
            _providerSet = true;
        }
    }

    /// <summary>Banco SQLite comum começa com o cabeçalho "SQLite format 3"; o SQLCipher grava tudo cifrado.</summary>
    public static bool IsPlainSqlite(string path)
    {
        var head = new byte[16];
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return f.Read(head, 0, 16) == 16 && System.Text.Encoding.ASCII.GetString(head, 0, 15) == "SQLite format 3";
    }

    /// <summary>Converte um banco antigo (sem criptografia) para SQLCipher sem perder a fila de vendas.</summary>
    private static void EncryptExisting(string path, string key)
    {
        var tmp = path + ".enc-tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            c.Open();
            using (var w = c.CreateCommand()) { w.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);"; w.ExecuteNonQuery(); }
            using var cmd = c.CreateCommand();
            cmd.CommandText = "ATTACH DATABASE $p AS enc KEY $k; SELECT sqlcipher_export('enc'); DETACH DATABASE enc;";
            cmd.Parameters.AddWithValue("$p", tmp);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }
        var backup = path + ".plain-old";
        File.Move(path, backup, true);
        foreach (var ext in new[] { "-wal", "-shm" }) if (File.Exists(path + ext)) File.Delete(path + ext);
        File.Move(tmp, path);
        File.Delete(backup); // a cópia antiga em texto puro não fica no disco
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

    /// <summary>Códigos de barras adicionais (caixa, código antigo do fornecedor...).</summary>
    public void ReplaceCodes(IEnumerable<(string Code, Guid ProductId)> codes)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM product_codes", tx);
        foreach (var (code, id) in codes) Exec(c, "INSERT OR REPLACE INTO product_codes(code,product_id) VALUES($c,$p)", tx, ("$c", code), ("$p", id.ToString()));
        tx.Commit();
    }

    /// <summary>Busca no catálogo local por nome (parcial), SKU ou código de barras (exatos).</summary>
    public List<(Guid Id, string Sku, string? Barcode, string Name, long PriceCents)> SearchProducts(string term, int max)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,sku,barcode,name,price_cents FROM products WHERE barcode=$c OR sku=$c OR id IN (SELECT product_id FROM product_codes WHERE code=$c) OR name LIKE $l ESCAPE '\\' ORDER BY name LIMIT $m";
        cmd.Parameters.AddWithValue("$c", term);
        cmd.Parameters.AddWithValue("$l", "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        cmd.Parameters.AddWithValue("$m", max);
        var list = new List<(Guid, string, string?, string, long)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((Guid.Parse(r.GetString(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetInt64(4)));
        return list;
    }

    /// <summary>Produto pelo código da balança (PLU): compara com o SKU, ignorando zeros à esquerda.</summary>
    public (Guid Id, string Name, long PriceCents, string Unit)? FindByPlu(string plu)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,price_cents,unit,sku FROM products";
        using var r = cmd.ExecuteReader();
        var target = plu.TrimStart('0');
        while (r.Read())
            if (r.GetString(4).TrimStart('0') == target && target.Length > 0)
                return (Guid.Parse(r.GetString(0)), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? "UN" : r.GetString(3));
        return null;
    }

    public string UnitOf(Guid id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT unit FROM products WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id.ToString());
        return cmd.ExecuteScalar() as string ?? "UN";
    }

    public (string Name, long PriceCents)? GetProduct(Guid id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name,price_cents FROM products WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id.ToString());
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetInt64(1)) : null;
    }

    public (Guid Id, string Name, long PriceCents)? FindProduct(string code)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,price_cents FROM products WHERE barcode=$c OR sku=$c OR id IN (SELECT product_id FROM product_codes WHERE code=$c) LIMIT 1";
        cmd.Parameters.AddWithValue("$c", code);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (Guid.Parse(r.GetString(0)), r.GetString(1), r.GetInt64(2)) : null;
    }
}

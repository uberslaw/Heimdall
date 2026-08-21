using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Heimdall.Shared.Contracts;
using Microsoft.Data.Sqlite;

namespace Heimdall.Agent.Services;

/// <summary>
/// Durable offline store for telemetry the API never received (ingest batches + fleet snapshots).
/// Payloads are gzip-compressed JSON; oldest rows are dropped when the DB exceeds
/// <see cref="MaxBytes"/> (~500 MB).
/// </summary>
public sealed class OfflineQueue
{
    public const long DefaultMaxBytes = 500L * 1024 * 1024;
    public const string KindIngest = "ingest";
    public const string KindFleet = "fleet";
    public const string KindResourceSample = "resource";

    private readonly string _dbPath;
    private readonly long _maxBytes;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly byte[] GzipMagic = [0x1f, 0x8b];

    public OfflineQueue(string dbPath, long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _maxBytes = Math.Max(16L * 1024 * 1024, maxBytes);
        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        EnsureSchema();
    }

    public long MaxBytes => _maxBytes;

    public void EnqueueIngest(IngestBatchDto batch) =>
        Enqueue(KindIngest, JsonSerializer.SerializeToUtf8Bytes(batch, JsonOptions));

    public void EnqueueFleet(FleetSnapshotDto dto) =>
        Enqueue(KindFleet, JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions));

    public void EnqueueResourceSample(ResourceSampleReportDto dto) =>
        Enqueue(KindResourceSample, JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions));

    public List<QueuedItem> Peek(int take = 20)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, kind, payload FROM queue ORDER BY id LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", take);
        using var reader = cmd.ExecuteReader();
        var list = new List<QueuedItem>();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var kind = reader.IsDBNull(1) ? KindIngest : reader.GetString(1);
            var blob = ReadPayloadBlob(reader, 2);
            if (blob is null || blob.Length == 0)
                continue;
            try
            {
                var json = DecompressToUtf8(blob);
                list.Add(new QueuedItem(id, kind, json));
            }
            catch
            {
                // Leave poison for drain to delete after permanent failure path.
                list.Add(new QueuedItem(id, kind, null, Corrupt: true));
            }
        }
        return list;
    }

    public void Remove(IEnumerable<long> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var id in idList)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM queue WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public (int Count, long ApproxBytes) GetStats()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), IFNULL(SUM(LENGTH(payload)), 0) FROM queue;";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return (0, 0);
        return (reader.GetInt32(0), reader.GetInt64(1));
    }

    private void Enqueue(string kind, byte[] utf8Json)
    {
        var compressed = Gzip(utf8Json);
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO queue (kind, payload, created_utc, payload_bytes) VALUES ($k, $p, $c, $b);";
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$p", compressed);
            cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$b", compressed.LongLength);
            cmd.ExecuteNonQuery();
        }

        TrimToBudget(conn);
    }

    private void TrimToBudget(SqliteConnection conn)
    {
        // Prefer SUM(payload_bytes); fall back to LENGTH for legacy rows.
        long used;
        using (var sum = conn.CreateCommand())
        {
            sum.CommandText = """
                SELECT IFNULL(SUM(COALESCE(payload_bytes, LENGTH(payload))), 0) FROM queue;
                """;
            used = (long)(sum.ExecuteScalar() ?? 0L);
        }

        if (used <= _maxBytes)
            return;

        // Drop oldest until under ~90% of budget (hysteresis).
        var target = (long)(_maxBytes * 0.9);
        while (used > target)
        {
            long? oldestId;
            long oldestBytes;
            using (var peek = conn.CreateCommand())
            {
                peek.CommandText = """
                    SELECT id, COALESCE(payload_bytes, LENGTH(payload))
                    FROM queue ORDER BY id LIMIT 1;
                    """;
                using var reader = peek.ExecuteReader();
                if (!reader.Read())
                    break;
                oldestId = reader.GetInt64(0);
                oldestBytes = reader.GetInt64(1);
            }

            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM queue WHERE id = $id;";
                del.Parameters.AddWithValue("$id", oldestId!.Value);
                del.ExecuteNonQuery();
            }

            used -= oldestBytes;
        }
    }

    private void EnsureSchema()
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS queue (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  payload BLOB NOT NULL,
                  created_utc TEXT NOT NULL,
                  kind TEXT NOT NULL DEFAULT 'ingest',
                  payload_bytes INTEGER NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        TryExec(conn, "ALTER TABLE queue ADD COLUMN kind TEXT NOT NULL DEFAULT 'ingest'");
        TryExec(conn, "ALTER TABLE queue ADD COLUMN payload_bytes INTEGER NULL");

        // Migrate legacy TEXT payloads: leave as-is; readers accept UTF-8 text or gzip.
        using (var backfill = conn.CreateCommand())
        {
            backfill.CommandText = """
                UPDATE queue SET payload_bytes = LENGTH(payload)
                WHERE payload_bytes IS NULL;
                """;
            backfill.ExecuteNonQuery();
        }
    }

    private static void TryExec(SqliteConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Column already exists.
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private static byte[] Gzip(byte[] utf8)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(utf8, 0, utf8.Length);
        return ms.ToArray();
    }

    private static byte[] DecompressToUtf8(byte[] blob)
    {
        if (blob.Length >= 2 && blob[0] == GzipMagic[0] && blob[1] == GzipMagic[1])
        {
            using var input = new MemoryStream(blob);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            return output.ToArray();
        }

        // Legacy uncompressed UTF-8 JSON text stored as TEXT/BLOB.
        return blob;
    }

    private static byte[]? ReadPayloadBlob(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        if (reader.GetFieldType(ordinal) == typeof(string))
            return Encoding.UTF8.GetBytes(reader.GetString(ordinal));
        return (byte[])reader.GetValue(ordinal);
    }

    public sealed record QueuedItem(long Id, string Kind, byte[]? Utf8Json, bool Corrupt = false)
    {
        public IngestBatchDto? AsIngest(JsonSerializerOptions? options = null) =>
            Utf8Json is null ? null : JsonSerializer.Deserialize<IngestBatchDto>(Utf8Json, options ?? JsonOptions);

        public FleetSnapshotDto? AsFleet(JsonSerializerOptions? options = null) =>
            Utf8Json is null ? null : JsonSerializer.Deserialize<FleetSnapshotDto>(Utf8Json, options ?? JsonOptions);

        public ResourceSampleReportDto? AsResource(JsonSerializerOptions? options = null) =>
            Utf8Json is null ? null : JsonSerializer.Deserialize<ResourceSampleReportDto>(Utf8Json, options ?? JsonOptions);
    }
}

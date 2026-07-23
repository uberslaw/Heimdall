using System.Text.Json;
using Heimdall.Shared.Contracts;
using Microsoft.Data.Sqlite;

namespace Heimdall.Agent.Services;

public sealed class OfflineQueue
{
    private readonly string _dbPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public OfflineQueue(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS queue (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              payload TEXT NOT NULL,
              created_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Enqueue(IngestBatchDto batch)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO queue (payload, created_utc) VALUES ($p, $c);";
        cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(batch, JsonOptions));
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public List<(long Id, IngestBatchDto Batch)> Peek(int take = 20)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, payload FROM queue ORDER BY id LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", take);
        using var reader = cmd.ExecuteReader();
        var list = new List<(long, IngestBatchDto)>();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var json = reader.GetString(1);
            var batch = JsonSerializer.Deserialize<IngestBatchDto>(json, JsonOptions);
            if (batch is not null)
                list.Add((id, batch));
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

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}

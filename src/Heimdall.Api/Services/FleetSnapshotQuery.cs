using System.Data;
using System.Globalization;
using System.Text;
using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Date-bounded fleet snapshot loads and retention purge.
/// SQLite + EF cannot reliably translate DateTimeOffset comparisons, so bounds are applied in SQL
/// against the TEXT column (ISO-8601 lexicographic order matches chronological order for UTC offsets).
/// </summary>
public static class FleetSnapshotQuery
{
    /// <summary>Format matching EF Core SQLite DateTimeOffset storage for TEXT comparisons.</summary>
    public static string ToSqliteText(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);

    public static async Task<List<FleetMetricSnapshot>> LoadForMachinesAsync(
        HeimdallDbContext db,
        IReadOnlyCollection<int> machineIds,
        DateTimeOffset fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken ct)
    {
        if (machineIds.Count == 0)
            return [];

        var ids = machineIds.Distinct().ToList();
        var fromText = ToSqliteText(fromUtc);
        var toText = toUtc is null ? null : ToSqliteText(toUtc.Value);

        // SQLite default variable limit is 999 — keep headroom for from/to params.
        const int chunkSize = 400;
        var result = new List<FleetMetricSnapshot>();

        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        for (var offset = 0; offset < ids.Count; offset += chunkSize)
        {
            var chunk = ids.Skip(offset).Take(chunkSize).ToList();
            await using var cmd = conn.CreateCommand();
            var sb = new StringBuilder();
            sb.Append("""
                SELECT Id, SampledAtUtc, MachineId, Username, TuflowRunning,
                       CpuPercent, GpuPercent, GpuMemoryUsedMb, RamUsedMb,
                       DiskReadMBps, DiskWriteMBps, NetworkInMBps, NetworkOutMBps,
                       ProcessCpuPercent, ProcessGpuPercent, ProcessDiskReadMBps, ProcessDiskWriteMBps,
                       IsActive,
                       TopCpuProcessesJson, TopGpuProcessesJson,
                       TopDiskReadProcessesJson, TopDiskWriteProcessesJson,
                       TuflowInstanceCount, ClaimedHpcSeats, ClaimedClassicSeats, TuflowClaimDetail
                FROM FleetMetricSnapshots
                WHERE MachineId IN (
                """);

            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var name = "@m" + i;
                sb.Append(name);
                var p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = chunk[i];
                cmd.Parameters.Add(p);
            }

            sb.Append(") AND SampledAtUtc >= @from");
            var fromParam = cmd.CreateParameter();
            fromParam.ParameterName = "@from";
            fromParam.Value = fromText;
            cmd.Parameters.Add(fromParam);

            if (toText is not null)
            {
                sb.Append(" AND SampledAtUtc < @to");
                var toParam = cmd.CreateParameter();
                toParam.ParameterName = "@to";
                toParam.Value = toText;
                cmd.Parameters.Add(toParam);
            }

            sb.Append(" ORDER BY SampledAtUtc");
            cmd.CommandText = sb.ToString();

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(ReadSnapshot(reader));
        }

        if (ids.Count > chunkSize)
            result = result.OrderBy(s => s.SampledAtUtc).ToList();

        return result;
    }

    public static async Task<int> PurgeOlderThanAsync(
        HeimdallDbContext db,
        DateTimeOffset cutoffUtc,
        CancellationToken ct)
    {
        var cutoffText = ToSqliteText(cutoffUtc);
        return await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM FleetMetricSnapshots WHERE SampledAtUtc < {0}",
            cutoffText);
    }

    private static FleetMetricSnapshot ReadSnapshot(System.Data.Common.DbDataReader reader)
    {
        // Older DBs may lack top-process / claim columns until schema patch; ordinals optional.
        string ReadJson(int ordinal) =>
            reader.FieldCount > ordinal && !reader.IsDBNull(ordinal)
                ? reader.GetString(ordinal)
                : "[]";

        int? ReadInt(int ordinal) =>
            reader.FieldCount > ordinal && !reader.IsDBNull(ordinal)
                ? Convert.ToInt32(reader.GetValue(ordinal))
                : null;

        string? ReadString(int ordinal) =>
            reader.FieldCount > ordinal && !reader.IsDBNull(ordinal)
                ? reader.GetString(ordinal)
                : null;

        return new FleetMetricSnapshot
        {
            Id = reader.GetInt64(0),
            SampledAtUtc = ParseDto(reader.GetString(1)),
            MachineId = reader.GetInt32(2),
            Username = reader.IsDBNull(3) ? null : reader.GetString(3),
            TuflowRunning = reader.GetBoolean(4),
            CpuPercent = reader.IsDBNull(5) ? null : reader.GetDouble(5),
            GpuPercent = reader.IsDBNull(6) ? null : reader.GetDouble(6),
            GpuMemoryUsedMb = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            RamUsedMb = reader.IsDBNull(8) ? null : reader.GetDouble(8),
            DiskReadMBps = reader.IsDBNull(9) ? null : reader.GetDouble(9),
            DiskWriteMBps = reader.IsDBNull(10) ? null : reader.GetDouble(10),
            NetworkInMBps = reader.IsDBNull(11) ? null : reader.GetDouble(11),
            NetworkOutMBps = reader.IsDBNull(12) ? null : reader.GetDouble(12),
            ProcessCpuPercent = reader.IsDBNull(13) ? null : reader.GetDouble(13),
            ProcessGpuPercent = reader.IsDBNull(14) ? null : reader.GetDouble(14),
            ProcessDiskReadMBps = reader.IsDBNull(15) ? null : reader.GetDouble(15),
            ProcessDiskWriteMBps = reader.IsDBNull(16) ? null : reader.GetDouble(16),
            IsActive = reader.GetBoolean(17),
            TopCpuProcessesJson = ReadJson(18),
            TopGpuProcessesJson = ReadJson(19),
            TopDiskReadProcessesJson = ReadJson(20),
            TopDiskWriteProcessesJson = ReadJson(21),
            TuflowInstanceCount = ReadInt(22),
            ClaimedHpcSeats = ReadInt(23),
            ClaimedClassicSeats = ReadInt(24),
            TuflowClaimDetail = ReadString(25)
        };
    }

    private static DateTimeOffset ParseDto(string text)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto;
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
    }
}

using System.Data;
using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Queues fleet/weekly disk usage scans via the existing <see cref="Machine.PendingDiskUsageScanJson"/> path
/// (same pickup as Machine detail Scan — no separate command token).
/// Last weekly run stamp is stored in SystemFlags (same pattern as PublishedVersionService).
/// </summary>
public sealed class StorageScanService(
    HeimdallDbContext db,
    IConfiguration configuration,
    ILogger<StorageScanService> logger)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <summary>Fleet weekly defaults: top 5 non-system folders, top 5 files ≥ 1 GB, hotspots on.</summary>
    public const int FleetMinFileMb = 1024;
    public const int FleetTopFolderCount = 5;
    public const int FleetMaxLargeFiles = 5;
    public const int FleetMaxSeconds = 180;

    public StorageScanOptions GetOptions()
    {
        var section = configuration.GetSection("Heimdall:StorageScan");
        return new StorageScanOptions
        {
            Enabled = section.GetValue("Enabled", true),
            DayOfWeek = ParseDayOfWeek(section.GetValue<string>("DayOfWeek"), DayOfWeek.Sunday),
            TimeUtc = ParseTimeUtc(section.GetValue<string>("TimeUtc"), new TimeOnly(6, 0)),
            MinAgentVersion = section.GetValue("MinAgentVersion", VersionCompare.MinDiskUsageScanVersion),
            RootPath = NormalizeRoot(section.GetValue<string>("RootPath") ?? @"C:\"),
            MaxSeconds = Math.Clamp(section.GetValue("MaxSeconds", FleetMaxSeconds), 30, 600),
            PollMinutes = Math.Max(1, section.GetValue("PollMinutes", 15)),
            InitialDelaySeconds = Math.Max(0, section.GetValue("InitialDelaySeconds", 90))
        };
    }

    /// <summary>
    /// Queue a fleet-profile disk scan on hosts that support it and do not already have a pending scan.
    /// Offline hosts still get a pending request — the agent picks it up when next online.
    /// </summary>
    public async Task<(int Queued, int SkippedUnsupported, int SkippedPending, string Message)> QueueFleetScansAsync(
        IEnumerable<string>? hostnames,
        CancellationToken ct,
        string reason = "manual")
    {
        var options = GetOptions();
        var minVersion = options.MinAgentVersion;
        List<Machine> machines;
        if (hostnames is null)
        {
            machines = await db.Machines.OrderBy(m => m.Hostname).ToListAsync(ct);
        }
        else
        {
            var set = hostnames
                .Select(h => h.Trim())
                .Where(h => h.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (set.Count == 0)
                return (0, 0, 0, "No hostnames selected.");

            machines = await db.Machines
                .Where(m => set.Contains(m.Hostname))
                .OrderBy(m => m.Hostname)
                .ToListAsync(ct);
        }

        var queued = 0;
        var skippedUnsupported = 0;
        var skippedPending = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var machine in machines)
        {
            var simple = VersionCompare.TryGetSimpleVersion(machine.AgentVersion);
            if (simple is null || simple.Value < minVersion)
            {
                skippedUnsupported++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(machine.PendingDiskUsageScanJson))
            {
                skippedPending++;
                continue;
            }

            var request = CreateFleetRequest(options, now);
            machine.PendingDiskUsageScanJson = JsonSerializer.Serialize(request, JsonOptions);
            machine.DiskUsageScanProgressJson = JsonSerializer.Serialize(new DiskUsageScanProgressDto
            {
                ScanId = request.ScanId,
                RootPath = request.RootPath,
                Status = DiskUsageScanStatuses.Queued,
                UpdatedUtc = now,
                Message = $"Fleet storage scan queued ({reason})"
            }, JsonOptions);
            queued++;
        }

        if (queued > 0)
            await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Storage scan queue ({Reason}): queued={Queued}, unsupported={Unsupported}, alreadyPending={Pending}",
            reason, queued, skippedUnsupported, skippedPending);

        var message =
            $"Queued storage scan on {queued} machine(s)"
            + (skippedUnsupported > 0 ? $"; {skippedUnsupported} need agent v{minVersion}+" : "")
            + (skippedPending > 0 ? $"; {skippedPending} already had a scan pending" : "")
            + ".";
        return (queued, skippedUnsupported, skippedPending, message);
    }

    /// <summary>
    /// If today is the configured UTC day and we are past TimeUtc and have not yet run this week, queue all eligible hosts.
    /// Persists last-run stamp in SystemFlags (<see cref="StorageScanSettingKeys.LastWeeklyRunUtc"/>).
    /// </summary>
    public async Task<int> TryRunWeeklyIfDueAsync(CancellationToken ct)
    {
        var options = GetOptions();
        if (!options.Enabled)
            return 0;

        var now = DateTimeOffset.UtcNow;
        if (now.DayOfWeek != options.DayOfWeek)
            return 0;

        var todayUtc = DateOnly.FromDateTime(now.UtcDateTime);
        var scheduled = todayUtc.ToDateTime(options.TimeUtc, DateTimeKind.Utc);
        if (now.UtcDateTime < scheduled)
            return 0;

        var last = await GetLastWeeklyRunUtcAsync(ct);
        if (last is { } lastUtc && lastUtc >= scheduled)
            return 0;

        var (queued, _, _, _) = await QueueFleetScansAsync(hostnames: null, ct, reason: "weekly");
        await SetLastWeeklyRunUtcAsync(now, ct);
        return queued;
    }

    public async Task<DateTimeOffset?> GetLastWeeklyRunUtcAsync(CancellationToken ct)
    {
        var raw = await ReadFlagAsync(StorageScanSettingKeys.LastWeeklyRunUtc, ct);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return DateTimeOffset.TryParse(raw, out var dto) ? dto : null;
    }

    private async Task SetLastWeeklyRunUtcAsync(DateTimeOffset when, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO SystemFlags (Key, Value) VALUES ({0}, {1});",
            [StorageScanSettingKeys.LastWeeklyRunUtc, when.ToString("o")], ct);
    }

    private async Task<string?> ReadFlagAsync(string key, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != ConnectionState.Open;
        if (openedHere)
            await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM SystemFlags WHERE Key = $k LIMIT 1;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$k";
            p.Value = key;
            cmd.Parameters.Add(p);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result as string;
        }
        finally
        {
            if (openedHere)
                await conn.CloseAsync();
        }
    }

    public static DiskUsageScanRequestDto CreateFleetRequest(StorageScanOptions options, DateTimeOffset requestedUtc) =>
        new()
        {
            ScanId = Guid.NewGuid().ToString("N"),
            RootPath = options.RootPath,
            RequestedUtc = requestedUtc,
            MinFileMb = FleetMinFileMb,
            TopFolderCount = FleetTopFolderCount,
            MaxLargeFiles = FleetMaxLargeFiles,
            MaxSeconds = options.MaxSeconds,
            ExcludeSystemFolders = true,
            IncludeHotspots = true,
            FleetProfile = true
        };

    private static string NormalizeRoot(string root)
    {
        var r = (root ?? @"C:\").Trim();
        if (r.Length == 2 && r[1] == ':')
            r += @"\";
        if (!System.Text.RegularExpressions.Regex.IsMatch(r, @"^[A-Za-z]:\\"))
            return @"C:\";
        return r;
    }

    private static DayOfWeek ParseDayOfWeek(string? value, DayOfWeek fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        return Enum.TryParse<DayOfWeek>(value.Trim(), ignoreCase: true, out var d) ? d : fallback;
    }

    private static TimeOnly ParseTimeUtc(string? value, TimeOnly fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        return TimeOnly.TryParse(value.Trim(), out var t) ? t : fallback;
    }
}

public sealed class StorageScanOptions
{
    public bool Enabled { get; init; } = true;
    public DayOfWeek DayOfWeek { get; init; } = DayOfWeek.Sunday;
    public TimeOnly TimeUtc { get; init; } = new(6, 0);
    public int MinAgentVersion { get; init; } = VersionCompare.MinDiskUsageScanVersion;
    public string RootPath { get; init; } = @"C:\";
    public int MaxSeconds { get; init; } = StorageScanService.FleetMaxSeconds;
    public int PollMinutes { get; init; } = 15;
    public int InitialDelaySeconds { get; init; } = 90;
}

public static class StorageScanSettingKeys
{
    public const string LastWeeklyRunUtc = "Heimdall.StorageScan.LastWeeklyRunUtc";
}

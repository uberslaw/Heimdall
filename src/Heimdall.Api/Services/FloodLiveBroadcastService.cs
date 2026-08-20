using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Rebuilds Flood Live table + Active GPU series once every ~30s and publishes to <see cref="FloodLiveHub"/>.
/// Multiple browser viewers share this payload via SSE — they do not each rebuild Live.
/// </summary>
public sealed class FloodLiveBroadcastService(
    IServiceScopeFactory scopeFactory,
    FloodLiveHub hub,
    CodeMeterLicenseHub codeMeterHub,
    Microsoft.Extensions.Options.IOptions<CodeMeterOptions> codeMeterOptions,
    ILogger<FloodLiveBroadcastService> logger) : BackgroundService
{
    public static readonly TimeSpan RebuildInterval = TimeSpan.FromSeconds(30);
    private const int MaxSeriesPoints = 500;
    private static readonly TimeSpan FallbackWindow = TimeSpan.FromHours(2);

    private long _version;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Short delay so DI / DB are ready after boot.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                await RebuildAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Flood Live broadcast rebuild failed");
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            var delay = RebuildInterval - elapsed;
            if (delay < TimeSpan.FromSeconds(1))
                delay = TimeSpan.FromSeconds(1);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RebuildAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var fleet = scope.ServiceProvider.GetRequiredService<FleetDashboardService>();
        var db = scope.ServiceProvider.GetRequiredService<HeimdallDbContext>();

        var rows = await fleet.GetLiveFleetAsync(enrolledOnly: true, ct);
        var enrolledCount = rows.Count;
        var now = DateTimeOffset.UtcNow;
        var licenseSnap = codeMeterHub.Latest;
        var cmEnabled = codeMeterOptions.Value.Enabled;

        var rowDtos = rows.Select(r => MapRow(r, licenseSnap)).ToList();
        var active = rows
            .Where(r => r.Status == FleetDashboardService.FleetStatus.Active)
            .ToList();

        var charts = new List<FloodLiveChartDto>();
        if (active.Count > 0)
        {
            var fromUtc = active
                .Select(r => r.DetectedRunStartedUtc ?? now - FallbackWindow)
                .DefaultIfEmpty(now - FallbackWindow)
                .Min();
            // Floor a bit earlier so series includes start.
            fromUtc = fromUtc.AddMinutes(-1);
            if (fromUtc < now - FallbackWindow)
                fromUtc = now - FallbackWindow;

            var ids = active.Select(r => r.MachineId).ToList();
            var snaps = await FleetSnapshotQuery.LoadForMachinesAsync(db, ids, fromUtc, now, ct);
            var byMachine = snaps.GroupBy(s => s.MachineId).ToDictionary(g => g.Key, g => g.ToList());
            var ramGbByMachine = await db.Machines.AsNoTracking()
                .Where(m => ids.Contains(m.Id))
                .Select(m => new { m.Id, m.HardwareRamGb })
                .ToDictionaryAsync(x => x.Id, x => x.HardwareRamGb, ct);

            foreach (var r in active.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var start = r.DetectedRunStartedUtc ?? now - FallbackWindow;
                byMachine.TryGetValue(r.MachineId, out var list);
                list ??= [];
                ramGbByMachine.TryGetValue(r.MachineId, out var ramGb);
                var series = BuildSeries(list, start, now, ramGb);
                charts.Add(new FloodLiveChartDto(
                    r.MachineId,
                    r.DisplayName,
                    r.Username,
                    start.ToUnixTimeSeconds(),
                    now.ToUnixTimeSeconds(),
                    series));
            }
        }

        var licenses = BuildLicenseDto(cmEnabled, licenseSnap, rows.Select(r => r.LastIp));
        var version = Interlocked.Increment(ref _version);
        hub.Publish(new FloodLivePayload(version, now, enrolledCount, rowDtos, charts, licenses));
        logger.LogDebug(
            "Flood Live broadcast v{Version}: {Rows} rows, {Charts} active charts",
            version, rowDtos.Count, charts.Count);
    }

    internal static FloodLiveLicenseDto BuildLicenseDto(
        bool enabled,
        CodeMeterLicenseSnapshot snap,
        IEnumerable<string?> knownIps)
    {
        if (!enabled)
        {
            return new FloodLiveLicenseDto(
                false, false, false,
                null, snap.Hpc.TotalLicenses, null,
                null, snap.Classic.TotalLicenses, null,
                0, 0, 0, null, "CodeMeter poller disabled");
        }

        var (unHpc, unClassic) = snap.Available
            ? snap.UnmatchedSeats(knownIps)
            : (0, 0);
        var note = snap.ServerNotes.Count == 0
            ? null
            : string.Join(" · ", snap.ServerNotes.Take(6));
        if (!snap.Available)
            note = string.IsNullOrWhiteSpace(note) ? "No license data yet" : note;

        return new FloodLiveLicenseDto(
            true,
            snap.Available,
            snap.Partial,
            snap.Hpc.PoolUsed,
            snap.Hpc.TotalLicenses,
            snap.Hpc.PoolAvailable,
            snap.Classic.PoolUsed,
            snap.Classic.TotalLicenses,
            snap.Classic.PoolAvailable,
            unHpc,
            unClassic,
            snap.PollDurationMs,
            snap.Available ? snap.QueriedAtUtc : null,
            note);
    }

    private static FloodLiveRowDto MapRow(FleetDashboardService.LiveFleetRow r, CodeMeterLicenseSnapshot licenses)
    {
        var (hpc, classic) = licenses.Available ? licenses.SeatsForIp(r.LastIp) : (0, 0);
        return new(
            r.MachineId,
            r.Hostname,
            r.DisplayName,
            r.FriendlyName,
            r.LastIp,
            r.Username,
            r.TuflowRunning,
            r.Status.ToString(),
            r.CpuPercent,
            r.GpuPercent,
            r.GpuMemoryUsedMb,
            r.RamUsedMb,
            r.DiskReadMBps,
            r.DiskWriteMBps,
            r.NetworkInMBps,
            r.NetworkOutMBps,
            r.TodayRuntimeHours,
            r.TodayActiveHours,
            r.TodayGpuHours,
            r.LastSampleUtc,
            r.LastSeenUtc,
            r.SessionState?.ToString(),
            r.DetectedRunStartedUtc,
            r.DetectedRunEndedUtc,
            r.DetectedRunState,
            hpc,
            classic);
    }

    private static IReadOnlyList<FloodLiveMetricPointDto> BuildSeries(
        IReadOnlyList<FleetMetricSnapshot> snaps,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        double? hardwareRamGb)
    {
        var ramTotalMb = hardwareRamGb is > 0 ? hardwareRamGb.Value * 1024.0 : (double?)null;

        var points = snaps
            .Where(s => s.SampledAtUtc >= fromUtc && s.SampledAtUtc <= toUtc)
            .OrderBy(s => s.SampledAtUtc)
            .Select(s =>
            {
                var gpu = FleetDashboardService.SanitizeGpuPercent(s.ProcessGpuPercent)
                          ?? FleetDashboardService.SanitizeGpuPercent(s.GpuPercent);
                var cpu = s.ProcessCpuPercent ?? s.CpuPercent;
                double? ram = null;
                if (ramTotalMb is { } total && s.RamUsedMb is { } used && total > 0)
                    ram = Math.Clamp(100.0 * used / total, 0, 100);
                var diskW = s.ProcessDiskWriteMBps ?? s.DiskWriteMBps;
                var netTx = s.NetworkOutMBps;

                if (gpu is null && cpu is null && ram is null && diskW is null && netTx is null)
                    return null;

                return new FloodLiveMetricPointDto(
                    s.SampledAtUtc.ToUnixTimeSeconds(),
                    Gpu: gpu,
                    Cpu: cpu,
                    Ram: ram,
                    DiskW: diskW,
                    NetTx: netTx);
            })
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        if (points.Count <= MaxSeriesPoints)
            return points;

        var result = new FloodLiveMetricPointDto[MaxSeriesPoints];
        var last = points.Count - 1;
        for (var i = 0; i < MaxSeriesPoints; i++)
        {
            var idx = (int)Math.Round((double)i * last / (MaxSeriesPoints - 1));
            result[i] = points[idx];
        }

        return result;
    }
}

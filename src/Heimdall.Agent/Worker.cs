using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using Heimdall.Agent.Collectors;
using Heimdall.Agent.Services;
using Heimdall.Shared.Contracts;

namespace Heimdall.Agent;

public sealed class Worker(
    ILogger<Worker> logger,
    HeimdallApiClient api,
    IConfiguration configuration) : BackgroundService
{
    private readonly SessionCollector _sessions = new();
    private readonly ProcessCollector _processes = new();
    private AgentConfigDto _config = DefaultConfig();
    private DateTimeOffset _nextConfigRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _nextSample = DateTimeOffset.MinValue;
    private DateTimeOffset _nextUpload = DateTimeOffset.MinValue;
    private DateTimeOffset _nextHardwareRefresh = DateTimeOffset.MinValue;
    private HardwareInventoryCollector.Snapshot? _hardware;
    private readonly List<SessionEventDto> _sessionBuffer = [];
    private readonly Dictionary<string, ProcessRunDto> _processBuffer = new(StringComparer.OrdinalIgnoreCase);
    private OfflineQueue? _queue;
    private bool _sendInventoryNextUpload;
    private readonly HashSet<string> _executedPendingCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _commandsToAck = [];
    private readonly List<CommandExecutionReportDto> _commandReports = [];

    // Live resource sampling (Staff Access) — separate fast poll + cadence, independent of config refresh.
    // Only runs while the API reports an active viewer for this host; see LiveSamplingService.
    private DateTimeOffset _nextResourceControlPoll = DateTimeOffset.MinValue;
    private DateTimeOffset _nextResourceSample = DateTimeOffset.MinValue;
    private bool _resourceSamplingActive;
    private List<string> _resourceFavoriteNames = [];
    private readonly List<ResourceMetricsCollector.Sample> _resourceCalibrationBuffer = [];
    private const int CalibrationSampleCount = 10;
    private static readonly TimeSpan ResourceControlPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResourceSteadyStateInterval = TimeSpan.FromSeconds(10);

    // Historical Dashboard fleet sampling — always-on while config says enrolled; independent of Staff live sampling.
    private DateTimeOffset _nextFleetSample = DateTimeOffset.MinValue;
    private static readonly TimeSpan FleetSampleInterval = TimeSpan.FromSeconds(30);

    // Fast TUFLOW start/stop poll — independent of ConfigRefreshSeconds (default 300s) so a queued
    // start or graceful-stop reaches the agent in ~20s instead of up to 5 minutes. Always-on, same as
    // fleet sampling above (no "someone is viewing a page" gate the way live resource sampling has).
    private DateTimeOffset _nextTuflowPoll = DateTimeOffset.MinValue;
    private static readonly TimeSpan TuflowPollInterval = TimeSpan.FromSeconds(20);

    [SupportedOSPlatform("windows")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hostname = configuration["Heimdall:Hostname"] ?? Environment.MachineName;
        var group = configuration["Heimdall:MachineGroup"];
        var configuredQueue = configuration["Heimdall:QueuePath"];
        var queuePath = string.IsNullOrWhiteSpace(configuredQueue)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Heimdall",
                "queue.db")
            : configuredQueue;
        _queue = new OfflineQueue(queuePath);

        logger.LogInformation("Heimdall agent starting on {Hostname}", hostname);
        RefreshHardware(hostname, force: true);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;

            if (now >= _nextConfigRefresh)
            {
                var remote = await api.GetConfigAsync(hostname, stoppingToken);
                if (remote is not null)
                {
                    _config = remote;
                    if (remote.PendingAppAnalysis)
                        _sendInventoryNextUpload = true;
                    ProcessPendingCommands(remote.PendingCommands);
                    TuflowRunHelper.TryStartIfRequested(remote.PendingTuflowStart, logger);
                    if (remote.PendingClientUpdate is not null
                        && remote.PendingCommands.Any(c =>
                            string.Equals(c, RemoteMachineCommands.UpdateClient, StringComparison.OrdinalIgnoreCase)))
                    {
                        await TryProcessClientUpdateAsync(remote.PendingClientUpdate, stoppingToken);
                    }
                    logger.LogInformation("Config refreshed v{Version}; tracking {Count} processes{Inventory}{Commands}",
                        _config.ConfigVersion, _config.IncludeProcesses.Count + _config.KnownApps.Count(a => a.Enabled),
                        remote.PendingAppAnalysis ? "; inventory requested" : "",
                        remote.PendingCommands.Count > 0 ? $"; pending commands: {string.Join(", ", remote.PendingCommands)}" : "");
                }
                _nextConfigRefresh = now.AddSeconds(Math.Max(60, _config.ConfigRefreshSeconds));
                // Re-scan hardware on config refresh cadence (not every sample)
                RefreshHardware(hostname, force: false);
            }

            if (now >= _nextSample)
            {
                try
                {
                    var sessionEvents = _sessions.SnapshotAndDiff(hostname);
                    lock (_sessionBuffer)
                        _sessionBuffer.AddRange(sessionEvents);

                    var processEvents = _processes.Sample(hostname, _config, sessionId =>
                    {
                        return ResolveSessionUser(sessionId);
                    });

                    foreach (var run in processEvents)
                        _processBuffer[run.RunId] = run;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Sampling failed");
                }

                _nextSample = now.AddSeconds(Math.Max(10, _config.SampleIntervalSeconds));
            }

            if (now >= _nextUpload)
            {
                await FlushAsync(hostname, group, stoppingToken);
                _nextUpload = now.AddSeconds(Math.Max(30, _config.UploadIntervalSeconds));
            }

            await RunResourceSamplingTickAsync(hostname, now, stoppingToken);
            await RunFleetSamplingTickAsync(hostname, now, stoppingToken);
            await RunTuflowPollTickAsync(hostname, now, stoppingToken);

            await Task.Delay(1000, stoppingToken);
        }
    }

    [SupportedOSPlatform("windows")]
    private void RefreshHardware(string hostname, bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now < _nextHardwareRefresh)
            return;

        try
        {
            var pattern = configuration["Heimdall:HostnameSerialPattern"];
            _hardware = HardwareInventoryCollector.TryCollect(hostname, pattern);
            if (_hardware is not null)
                logger.LogInformation(
                    "Hardware inventory: {Brand} {Model} asset={Asset} bios={Bios} city={City} chassis={Chassis} guid={Guid} uuid={Uuid} CPU={Cpu} RAM={Ram}GB disk={Disk}GB GPU={Gpu}",
                    _hardware.Brand, _hardware.Model, _hardware.AssetSerial, _hardware.BiosSerial,
                    _hardware.HostnameCityCode, _hardware.HostnameChassisHint,
                    _hardware.MachineGuid, _hardware.SmbiosUuid,
                    _hardware.Cpu, _hardware.RamGb, _hardware.DiskGb, _hardware.Gpu);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Hardware inventory collection failed");
        }

        // At least daily even if config refresh is more frequent
        _nextHardwareRefresh = now.AddHours(24);
    }

    private async Task FlushAsync(string hostname, string? group, CancellationToken ct)
    {
        List<SessionEventDto> sessions;
        List<ProcessRunDto> processes;
        lock (_sessionBuffer)
        {
            // Sample ticks can append the same EventId twice before upload; dedupe so ingest does not 500.
            sessions = _sessionBuffer
                .GroupBy(s => s.EventId)
                .Select(g => g.OrderByDescending(s => s.ObservedAtUtc).First())
                .ToList();
            _sessionBuffer.Clear();
        }

        processes = _processBuffer.Values.ToList();
        _processBuffer.Clear();

        List<DiscoveredProcessDto> discovered = [];
        if (_sendInventoryNextUpload)
        {
            try
            {
                discovered = ProcessCollector.DiscoverInventory().ToList();
                logger.LogInformation("Sending process inventory ({Count} processes) for app analysis", discovered.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Process inventory failed");
            }
            _sendInventoryNextUpload = false;
        }

        var hw = _hardware;
        List<DiskVolumeDto> volumes = [];
        try
        {
            volumes = HardwareInventoryCollector.TryCollectVolumes().ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Logical disk volume collect failed");
        }

        List<string> acks;
        List<CommandExecutionReportDto> reports;
        lock (_commandsToAck)
        {
            acks = _commandsToAck.ToList();
        }
        lock (_commandReports)
        {
            reports = _commandReports.ToList();
        }

        var batch = new IngestBatchDto
        {
            Heartbeat = new HeartbeatDto
            {
                Hostname = hostname,
                MachineGroup = group,
                OsVersion = Environment.OSVersion.ToString(),
                TimestampUtc = DateTimeOffset.UtcNow,
                IsInUse = _sessions.ActiveCount > 0,
                ActiveSessionCount = _sessions.ActiveCount,
                AgentVersion = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                    ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                    ?? "2",
                HardwareSerialNumber = hw?.PreferredSerial,
                HardwareBrand = hw?.Brand,
                HardwareModel = hw?.Model,
                HardwareCpu = hw?.Cpu,
                HardwareRamGb = hw?.RamGb,
                HardwareDiskGb = hw?.DiskGb,
                HardwareGpu = hw?.Gpu,
                BiosSerial = hw?.BiosSerial,
                AssetSerial = hw?.AssetSerial,
                HostnameCityCode = hw?.HostnameCityCode,
                HostnameChassisHint = hw?.HostnameChassisHint,
                MachineGuid = hw?.MachineGuid,
                SmbiosUuid = hw?.SmbiosUuid,
                OsInstallDateUtc = hw?.OsInstallDateUtc,
                WindowsFolderCreatedUtc = hw?.WindowsFolderCreatedUtc,
                DiskVolumes = volumes,
                PrimaryIpAddress = NetworkInfoHelper.TryGetPrimaryIPv4(),
                TermServiceStatus = TermServiceHelper.GetStatus(),
                TuflowRunStatus = TuflowRunHelper.ReadCurrentStatus(),
                AcknowledgedCommands = acks,
                CommandExecutionReports = reports
            },
            Sessions = sessions,
            ProcessRuns = processes,
            DiscoveredProcesses = discovered
        };

        var ok = await api.UploadAsync(batch, ct);
        if (!ok)
        {
            _queue?.Enqueue(batch);
            logger.LogWarning("Queued batch offline ({Sessions} sessions, {Processes} processes)", sessions.Count, processes.Count);
            return;
        }

        if (acks.Count > 0)
        {
            lock (_commandsToAck)
            {
                foreach (var ack in acks)
                    _commandsToAck.RemoveAll(c => string.Equals(c, ack, StringComparison.OrdinalIgnoreCase));
            }
            foreach (var ack in acks)
                _executedPendingCommands.Remove(ack);
        }

        if (reports.Count > 0)
        {
            lock (_commandReports)
            {
                foreach (var report in reports)
                {
                    _commandReports.RemoveAll(r =>
                        string.Equals(r.Command, report.Command, StringComparison.OrdinalIgnoreCase)
                        && r.ExecutedUtc == report.ExecutedUtc);
                }
            }
        }

        if (_queue is null) return;

        var pending = _queue.Peek(20);
        var acked = new List<long>();
        foreach (var (id, queued) in pending)
        {
            if (await api.UploadAsync(queued, ct))
                acked.Add(id);
            else
                break;
        }
        _queue.Remove(acked);
    }

    /// <summary>
    /// Live resource sampling state machine, driven by the same 1s tick as the rest of the worker (no
    /// extra thread). Control poll every ~10s decides on/off; while on, samples 1/sec for the first 10s
    /// (calibration average — matches "1 sample/sec for 10s -> average"), then one reading every 10s.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task RunResourceSamplingTickAsync(string hostname, DateTimeOffset now, CancellationToken ct)
    {
        if (now >= _nextResourceControlPoll)
        {
            _nextResourceControlPoll = now.Add(ResourceControlPollInterval);
            var status = await api.GetResourceSamplingStatusAsync(hostname, ct);
            if (status is not null)
            {
                _resourceFavoriteNames = status.FavoriteProcessNames;

                if (status.Active && !_resourceSamplingActive)
                {
                    _resourceSamplingActive = true;
                    _resourceCalibrationBuffer.Clear();
                    _nextResourceSample = DateTimeOffset.MinValue; // sample immediately below
                    logger.LogInformation("Resource sampling starting for {Host} (staff viewer active)", hostname);
                }
                else if (!status.Active && _resourceSamplingActive)
                {
                    _resourceSamplingActive = false;
                    _resourceCalibrationBuffer.Clear();
                    logger.LogInformation("Resource sampling stopping for {Host} (no staff viewers)", hostname);
                }
            }
            // status is null on transient failure — keep previous state rather than flapping.
        }

        if (!_resourceSamplingActive || now < _nextResourceSample)
            return;

        ResourceMetricsCollector.Sample sample;
        try
        {
            sample = ResourceMetricsCollector.Collect();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Resource sampling collect failed for {Host}", hostname);
            _nextResourceSample = now.AddSeconds(1);
            return;
        }

        if (_resourceCalibrationBuffer.Count < CalibrationSampleCount)
        {
            _resourceCalibrationBuffer.Add(sample);
            _nextResourceSample = now.AddSeconds(1);

            if (_resourceCalibrationBuffer.Count == CalibrationSampleCount)
            {
                var report = BuildReport(hostname, sample, _resourceCalibrationBuffer, _resourceFavoriteNames, isCalibrationAverage: true);
                await api.ReportResourceSampleAsync(report, ct);
                _nextResourceSample = now.AddSeconds(ResourceSteadyStateInterval.TotalSeconds);
            }
        }
        else
        {
            var report = BuildReport(hostname, sample, burst: null, _resourceFavoriteNames, isCalibrationAverage: false);
            await api.ReportResourceSampleAsync(report, ct);
            _nextResourceSample = now.Add(ResourceSteadyStateInterval);
        }
    }

    /// <summary>
    /// Headline values (CPU/GPU/RAM/disk) are averaged across the burst when provided (calibration), or taken
    /// from the single steady-state sample. Top-3 lists and favourites always reflect the latest sample only —
    /// averaging per-process ranks across a 10-sample burst adds complexity for little benefit at this refresh
    /// rate (documented simplification).
    /// </summary>
    private static ResourceSampleReportDto BuildReport(
        string hostname,
        ResourceMetricsCollector.Sample latest,
        IReadOnlyList<ResourceMetricsCollector.Sample>? burst,
        IReadOnlyList<string> favoriteNames,
        bool isCalibrationAverage)
    {
        double? Avg(Func<ResourceMetricsCollector.Sample, double?> select)
        {
            if (burst is null || burst.Count == 0) return select(latest);
            var values = burst.Select(select).Where(v => v is not null).Select(v => v!.Value).ToList();
            return values.Count == 0 ? null : Math.Round(values.Average(), 1);
        }

        var diskRead = Avg(s => s.DiskReadBytesPerSec);
        var diskWrite = Avg(s => s.DiskWriteBytesPerSec);

        return new ResourceSampleReportDto
        {
            Hostname = hostname,
            SampledAtUtc = DateTimeOffset.UtcNow,
            IsCalibrationAverage = isCalibrationAverage,
            CpuPercent = Avg(s => s.CpuPercent),
            GpuPercent = Avg(s => s.GpuPercent),
            RamPercent = Avg(s => s.RamPercent),
            RamUsedGb = Avg(s => s.RamUsedGb),
            RamTotalGb = Avg(s => s.RamTotalGb),
            DiskReadBytesPerSec = diskRead,
            DiskWriteBytesPerSec = diskWrite,
            DiskReadLevel = DiskActivityLevel.Classify(diskRead),
            DiskWriteLevel = DiskActivityLevel.Classify(diskWrite),
            TopCpuProcesses = ResourceMetricsCollector.TopByCpu(latest, 3),
            TopGpuProcesses = ResourceMetricsCollector.TopByGpu(latest, 3),
            TopRamProcesses = ResourceMetricsCollector.TopByRam(latest, 3),
            TopDiskReadProcesses = ResourceMetricsCollector.TopByDiskRead(latest, 3),
            TopDiskWriteProcesses = ResourceMetricsCollector.TopByDiskWrite(latest, 3),
            FavoriteProcesses = ResourceMetricsCollector.ResolveFavorites(latest, favoriteNames)
        };
    }

    /// <summary>
    /// Always-on 30s fleet sampler for Historical Dashboard enrollment. Gated by FleetSamplingEnabled
    /// from config refresh — separate from viewer-triggered Staff live sampling.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task RunFleetSamplingTickAsync(string hostname, DateTimeOffset now, CancellationToken ct)
    {
        if (!_config.FleetSamplingEnabled)
        {
            _nextFleetSample = DateTimeOffset.MinValue;
            return;
        }

        if (now < _nextFleetSample)
            return;

        _nextFleetSample = now.Add(FleetSampleInterval);

        ResourceMetricsCollector.Sample sample;
        try
        {
            sample = ResourceMetricsCollector.Collect();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fleet sampling collect failed for {Host}", hostname);
            return;
        }

        var processNames = _config.FleetProcessNames is { Count: > 0 }
            ? _config.FleetProcessNames
            : ["tuflow"];
        var tuflowRunning = IsFleetProcessRunning(sample, processNames);
        var processUtil = AggregateFleetProcessUtil(sample, processNames);

        static double? BytesToMBps(double? bytesPerSec) =>
            bytesPerSec is null ? null : Math.Round(bytesPerSec.Value / (1024.0 * 1024.0), 3);

        var dto = new FleetSnapshotDto
        {
            Hostname = hostname,
            SampledAtUtc = DateTimeOffset.UtcNow,
            Username = _sessions.TryGetPrimaryInteractiveUsername(),
            TuflowRunning = tuflowRunning,
            CpuPercent = sample.CpuPercent is null ? null : Math.Round(sample.CpuPercent.Value, 1),
            GpuPercent = sample.GpuPercent is null ? null : Math.Round(sample.GpuPercent.Value, 1),
            GpuMemoryUsedMb = sample.GpuMemoryUsedMb is null ? null : Math.Round(sample.GpuMemoryUsedMb.Value, 1),
            RamUsedMb = sample.RamUsedGb is null ? null : Math.Round(sample.RamUsedGb.Value * 1024.0, 1),
            DiskReadMBps = BytesToMBps(sample.DiskReadBytesPerSec),
            DiskWriteMBps = BytesToMBps(sample.DiskWriteBytesPerSec),
            NetworkInMBps = BytesToMBps(sample.NetworkInBytesPerSec),
            NetworkOutMBps = BytesToMBps(sample.NetworkOutBytesPerSec),
            ProcessCpuPercent = tuflowRunning ? Math.Round(processUtil.CpuPercent, 1) : null,
            ProcessGpuPercent = tuflowRunning ? Math.Round(processUtil.GpuPercent, 1) : null,
            ProcessDiskReadMBps = tuflowRunning ? Math.Round(processUtil.DiskReadMBps, 3) : null,
            ProcessDiskWriteMBps = tuflowRunning ? Math.Round(processUtil.DiskWriteMBps, 3) : null
        };

        var ok = await api.ReportFleetSnapshotAsync(dto, ct);
        if (!ok)
            logger.LogDebug("Fleet snapshot upload failed for {Host} (dropped)", hostname);
    }

    private static bool MatchesFleetProcess(string name, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;
            if (name.Contains(pattern.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsFleetProcessRunning(ResourceMetricsCollector.Sample sample, IReadOnlyList<string> patterns)
    {
        foreach (var name in sample.ProcessesByName.Keys)
        {
            if (MatchesFleetProcess(name, patterns))
                return true;
        }

        // Also check live process list in case WMI process counters omit short-lived names.
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (MatchesFleetProcess(p.ProcessName, patterns))
                        return true;
                }
                finally { p.Dispose(); }
            }
        }
        catch { /* best-effort */ }

        return false;
    }

    /// <summary>Sum CPU/GPU/disk for processes matching fleet patterns (TUFLOW Active/Idle thresholds).</summary>
    private static (double CpuPercent, double GpuPercent, double DiskReadMBps, double DiskWriteMBps) AggregateFleetProcessUtil(
        ResourceMetricsCollector.Sample sample,
        IReadOnlyList<string> patterns)
    {
        double cpu = 0, gpu = 0, diskRead = 0, diskWrite = 0;
        foreach (var (name, usage) in sample.ProcessesByName)
        {
            if (!MatchesFleetProcess(name, patterns))
                continue;
            cpu += usage.CpuPercent;
            gpu += usage.GpuPercent;
            diskRead += usage.DiskReadBytesPerSec / (1024.0 * 1024.0);
            diskWrite += usage.DiskWriteBytesPerSec / (1024.0 * 1024.0);
        }

        return (cpu, gpu, diskRead, diskWrite);
    }

    private async Task RunTuflowPollTickAsync(string hostname, DateTimeOffset now, CancellationToken ct)
    {
        if (now < _nextTuflowPoll)
            return;

        _nextTuflowPoll = now.Add(TuflowPollInterval);

        var pending = await api.GetTuflowPendingAsync(hostname, ct);
        if (pending is null)
            return; // transient failure — next tick (or the slower config-refresh path) will catch it

        if (pending.PendingTuflowStart is not null)
            TuflowRunHelper.TryStartIfRequested(pending.PendingTuflowStart, logger);

        if (pending.StopRequested)
            TryExecuteTuflowStopFastPath();
    }

    /// <summary>
    /// Shares _executedPendingCommands/_commandsToAck with ProcessPendingCommands on purpose —
    /// both this fast tick and the slower config-refresh path can see TuflowStopGraceful and race to
    /// execute it; the shared dedupe set means whichever gets there first wins and the other is a no-op.
    /// Failures are logged at Debug on this path — "no run tracked yet" is expected most of the time.
    /// </summary>
    private void TryExecuteTuflowStopFastPath()
    {
        const string command = RemoteMachineCommands.TuflowStopGraceful;
        if (_executedPendingCommands.Contains(command))
            return;

        if (!TuflowRunHelper.TryExecuteCommand(command, logger, out var detail))
        {
            logger.LogDebug("TUFLOW stop not actioned yet on fast poll: {Detail}", detail);
            return;
        }

        _executedPendingCommands.Add(command);
        lock (_commandsToAck)
        {
            if (!_commandsToAck.Contains(command, StringComparer.OrdinalIgnoreCase))
                _commandsToAck.Add(command);
        }
        RecordCommandReport(command, success: true, detail);
    }

    private async Task TryProcessClientUpdateAsync(ClientUpdateRequestDto request, CancellationToken ct)
    {
        const string command = RemoteMachineCommands.UpdateClient;
        if (_executedPendingCommands.Contains(command))
            return;

        var (ack, success, detail) = await ClientUpdateHelper.TryApplyAsync(
            request, _sessions, api, configuration, logger, ct);

        RecordCommandReport(command, success, detail);
        if (!ack)
            return;

        _executedPendingCommands.Add(command);
        lock (_commandsToAck)
        {
            if (!_commandsToAck.Contains(command, StringComparer.OrdinalIgnoreCase))
                _commandsToAck.Add(command);
        }
    }

    private void ProcessPendingCommands(IReadOnlyList<string> commands)
    {
        if (commands.Count == 0)
            return;

        foreach (var command in commands)
        {
            if (_executedPendingCommands.Contains(command))
                continue;

            // Handled asynchronously via PendingClientUpdate payload
            if (string.Equals(command, RemoteMachineCommands.UpdateClient, StringComparison.OrdinalIgnoreCase))
                continue;

            if (TermServiceHelper.TryExecuteCommand(command, logger, out var detail))
            {
                _executedPendingCommands.Add(command);
                lock (_commandsToAck)
                {
                    if (!_commandsToAck.Contains(command, StringComparer.OrdinalIgnoreCase))
                        _commandsToAck.Add(command);
                }
                RecordCommandReport(command, success: true, detail);
            }
            else
            {
                if (TuflowRunHelper.TryExecuteCommand(command, logger, out detail))
                {
                    _executedPendingCommands.Add(command);
                    lock (_commandsToAck)
                    {
                        if (!_commandsToAck.Contains(command, StringComparer.OrdinalIgnoreCase))
                            _commandsToAck.Add(command);
                    }
                    RecordCommandReport(command, success: true, detail);
                }
                else
                {
                    logger.LogWarning("Pending command failed: {Command} — {Detail}", command, detail);
                    RecordCommandReport(command, success: false, detail);
                }
            }
        }
    }

    private void RecordCommandReport(string command, bool success, string detail)
    {
        lock (_commandReports)
        {
            _commandReports.Add(new CommandExecutionReportDto
            {
                Command = command,
                Success = success,
                Detail = detail,
                ExecutedUtc = DateTimeOffset.UtcNow
            });
        }
    }

    private static (string Username, string? Domain)? ResolveSessionUser(int sessionId)
    {
        try
        {
            return SessionCollector.TryGetSessionUser(sessionId);
        }
        catch
        {
            return null;
        }
    }

    private static AgentConfigDto DefaultConfig() => new()
    {
        SampleIntervalSeconds = 30,
        UploadIntervalSeconds = 60,
        ConfigRefreshSeconds = 300,
        IncludeProcesses = ["Revit", "acad", "EXCEL", "chrome", "msedge"],
        ExcludeProcesses = ["Idle", "System", "svchost"],
        KnownApps =
        [
            new KnownAppDto { DisplayName = "Revit", ProcessName = "Revit" },
            new KnownAppDto { DisplayName = "AutoCAD", ProcessName = "acad" },
            new KnownAppDto { DisplayName = "Remote Desktop (mstsc)", ProcessName = "mstsc" },
            new KnownAppDto { DisplayName = "Remote Desktop (msrdc)", ProcessName = "msrdc" },
            new KnownAppDto { DisplayName = "Remote Desktop (msrdcw)", ProcessName = "msrdcw" }
        ]
    };
}


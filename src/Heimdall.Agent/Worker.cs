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
    private bool _weeklyInventoryThrottle;
    private WeeklyInventoryState? _weeklyInventory;
    private string? _weeklyInventoryPath;
    private DateTimeOffset _nextWeeklyInventoryCheck = DateTimeOffset.MinValue;
    private DiskUsageScanRequestDto? _pendingDiskScan;
    private Task<DiskUsageScanResultDto>? _diskScanTask;
    private DiskUsageScanResultDto? _diskScanResultReady;
    private DiskUsageScanProgressDto? _diskScanProgressReady;
    private readonly object _diskScanGate = new();
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

    // Fast disk-usage scan poll — same cadence as TUFLOW so Scan is not stuck behind config refresh.
    private DateTimeOffset _nextDiskUsagePoll = DateTimeOffset.MinValue;
    private static readonly TimeSpan DiskUsagePollInterval = TimeSpan.FromSeconds(20);

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
        _weeklyInventoryPath = Path.Combine(
            Path.GetDirectoryName(queuePath) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Heimdall"),
            "weekly-inventory.json");
        _weeklyInventory = WeeklyInventoryState.LoadOrCreate(_weeklyInventoryPath);

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
                    if (remote.PendingDiskUsageScan is not null)
                        QueueDiskUsageScan(hostname, remote.PendingDiskUsageScan);
                    ProcessPendingCommands(remote.PendingCommands);
                    TuflowRunHelper.TryStartIfRequested(remote.PendingTuflowStart, logger);
                    if (remote.PendingClientUpdate is not null
                        && remote.PendingCommands.Any(c =>
                            string.Equals(c, RemoteMachineCommands.UpdateClient, StringComparison.OrdinalIgnoreCase)))
                    {
                        await TryProcessClientUpdateAsync(remote.PendingClientUpdate, stoppingToken);
                    }

                    if (remote.PendingCommands.Any(c =>
                            string.Equals(c, RemoteMachineCommands.DepositClientPack, StringComparison.OrdinalIgnoreCase)))
                    {
                        await TryProcessDepositClientPackAsync(remote.PendingClientDeposit, stoppingToken);
                    }
                    logger.LogInformation("Config refreshed v{Version}; tracking {Count} processes{Inventory}{Commands}",
                        _config.ConfigVersion, _config.IncludeProcesses.Count,
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
            await RunDiskUsagePollTickAsync(hostname, now, stoppingToken);
            TryScheduleWeeklyInventory(now);

            await Task.Delay(1000, stoppingToken);
        }
    }

    [SupportedOSPlatform("windows")]
    private void QueueDiskUsageScan(string hostname, DiskUsageScanRequestDto request)
    {
        lock (_diskScanGate)
        {
            if (_pendingDiskScan is not null
                && string.Equals(_pendingDiskScan.ScanId, request.ScanId, StringComparison.OrdinalIgnoreCase))
                return;
            if (_diskScanTask is { IsCompleted: false })
            {
                // Already scanning something else — keep latest request for after.
                _pendingDiskScan = request;
                return;
            }

            _pendingDiskScan = request;
            var scanReq = request;
            var host = hostname;
            logger.LogInformation(
                "Disk usage scan queued: {Root} (min file {MinMb} MB, top {Top}, max {MaxSec}s)",
                DiskUsageScanRoots.FormatForDisplay(scanReq.RootPath), scanReq.MinFileMb, scanReq.TopFolderCount, scanReq.MaxSeconds);

            void OnProgress(DiskUsageScanProgressDto progress)
            {
                lock (_diskScanGate)
                    _diskScanProgressReady = progress;
                _ = api.ReportDiskUsageProgressAsync(host, progress, CancellationToken.None);
            }

            _diskScanTask = Task.Run(() =>
            {
                try
                {
                    return DiskUsageScanner.Scan(scanReq, OnProgress);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Disk usage scan failed");
                    return new DiskUsageScanResultDto
                    {
                        ScanId = scanReq.ScanId,
                        RootPath = scanReq.RootPath,
                        CompletedUtc = DateTimeOffset.UtcNow,
                        ElapsedSeconds = 0,
                        Error = ex.Message
                    };
                }
            });

            _ = _diskScanTask.ContinueWith(t =>
            {
                DiskUsageScanResultDto? result = null;
                if (t.Status == TaskStatus.RanToCompletion)
                    result = t.Result;
                DiskUsageScanRequestDto? next = null;
                lock (_diskScanGate)
                {
                    if (result is not null)
                        _diskScanResultReady = result;
                    if (_pendingDiskScan is not null
                        && string.Equals(_pendingDiskScan.ScanId, scanReq.ScanId, StringComparison.OrdinalIgnoreCase))
                        _pendingDiskScan = null;
                    // A newer request may have been parked while this scan ran — start it next.
                    if (_pendingDiskScan is not null
                        && !string.Equals(_pendingDiskScan.ScanId, scanReq.ScanId, StringComparison.OrdinalIgnoreCase))
                        next = _pendingDiskScan;
                    _diskScanTask = null;
                }
                logger.LogInformation(
                    "Disk usage scan finished: folders={Folders} files={Files} truncated={Truncated} err={Error}",
                    result?.TopFolders.Count, result?.LargeFiles.Count, result?.Truncated, result?.Error);
                if (next is not null)
                    QueueDiskUsageScan(host, next);
            }, TaskScheduler.Default);
        }
    }

    [SupportedOSPlatform("windows")]
    private void TryScheduleWeeklyInventory(DateTimeOffset now)
    {
        if (_weeklyInventory is null || _weeklyInventoryPath is null)
            return;
        if (now < _nextWeeklyInventoryCheck)
            return;
        _nextWeeklyInventoryCheck = now.AddMinutes(5);

        if (!_weeklyInventory.ShouldAttempt(now, out _))
        {
            _weeklyInventory.Save(_weeklyInventoryPath);
            return;
        }

        // Idle gate: CPU and GPU both under 50% (missing GPU treated as 0).
        double? cpu = null;
        double? gpu = null;
        try
        {
            var sample = ResourceMetricsCollector.Collect();
            cpu = sample.CpuPercent;
            gpu = sample.GpuPercent;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Weekly inventory idle sample failed");
        }

        var cpuOk = cpu is null or < 50;
        var gpuOk = gpu is null or < 50;
        if (!cpuOk || !gpuOk)
        {
            _weeklyInventory.RecordIdleFailure(now);
            _weeklyInventory.Save(_weeklyInventoryPath);
            logger.LogInformation(
                "Weekly inventory deferred (CPU={Cpu} GPU={Gpu} attempts={Attempts}/6); retry after {Retry}",
                cpu, gpu, _weeklyInventory.FailedIdleAttempts, _weeklyInventory.NextRetryUtc);
            return;
        }

        _sendInventoryNextUpload = true;
        _weeklyInventoryThrottle = true;
        // Success is recorded in FlushAsync after inventory is actually collected.
        _weeklyInventory.Save(_weeklyInventoryPath);
        logger.LogInformation("Weekly opportunistic inventory queued (CPU={Cpu} GPU={Gpu})", cpu, gpu);
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
                var throttle = _weeklyInventoryThrottle;
                discovered = ProcessCollector.DiscoverInventory(throttle).ToList();
                logger.LogInformation("Sending process inventory ({Count} processes) for app analysis{Throttle}",
                    discovered.Count, throttle ? " (throttled)" : "");
                if (_weeklyInventoryThrottle && _weeklyInventory is not null && _weeklyInventoryPath is not null)
                {
                    _weeklyInventory.RecordSuccess(DateTimeOffset.UtcNow);
                    _weeklyInventory.Save(_weeklyInventoryPath);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Process inventory failed");
                if (_weeklyInventoryThrottle && _weeklyInventory is not null && _weeklyInventoryPath is not null)
                {
                    _weeklyInventory.RecordIdleFailure(DateTimeOffset.UtcNow);
                    _weeklyInventory.Save(_weeklyInventoryPath);
                }
            }
            _sendInventoryNextUpload = false;
            _weeklyInventoryThrottle = false;
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

        DiskUsageScanResultDto? diskScan = null;
        DiskUsageScanProgressDto? diskProgress = null;
        lock (_diskScanGate)
        {
            if (_diskScanResultReady is not null)
            {
                diskScan = _diskScanResultReady;
                _diskScanResultReady = null;
            }
            if (_diskScanProgressReady is not null && diskScan is null)
            {
                diskProgress = _diskScanProgressReady;
                // Keep latest until result ships; next upload can refresh.
            }
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
            DiscoveredProcesses = discovered,
            DiskUsageScan = diskScan,
            DiskUsageScanProgress = diskProgress
        };

        var ok = await api.UploadAsync(batch, ct);
        if (!ok)
        {
            // Keep disk-scan payload for the next successful upload (don't drop the only copy).
            if (diskScan is not null || diskProgress is not null)
            {
                lock (_diskScanGate)
                {
                    if (diskScan is not null && _diskScanResultReady is null)
                        _diskScanResultReady = diskScan;
                    if (diskProgress is not null && _diskScanProgressReady is null)
                        _diskScanProgressReady = diskProgress;
                }
            }
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
    /// Always-on 30s fleet sampler for util / historical metrics. Gated by FleetSamplingEnabled
    /// from config refresh (true for every known Machine) — separate from viewer-triggered Staff live sampling.
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
            ProcessDiskWriteMBps = tuflowRunning ? Math.Round(processUtil.DiskWriteMBps, 3) : null,
            TopCpuProcesses = ResourceMetricsCollector.TopByCpu(sample, 5),
            TopGpuProcesses = ResourceMetricsCollector.TopByGpu(sample, 5),
            TopDiskReadProcesses = ResourceMetricsCollector.TopByDiskRead(sample, 5),
            TopDiskWriteProcesses = ResourceMetricsCollector.TopByDiskWrite(sample, 5)
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

    private async Task RunDiskUsagePollTickAsync(string hostname, DateTimeOffset now, CancellationToken ct)
    {
        if (now < _nextDiskUsagePoll)
            return;

        _nextDiskUsagePoll = now.Add(DiskUsagePollInterval);

        var pending = await api.GetDiskUsagePendingAsync(hostname, ct);
        if (pending?.PendingDiskUsageScan is null)
            return;

        QueueDiskUsageScan(hostname, pending.PendingDiskUsageScan);
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

    private async Task TryProcessDepositClientPackAsync(ClientDepositRequestDto? depositRequest, CancellationToken ct)
    {
        const string command = RemoteMachineCommands.DepositClientPack;
        if (_executedPendingCommands.Contains(command))
            return;

        var (ack, success, detail) = await ClientPackDepositHelper.TryDepositAsync(api, logger, ct, depositRequest);
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

            // Handled asynchronously via PendingClientUpdate payload / deposit helper
            if (string.Equals(command, RemoteMachineCommands.UpdateClient, StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, RemoteMachineCommands.DepositClientPack, StringComparison.OrdinalIgnoreCase))
                continue;

            if (TermServiceHelper.TryExecuteCommand(command, logger, out var detail)
                || TuflowRunHelper.TryExecuteCommand(command, logger, out detail)
                || ClientMaintenanceHelper.TryExecuteCommand(command, logger, out detail))
            {
                // Cleanup / Restart skipped because UpdateClient or durable install.lock is held — keep pending.
                if ((string.Equals(command, RemoteMachineCommands.CleanupClientStaging, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(command, RemoteMachineCommands.RestartAgent, StringComparison.OrdinalIgnoreCase))
                    && detail.Contains("Skipped:", StringComparison.OrdinalIgnoreCase))
                {
                    RecordCommandReport(command, success: false, detail);
                    continue;
                }

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
                // Ack unknown commands so old agents do not retry forever with the same error.
                if (detail.Contains("Unknown command", StringComparison.OrdinalIgnoreCase))
                {
                    _executedPendingCommands.Add(command);
                    lock (_commandsToAck)
                    {
                        if (!_commandsToAck.Contains(command, StringComparer.OrdinalIgnoreCase))
                            _commandsToAck.Add(command);
                    }
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
        // Offline fallback: empty include until API config arrives (App lists drive tracking).
        IncludeProcesses = [],
        ExcludeProcesses = ["Idle", "System", "svchost"],
        KnownApps = []
    };
}


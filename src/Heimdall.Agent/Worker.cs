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
        RefreshHardware(force: true);

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
                    logger.LogInformation("Config refreshed v{Version}; tracking {Count} processes{Inventory}",
                        _config.ConfigVersion, _config.IncludeProcesses.Count + _config.KnownApps.Count(a => a.Enabled),
                        remote.PendingAppAnalysis ? "; inventory requested" : "");
                }
                _nextConfigRefresh = now.AddSeconds(Math.Max(60, _config.ConfigRefreshSeconds));
                // Re-scan hardware on config refresh cadence (not every sample)
                RefreshHardware(force: false);
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

            await Task.Delay(1000, stoppingToken);
        }
    }

    [SupportedOSPlatform("windows")]
    private void RefreshHardware(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now < _nextHardwareRefresh)
            return;

        try
        {
            _hardware = HardwareInventoryCollector.TryCollect();
            if (_hardware is not null)
                logger.LogInformation(
                    "Hardware inventory: {Brand} {Model} serial={Serial} CPU={Cpu} RAM={Ram}GB disk={Disk}GB GPU={Gpu}",
                    _hardware.Brand, _hardware.Model, _hardware.SerialNumber,
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
            sessions = _sessionBuffer.ToList();
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
                AgentVersion = "0.1.0",
                HardwareSerialNumber = hw?.SerialNumber,
                HardwareBrand = hw?.Brand,
                HardwareModel = hw?.Model,
                HardwareCpu = hw?.Cpu,
                HardwareRamGb = hw?.RamGb,
                HardwareDiskGb = hw?.DiskGb,
                HardwareGpu = hw?.Gpu
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
            new KnownAppDto { DisplayName = "AutoCAD", ProcessName = "acad" }
        ]
    };
}


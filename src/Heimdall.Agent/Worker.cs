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
    private readonly List<SessionEventDto> _sessionBuffer = [];
    private readonly Dictionary<string, ProcessRunDto> _processBuffer = new(StringComparer.OrdinalIgnoreCase);
    private OfflineQueue? _queue;

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

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;

            if (now >= _nextConfigRefresh)
            {
                var remote = await api.GetConfigAsync(hostname, stoppingToken);
                if (remote is not null)
                {
                    _config = remote;
                    logger.LogInformation("Config refreshed v{Version}; tracking {Count} processes",
                        _config.ConfigVersion, _config.IncludeProcesses.Count + _config.KnownApps.Count(a => a.Enabled));
                }
                _nextConfigRefresh = now.AddSeconds(Math.Max(60, _config.ConfigRefreshSeconds));
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
                        // Resolve via latest session event buffer / live collector indirectly:
                        // SessionCollector doesn't expose lookup; use Windows username from WTS via a lightweight query.
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
                AgentVersion = "0.1.0"
            },
            Sessions = sessions,
            ProcessRuns = processes
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
        // Reuse WTS query helpers via a tiny local call
        try
        {
            var sessions = typeof(SessionCollector); // keep reference for linker friendliness
            _ = sessions;
            return QueryUser(sessionId);
        }
        catch
        {
            return null;
        }
    }

    private static (string Username, string? Domain)? QueryUser(int sessionId)
    {
        if (!NativeWts.WTSQuerySessionInformation(IntPtr.Zero, sessionId, NativeWts.WTS_INFO_CLASS.WTSUserName, out var userPtr, out _))
            return null;
        string? user;
        try { user = System.Runtime.InteropServices.Marshal.PtrToStringUni(userPtr)?.Trim(); }
        finally { NativeWts.WTSFreeMemory(userPtr); }

        if (string.IsNullOrWhiteSpace(user))
            return null;

        string? domain = null;
        if (NativeWts.WTSQuerySessionInformation(IntPtr.Zero, sessionId, NativeWts.WTS_INFO_CLASS.WTSDomainName, out var domainPtr, out _))
        {
            try { domain = System.Runtime.InteropServices.Marshal.PtrToStringUni(domainPtr)?.Trim(); }
            finally { NativeWts.WTSFreeMemory(domainPtr); }
        }

        return (user, string.IsNullOrWhiteSpace(domain) ? null : domain);
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

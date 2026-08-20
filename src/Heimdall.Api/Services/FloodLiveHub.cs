using System.Threading.Channels;

namespace Heimdall.Api.Services;

/// <summary>
/// Shared Flood Live snapshot: one rebuild fans out to all SSE subscribers.
/// </summary>
public sealed class FloodLiveHub
{
    private readonly object _gate = new();
    private FloodLivePayload _latest = FloodLivePayload.Empty;
    private readonly List<Channel<FloodLivePayload>> _subscribers = [];

    public FloodLivePayload Latest
    {
        get { lock (_gate) return _latest; }
    }

    public void Publish(FloodLivePayload payload)
    {
        List<Channel<FloodLivePayload>> copy;
        lock (_gate)
        {
            _latest = payload;
            copy = _subscribers.ToList();
        }

        foreach (var ch in copy)
        {
            // Drop if a slow client falls behind — they reconnect and get Latest.
            ch.Writer.TryWrite(payload);
        }
    }

    public ChannelReader<FloodLivePayload> Subscribe(out FloodLivePayload current)
    {
        var ch = Channel.CreateBounded<FloodLivePayload>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        lock (_gate)
        {
            current = _latest;
            _subscribers.Add(ch);
        }

        return ch.Reader;
    }

    public void Unsubscribe(ChannelReader<FloodLivePayload> reader)
    {
        lock (_gate)
        {
            for (var i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_subscribers[i].Reader, reader))
                {
                    _subscribers[i].Writer.TryComplete();
                    _subscribers.RemoveAt(i);
                }
            }
        }
    }
}

public sealed record FloodLivePayload(
    long Version,
    DateTimeOffset GeneratedAtUtc,
    int EnrolledCount,
    IReadOnlyList<FloodLiveRowDto> Rows,
    IReadOnlyList<FloodLiveChartDto> Charts,
    FloodLiveLicenseDto? Licenses = null)
{
    public static FloodLivePayload Empty { get; } = new(0, DateTimeOffset.UnixEpoch, 0, [], [], null);
}

/// <summary>CodeMeter pool strip for Flood Live (HPC + Classic).</summary>
public sealed record FloodLiveLicenseDto(
    bool Enabled,
    bool Available,
    bool Partial,
    int? HpcUsed,
    int HpcTotal,
    int? HpcAvailable,
    int? ClassicUsed,
    int ClassicTotal,
    int? ClassicAvailable,
    int UnmatchedHpc,
    int UnmatchedClassic,
    double PollDurationMs,
    DateTimeOffset? QueriedAtUtc,
    string? StatusNote);

public sealed record FloodLiveRowDto(
    int MachineId,
    string Hostname,
    string DisplayName,
    string? FriendlyName,
    string? LastIp,
    string? Username,
    bool TuflowRunning,
    string Status,
    double? CpuPercent,
    double? GpuPercent,
    double? GpuMemoryUsedMb,
    double? RamUsedMb,
    double? DiskReadMBps,
    double? DiskWriteMBps,
    double? NetworkInMBps,
    double? NetworkOutMBps,
    double TodayRuntimeHours,
    double TodayActiveHours,
    double TodayGpuHours,
    DateTimeOffset? LastSampleUtc,
    DateTimeOffset LastSeenUtc,
    string? SessionState,
    DateTimeOffset? DetectedRunStartedUtc,
    DateTimeOffset? DetectedRunEndedUtc,
    string? DetectedRunState,
    int HpcSeats = 0,
    int ClassicSeats = 0);

public sealed record FloodLiveChartDto(
    int MachineId,
    string Label,
    string? Username,
    long StartUnix,
    long EndUnix,
    IReadOnlyList<FloodLiveMetricPointDto> Series);

/// <summary>Multi-metric sample for Active charts (GPU/CPU/RAM% + Disk W / Net Tx MB/s).</summary>
public sealed record FloodLiveMetricPointDto(
    long T,
    double? Gpu = null,
    double? Cpu = null,
    double? Ram = null,
    double? DiskW = null,
    double? NetTx = null);

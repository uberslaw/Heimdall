using System.Runtime.InteropServices;
using System.Text;
using Heimdall.Shared.Contracts;

namespace Heimdall.Agent.Collectors;

internal static class NativeWts
{
    public const int WTS_CURRENT_SERVER_HANDLE = 0;

    public enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit
    }

    public enum WTS_INFO_CLASS
    {
        WTSUserName = 5,
        WTSDomainName = 7,
        WTSClientProtocolType = 16,
        WTSClientName = 10,
        WTSClientAddress = 14,
        WTSSessionInfo = 24
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WTS_CLIENT_ADDRESS
    {
        public int AddressFamily;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        public byte[] Address;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSEnumerateSessions(
        IntPtr hServer,
        int Reserved,
        int Version,
        out IntPtr ppSessionInfo,
        out int pCount);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        int sessionId,
        WTS_INFO_CLASS wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);
}

public sealed class TrackedSession
{
    public required string EventId { get; init; }
    public int SessionId { get; init; }
    public required string Username { get; set; }
    public string? Domain { get; set; }
    public SessionType SessionType { get; set; }
    public SessionState State { get; set; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public DateTimeOffset LastObservedUtc { get; set; }
    public DateTimeOffset StateChangedAtUtc { get; set; }
    public string? ClientName { get; set; }
    public string? ClientAddress { get; set; }
    public long ActiveSeconds { get; set; }
    public long DisconnectedSeconds { get; set; }
}

public sealed class SessionCollector
{
    private readonly Dictionary<int, TrackedSession> _sessions = new();
    private readonly object _gate = new();

    public IReadOnlyList<SessionEventDto> SnapshotAndDiff(string hostname)
    {
        var now = DateTimeOffset.UtcNow;
        var live = EnumerateSessions();
        var events = new List<SessionEventDto>();

        lock (_gate)
        {
            var seen = new HashSet<int>();

            foreach (var liveSession in live)
            {
                seen.Add(liveSession.SessionId);
                if (!_sessions.TryGetValue(liveSession.SessionId, out var tracked))
                {
                    tracked = new TrackedSession
                    {
                        EventId = $"{hostname}:{liveSession.SessionId}:{now.ToUnixTimeSeconds()}",
                        SessionId = liveSession.SessionId,
                        Username = liveSession.Username,
                        Domain = liveSession.Domain,
                        SessionType = liveSession.SessionType,
                        State = liveSession.State,
                        StartedAtUtc = now,
                        LastObservedUtc = now,
                        StateChangedAtUtc = now,
                        ClientName = liveSession.ClientName,
                        ClientAddress = liveSession.ClientAddress
                    };
                    _sessions[liveSession.SessionId] = tracked;
                    events.Add(ToDto(hostname, tracked, now));
                    continue;
                }

                AccumulateTime(tracked, now);
                var stateChanged = tracked.State != liveSession.State;
                tracked.Username = liveSession.Username;
                tracked.Domain = liveSession.Domain;
                tracked.SessionType = liveSession.SessionType;
                tracked.ClientName = liveSession.ClientName ?? tracked.ClientName;
                tracked.ClientAddress = liveSession.ClientAddress ?? tracked.ClientAddress;
                tracked.LastObservedUtc = now;

                if (stateChanged)
                {
                    tracked.State = liveSession.State;
                    tracked.StateChangedAtUtc = now;
                    events.Add(ToDto(hostname, tracked, now));
                }
            }

            foreach (var id in _sessions.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                var tracked = _sessions[id];
                AccumulateTime(tracked, now);
                tracked.State = SessionState.Ended;
                tracked.EndedAtUtc = now;
                tracked.LastObservedUtc = now;
                events.Add(ToDto(hostname, tracked, now));
                _sessions.Remove(id);
            }

            // Periodic refresh of open sessions so active/disconnected counters land in DB
            foreach (var tracked in _sessions.Values)
            {
                if (events.Any(e => e.EventId == tracked.EventId && e.ObservedAtUtc == now))
                    continue;
                events.Add(ToDto(hostname, tracked, now));
            }
        }

        return events;
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
                return _sessions.Count(s => s.Value.State is SessionState.Active or SessionState.Disconnected);
        }
    }

    private static void AccumulateTime(TrackedSession tracked, DateTimeOffset now)
    {
        var delta = (long)Math.Max(0, (now - tracked.StateChangedAtUtc).TotalSeconds);
        if (tracked.State == SessionState.Active)
            tracked.ActiveSeconds += delta;
        else if (tracked.State == SessionState.Disconnected)
            tracked.DisconnectedSeconds += delta;
        tracked.StateChangedAtUtc = now;
    }

    private static SessionEventDto ToDto(string hostname, TrackedSession s, DateTimeOffset now) => new()
    {
        EventId = s.EventId,
        Hostname = hostname,
        SessionId = s.SessionId,
        Username = s.Username,
        Domain = s.Domain,
        SessionType = s.SessionType,
        State = s.State,
        ObservedAtUtc = now,
        StartedAtUtc = s.StartedAtUtc,
        EndedAtUtc = s.EndedAtUtc,
        ClientName = s.ClientName,
        ClientAddress = s.ClientAddress,
        ActiveSeconds = s.ActiveSeconds,
        DisconnectedSeconds = s.DisconnectedSeconds
    };

    private static List<TrackedSession> EnumerateSessions()
    {
        var results = new List<TrackedSession>();
        if (!NativeWts.WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var ptr, out var count) || ptr == IntPtr.Zero)
            return results;

        try
        {
            var size = Marshal.SizeOf<NativeWts.WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<NativeWts.WTS_SESSION_INFO>(ptr + i * size);
                if (info.SessionId == 0)
                    continue; // services session

                var user = QueryString(info.SessionId, NativeWts.WTS_INFO_CLASS.WTSUserName);
                if (string.IsNullOrWhiteSpace(user))
                    continue;

                var domain = QueryString(info.SessionId, NativeWts.WTS_INFO_CLASS.WTSDomainName);
                var clientName = QueryString(info.SessionId, NativeWts.WTS_INFO_CLASS.WTSClientName);
                var protocol = QueryUInt16(info.SessionId, NativeWts.WTS_INFO_CLASS.WTSClientProtocolType);
                var address = QueryClientAddress(info.SessionId);

                var state = info.State switch
                {
                    NativeWts.WTS_CONNECTSTATE_CLASS.WTSActive => SessionState.Active,
                    NativeWts.WTS_CONNECTSTATE_CLASS.WTSConnected => SessionState.Active,
                    NativeWts.WTS_CONNECTSTATE_CLASS.WTSDisconnected => SessionState.Disconnected,
                    _ => SessionState.Disconnected
                };

                // Protocol 2 = RDP
                var type = protocol == 2 ? SessionType.Rdp : SessionType.Local;

                results.Add(new TrackedSession
                {
                    EventId = "temp",
                    SessionId = info.SessionId,
                    Username = user,
                    Domain = string.IsNullOrWhiteSpace(domain) ? null : domain,
                    SessionType = type,
                    State = state,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    LastObservedUtc = DateTimeOffset.UtcNow,
                    StateChangedAtUtc = DateTimeOffset.UtcNow,
                    ClientName = string.IsNullOrWhiteSpace(clientName) ? null : clientName,
                    ClientAddress = address
                });
            }
        }
        finally
        {
            NativeWts.WTSFreeMemory(ptr);
        }

        return results;
    }

    private static string? QueryString(int sessionId, NativeWts.WTS_INFO_CLASS infoClass)
    {
        if (!NativeWts.WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
            return null;

        try
        {
            return Marshal.PtrToStringUni(buffer)?.Trim();
        }
        finally
        {
            NativeWts.WTSFreeMemory(buffer);
        }
    }

    private static ushort QueryUInt16(int sessionId, NativeWts.WTS_INFO_CLASS infoClass)
    {
        if (!NativeWts.WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out var bytes) || bytes < 2)
            return 0;

        try
        {
            return (ushort)Marshal.ReadInt16(buffer);
        }
        finally
        {
            NativeWts.WTSFreeMemory(buffer);
        }
    }

    private static string? QueryClientAddress(int sessionId)
    {
        if (!NativeWts.WTSQuerySessionInformation(IntPtr.Zero, sessionId, NativeWts.WTS_INFO_CLASS.WTSClientAddress, out var buffer, out _))
            return null;

        try
        {
            var addr = Marshal.PtrToStructure<NativeWts.WTS_CLIENT_ADDRESS>(buffer);
            // AF_INET = 2
            if (addr.AddressFamily == 2 && addr.Address is { Length: >= 6 })
                return $"{addr.Address[2]}.{addr.Address[3]}.{addr.Address[4]}.{addr.Address[5]}";
            return null;
        }
        finally
        {
            NativeWts.WTSFreeMemory(buffer);
        }
    }
}

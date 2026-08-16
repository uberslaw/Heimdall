using System.Runtime.InteropServices;
using Heimdall.Shared;
using Heimdall.Shared.Contracts;

namespace Heimdall.Agent.Collectors;

internal static class NativeWts
{
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
        WTSClientName = 10,
        WTSClientAddress = 14,
        WTSClientProtocolType = 16,
        WTSSessionInfo = 24,
        /// <summary>BOOL — true when the session is remote (often still set after disconnect clears client fields).</summary>
        WTSIsRemoteSession = 29
    }

    /// <summary>WTS_PROTOCOL_TYPE_* values from WTSClientProtocolType.</summary>
    public const ushort ProtocolConsole = 0;
    public const ushort ProtocolIca = 1;
    public const ushort ProtocolRdp = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

    // Exact W entry points — CharSet.None defaults to ANSI (*A) which returns LPSTR;
    // pairing that with PtrToStringUni produces CJK mojibake (e.g. "Ch…" → "桃…").
    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    public static extern bool WTSEnumerateSessions(
        IntPtr hServer,
        int Reserved,
        int Version,
        out IntPtr ppSessionInfo,
        out int pCount);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
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
    public long LocalActiveSeconds { get; set; }
    public long LocalDisconnectedSeconds { get; set; }
    public long InboundRdpActiveSeconds { get; set; }
    public long InboundRdpDisconnectedSeconds { get; set; }
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
                // WTS often clears protocol/client fields on disconnect; keep the last known fingerprint.
                tracked.ClientName = liveSession.ClientName ?? tracked.ClientName;
                tracked.ClientAddress = liveSession.ClientAddress ?? tracked.ClientAddress;
                tracked.SessionType = StickySessionType(
                    liveSession.SessionType, tracked.SessionType, tracked.ClientName, tracked.ClientAddress);
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

    /// <summary>True when any WTS session is Active (connected) — used to defer silent client updates.</summary>
    public bool HasActiveInteractiveSession
    {
        get
        {
            lock (_gate)
                return _sessions.Values.Any(s => s.State == SessionState.Active);
        }
    }

    /// <summary>
    /// Primary interactive user for fleet snapshots.
    /// Prefer a non-ops Active session, then any non-ops interactive, then Active (incl. ops),
    /// then any interactive — so a transient ops.* fixer does not steal the Live USER label
    /// when a normal user session is still present.
    /// </summary>
    public string? TryGetPrimaryInteractiveUsername()
    {
        lock (_gate)
        {
            static bool HasUser(TrackedSession s) => !string.IsNullOrWhiteSpace(s.Username);
            static bool IsOps(TrackedSession s) => SupportAccount.IsOpsSupport(s.Username, s.Domain);

            var interactive = _sessions.Values
                .Where(s => (s.State is SessionState.Active or SessionState.Disconnected) && HasUser(s))
                .OrderBy(s => s.SessionId)
                .ToList();
            if (interactive.Count == 0)
                return null;

            var pick =
                interactive.FirstOrDefault(s => s.State == SessionState.Active && !IsOps(s))
                ?? interactive.FirstOrDefault(s => !IsOps(s))
                ?? interactive.FirstOrDefault(s => s.State == SessionState.Active)
                ?? interactive.FirstOrDefault();

            return pick is null ? null : FormatUser(pick.Domain, pick.Username);
        }
    }

    private static string FormatUser(string? domain, string username) =>
        string.IsNullOrWhiteSpace(domain) ? username : $"{domain}\\{username}";

    /// <summary>Resolve DOMAIN + username for a session (shared by process sampling).</summary>
    public static (string Username, string? Domain)? TryGetSessionUser(int sessionId)
    {
        var user = QueryString(sessionId, NativeWts.WTS_INFO_CLASS.WTSUserName);
        if (string.IsNullOrWhiteSpace(user))
            return null;

        var domain = QueryString(sessionId, NativeWts.WTS_INFO_CLASS.WTSDomainName);
        return (user, string.IsNullOrWhiteSpace(domain) ? null : domain);
    }

    private static void AccumulateTime(TrackedSession tracked, DateTimeOffset now)
    {
        var delta = (long)Math.Max(0, (now - tracked.StateChangedAtUtc).TotalSeconds);
        if (delta > 0)
        {
            var inbound = tracked.SessionType == SessionType.Rdp;
            if (tracked.State == SessionState.Active)
            {
                tracked.ActiveSeconds += delta;
                if (inbound) tracked.InboundRdpActiveSeconds += delta;
                else tracked.LocalActiveSeconds += delta;
            }
            else if (tracked.State == SessionState.Disconnected)
            {
                tracked.DisconnectedSeconds += delta;
                if (inbound) tracked.InboundRdpDisconnectedSeconds += delta;
                else tracked.LocalDisconnectedSeconds += delta;
            }
        }

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
        DisconnectedSeconds = s.DisconnectedSeconds,
        LocalActiveSeconds = s.LocalActiveSeconds,
        LocalDisconnectedSeconds = s.LocalDisconnectedSeconds,
        InboundRdpActiveSeconds = s.InboundRdpActiveSeconds,
        InboundRdpDisconnectedSeconds = s.InboundRdpDisconnectedSeconds
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
                var winStation = Marshal.PtrToStringUni(info.pWinStationName)?.Trim();
                var address = QueryClientAddress(info.SessionId);

                var state = info.State switch
                {
                    NativeWts.WTS_CONNECTSTATE_CLASS.WTSActive => SessionState.Active,
                    NativeWts.WTS_CONNECTSTATE_CLASS.WTSConnected => SessionState.Active,
                    NativeWts.WTS_CONNECTSTATE_CLASS.WTSDisconnected => SessionState.Disconnected,
                    _ => SessionState.Disconnected
                };

                var resolvedClientName = string.IsNullOrWhiteSpace(clientName) ? null : clientName;
                var isRemote = QueryBool(info.SessionId, NativeWts.WTS_INFO_CLASS.WTSIsRemoteSession);
                var type = ClassifySessionType(winStation, protocol, resolvedClientName, address, isRemote);

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
                    ClientName = resolvedClientName,
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

    /// <summary>
    /// Classify by protocol / RDP- WinStation / WTSIsRemoteSession first. Console alone must not force Local when
    /// the session is RDP (common when someone RDPs into the console session).
    /// RDP-to-self is still inbound RDP — physical presence is not what this field means.
    /// </summary>
    internal static SessionType ClassifySessionType(
        string? winStation,
        ushort protocol,
        string? clientName = null,
        string? clientAddress = null,
        bool isRemoteSession = false)
    {
        if (protocol is NativeWts.ProtocolRdp or NativeWts.ProtocolIca)
            return SessionType.Rdp;

        if (!string.IsNullOrWhiteSpace(winStation)
            && (winStation.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase)
                || winStation.StartsWith("ICA-", StringComparison.OrdinalIgnoreCase)))
            return SessionType.Rdp;

        // Survives disconnect when WTS clears protocol/client (rack hosts / RDP-to-console).
        if (isRemoteSession)
            return SessionType.Rdp;

        // Corroboration: remote client fingerprint on an otherwise "Console"/protocol-0 session
        // (seen when inbound RDP lands on the console WinStation).
        if (HasRemoteClientFingerprint(clientName, clientAddress))
            return SessionType.Rdp;

        return SessionType.Local;
    }

    /// <summary>
    /// After disconnect, WTS often reports Console/protocol-0 with empty client fields even though
    /// the session was inbound RDP. Prefer a retained RDP classify or client fingerprint over a fresh Local.
    /// </summary>
    internal static SessionType StickySessionType(
        SessionType classified,
        SessionType previous,
        string? clientName,
        string? clientAddress)
    {
        if (classified == SessionType.Rdp || previous == SessionType.Rdp)
            return SessionType.Rdp;
        if (HasRemoteClientFingerprint(clientName, clientAddress))
            return SessionType.Rdp;
        return classified;
    }

    internal static bool HasRemoteClientFingerprint(string? clientName, string? clientAddress)
    {
        if (!string.IsNullOrWhiteSpace(clientName))
            return true;

        if (string.IsNullOrWhiteSpace(clientAddress))
            return false;

        var addr = clientAddress.Trim();
        return addr is not ("0.0.0.0" or "::" or "::1");
    }

    private static string? QueryString(int sessionId, NativeWts.WTS_INFO_CLASS infoClass)
    {
        if (!NativeWts.WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out var bytes)
            || buffer == IntPtr.Zero
            || bytes <= 0)
            return null;

        try
        {
            // WTSQuerySessionInformationW returns a null-terminated UTF-16 string.
            var raw = Marshal.PtrToStringUni(buffer, bytes / 2)?.TrimEnd('\0').Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            // Belt-and-suspenders: if an older build/path still handed us ANSI-as-UTF16 junk, recover.
            return WindowsAccountEncoding.RepairAccountField(raw);
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

    private static bool QueryBool(int sessionId, NativeWts.WTS_INFO_CLASS infoClass)
    {
        if (!NativeWts.WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out var bytes)
            || buffer == IntPtr.Zero
            || bytes < 1)
            return false;

        try
        {
            // WTSIsRemoteSession returns a BOOL (4 bytes on Windows); accept 1-byte too.
            if (bytes >= 4)
                return Marshal.ReadInt32(buffer) != 0;
            return Marshal.ReadByte(buffer) != 0;
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
            // AF_INET = 2 — Address[2..5] are the IPv4 octets (bytes 0-1 are port / reserved).
            if (addr.AddressFamily == 2 && addr.Address is { Length: >= 6 })
                return $"{addr.Address[2]}.{addr.Address[3]}.{addr.Address[4]}.{addr.Address[5]}";

            // AF_INET6 = 23 — Address[2..17] are the 16 IPv6 octets.
            if (addr.AddressFamily == 23 && addr.Address is { Length: >= 18 })
            {
                var bytes = new byte[16];
                Buffer.BlockCopy(addr.Address, 2, bytes, 0, 16);
                return new System.Net.IPAddress(bytes).ToString();
            }

            return null;
        }
        finally
        {
            NativeWts.WTSFreeMemory(buffer);
        }
    }
}

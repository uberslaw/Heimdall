using System.Diagnostics;
using System.Runtime.Versioning;
using Heimdall.Shared.Contracts;

namespace Heimdall.Agent.Collectors;

public sealed class ProcessCollector
{
    private readonly Dictionary<string, ProcessRunDto> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    [SupportedOSPlatform("windows")]
    public IReadOnlyList<ProcessRunDto> Sample(
        string hostname,
        AgentConfigDto config,
        Func<int, (string Username, string? Domain)?> sessionUserLookup)
    {
        var now = DateTimeOffset.UtcNow;
        var include = new HashSet<string>(
            config.IncludeProcesses.Concat(config.KnownApps.Where(a => a.Enabled).Select(a => a.ProcessName)),
            StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(config.ExcludeProcesses, StringComparer.OrdinalIgnoreCase);

        if (include.Count == 0)
            return [];

        var wmiPaths = ProcessPathResolver.QueryWmiPaths();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updates = new List<ProcessRunDto>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (exclude.Contains(name) || !include.Contains(name))
                    continue;

                string? path = ProcessPathResolver.TryGetPath(process, wmiPaths);

                var sessionInfo = sessionUserLookup(process.SessionId);
                var user = sessionInfo is null
                    ? "unknown"
                    : string.IsNullOrEmpty(sessionInfo.Value.Domain)
                        ? sessionInfo.Value.Username
                        : $"{sessionInfo.Value.Domain}\\{sessionInfo.Value.Username}";

                var key = $"{process.Id}:{name}";
                seenKeys.Add(key);

                lock (_gate)
                {
                    if (_open.TryGetValue(key, out var existing))
                    {
                        var updated = existing with
                        {
                            LastSeenAtUtc = now,
                            SampleCount = existing.SampleCount + 1,
                            Username = user
                        };
                        _open[key] = updated;
                        updates.Add(updated);
                    }
                    else
                    {
                        var created = new ProcessRunDto
                        {
                            RunId = $"{hostname}:{process.Id}:{name}:{now.ToUnixTimeMilliseconds()}",
                            Hostname = hostname,
                            Username = user,
                            ProcessName = name,
                            ExecutablePath = path,
                            ProcessId = process.Id,
                            StartedAtUtc = SafeStartTime(process) ?? now,
                            LastSeenAtUtc = now,
                            SampleCount = 1
                        };
                        _open[key] = created;
                        updates.Add(created);
                    }
                }
            }
            catch
            {
                // Ignore processes that disappear mid-enumeration
            }
            finally
            {
                process.Dispose();
            }
        }

        lock (_gate)
        {
            foreach (var key in _open.Keys.Where(k => !seenKeys.Contains(k)).ToList())
            {
                var ended = _open[key] with { EndedAtUtc = now, LastSeenAtUtc = now };
                updates.Add(ended);
                _open.Remove(key);
            }
        }

        return updates;
    }

    /// <summary>One-shot inventory of running processes for server-side app analysis.</summary>
    /// <param name="throttle">When true, yields briefly between processes to keep agent CPU impact low (~5% target).</param>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<DiscoveredProcessDto> DiscoverInventory(bool throttle = false)
    {
        var wmiPaths = ProcessPathResolver.QueryWmiPaths();
        // Key by name + path so the same exe at different locations is reported separately (Spec discovery).
        var map = new Dictionary<string, DiscoveredProcessDto>(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var path = ProcessPathResolver.TryGetPath(process, wmiPaths);
                if (!Heimdall.Shared.DiscoveryCatalogFilter.IsEligible(name, path))
                    continue;
                var key = $"{name}\0{(path ?? "").Trim()}";
                if (!map.ContainsKey(key))
                {
                    var version = TryGetVersionInfo(path);
                    map[key] = new DiscoveredProcessDto
                    {
                        ProcessName = name,
                        DisplayName = name,
                        ExecutablePath = path,
                        FileVersion = version?.FileVersion,
                        ProductVersion = version?.ProductVersion,
                        CompanyName = version?.CompanyName,
                        FileDescription = version?.FileDescription
                    };
                }
            }
            catch { /* ignore */ }
            finally { process.Dispose(); }

            if (throttle && ++n % 8 == 0)
                Thread.Sleep(15);
        }
        return map.Values.OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ExecutablePath ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Best-effort Win32 file version read; honest null fallback when the path is missing/inaccessible/unversioned.</summary>
    [SupportedOSPlatform("windows")]
    private static FileVersionInfo? TryGetVersionInfo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return FileVersionInfo.GetVersionInfo(path);
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? SafeStartTime(Process process)
    {
        try { return new DateTimeOffset(process.StartTime.ToUniversalTime()); }
        catch { return null; }
    }
}

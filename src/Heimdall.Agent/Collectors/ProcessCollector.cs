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

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updates = new List<ProcessRunDto>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (exclude.Contains(name) || !include.Contains(name))
                    continue;

                string? path = null;
                try { path = process.MainModule?.FileName; } catch { /* access denied is fine */ }

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

    private static DateTimeOffset? SafeStartTime(Process process)
    {
        try { return new DateTimeOffset(process.StartTime.ToUniversalTime()); }
        catch { return null; }
    }
}

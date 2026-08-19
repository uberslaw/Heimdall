using System.Net.NetworkInformation;
using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Heimdall.Api.Services;

public class RemoteMachineService(HeimdallDbContext db, ConfigService config, ILogger<RemoteMachineService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public const int DefaultConfigRefreshSeconds = 300;

    /// <summary>
    /// Agent is "online" when <c>LastSeenUtc</c> is within this window.
    /// Sized for ~2 missed 30s fleet snapshots after shutdown (Live orange halo / Online Status).
    /// Heartbeat ingest and fleet snapshots both refresh <c>LastSeenUtc</c>; either stopping
    /// ends contact — there is no separate ping that keeps hosts looking online.
    /// </summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Client Version deploy-queue "Online" stays deliberately looser so brief gaps do not
    /// flip hosts offline while updates are queued. Live / Online Status use <see cref="OnlineWindow"/>.
    /// </summary>
    public static readonly TimeSpan ClientVersionOnlineWindow = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan RdpVerifyDelay = TimeSpan.FromSeconds(2);

    public static string FormatAgentContact(DateTimeOffset utc) =>
        utc.ToLocalTime().ToString("dd/MM/yyyy - HH:mm");

    public async Task<IReadOnlyList<RemoteMachineRow>> ListAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var onlineCutoff = now.Add(-OnlineWindow);

        var machines = await db.Machines.AsNoTracking()
            .OrderBy(m => m.Hostname)
            .ToListAsync(ct);

        return machines
            .Select(m => ToRow(m, onlineCutoff, now))
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Rows for a specific set of hostnames (e.g. a Staff Access group's assigned machines), in the given order.</summary>
    public async Task<IReadOnlyList<RemoteMachineRow>> ListForHostnamesAsync(IEnumerable<string> hostnames, CancellationToken ct)
    {
        var wanted = hostnames.ToList();
        if (wanted.Count == 0) return [];

        var now = DateTimeOffset.UtcNow;
        var onlineCutoff = now.Add(-OnlineWindow);

        var machines = await db.Machines.AsNoTracking()
            .Where(m => wanted.Contains(m.Hostname))
            .ToListAsync(ct);
        var byHost = machines.ToDictionary(m => m.Hostname, StringComparer.OrdinalIgnoreCase);

        return wanted
            .Select(h => byHost.TryGetValue(h, out var m) ? ToRow(m, onlineCutoff, now) : null)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();
    }

    public async Task<RemoteMachineRow?> GetRowAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        return ToRow(machine, now.Add(-OnlineWindow), now);
    }

    public async Task<PingResult> PingAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines
            .FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null)
            return new PingResult(hostname, null, false, "Machine not registered");

        var target = ResolveProbeTarget(machine);
        PingResult result;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target, 2000);
            var ok = reply.Status == IPStatus.Success;
            logger.LogInformation("Ping {Target} ({Host}): {Status}", target, hostname, reply.Status);
            var detail = ok ? $"{reply.RoundtripTime} ms" : reply.Status.ToString();
            result = new PingResult(hostname, target, ok, detail);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ping failed for {Host} ({Target})", hostname, target);
            result = new PingResult(hostname, target, false, ex.Message);
        }

        machine.LastPingResultJson = JsonSerializer.Serialize(result, JsonOptions);
        machine.LastPingUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<RdpProbeService.RdpProbeResult?> ProbeRdpAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null)
            return null;

        var target = ResolveProbeTarget(machine);
        logger.LogInformation("RDP probe requested for {Host} targeting {Target}", hostname, target);

        var result = await RdpProbeService.ProbeAsync(target, ct: ct);
        machine.LastRdpProbeResultJson = RdpProbeService.ToJson(result);
        machine.LastRdpProbeUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<bool> QueueRestartTermServiceAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null)
            return false;

        var pending = DeserializeCommands(machine.PendingCommandsJson);
        if (!pending.Contains(RemoteMachineCommands.RestartTermService, StringComparer.OrdinalIgnoreCase))
            pending.Add(RemoteMachineCommands.RestartTermService);

        machine.PendingCommandsJson = SerializeCommands(pending);

        var now = DateTimeOffset.UtcNow;
        var refreshSeconds = await ResolveConfigRefreshSecondsAsync(hostname, ct);
        machine.RestartRdsProgressJson = SerializeProgress(new RestartRdsProgress
        {
            Phase = RestartRdsPhases.Queued,
            StartedUtc = now,
            LastEventUtc = now,
            ExpectedPickupUtc = now.AddSeconds(refreshSeconds),
            ConfigRefreshSeconds = refreshSeconds,
            AttemptCount = 0,
            Detail = "Restart queued — waiting for agent to pick up command"
        });

        await db.SaveChangesAsync(ct);
        logger.LogWarning("Queued {Command} for {Host} (expected agent pickup by {PickupUtc:u})",
            RemoteMachineCommands.RestartTermService, hostname, now.AddSeconds(refreshSeconds));
        return true;
    }

    /// <summary>Apply remote-machine heartbeat fields; returns true when post-restart RDP verification should run.</summary>
    public bool ApplyHeartbeat(Machine machine, HeartbeatDto heartbeat)
    {
        if (!string.IsNullOrWhiteSpace(heartbeat.PrimaryIpAddress))
            machine.LastIp = heartbeat.PrimaryIpAddress.Trim();

        if (!string.IsNullOrWhiteSpace(heartbeat.TermServiceStatus))
        {
            machine.TermServiceStatus = heartbeat.TermServiceStatus.Trim();
            machine.TermServiceCheckedUtc = heartbeat.TimestampUtc;
        }

        var progress = DeserializeProgress(machine.RestartRdsProgressJson);
        var hasActiveRestart = progress is not null && IsActiveRestartPhase(progress.Phase);

        foreach (var report in heartbeat.CommandExecutionReports)
        {
            if (!string.Equals(report.Command, RemoteMachineCommands.RestartTermService, StringComparison.OrdinalIgnoreCase))
                continue;

            progress ??= NewRestartProgress(heartbeat.TimestampUtc, DefaultConfigRefreshSeconds);
            if (report.Success)
                continue;

            var refreshSeconds = progress.ConfigRefreshSeconds > 0
                ? progress.ConfigRefreshSeconds
                : DefaultConfigRefreshSeconds;

            progress.Phase = RestartRdsPhases.Retrying;
            progress.AttemptCount = Math.Max(progress.AttemptCount, 0) + 1;
            progress.LastEventUtc = report.ExecutedUtc;
            progress.ExpectedPickupUtc = report.ExecutedUtc.AddSeconds(refreshSeconds);
            progress.Detail = string.IsNullOrWhiteSpace(report.Detail)
                ? "TermService restart failed on agent"
                : report.Detail;
            hasActiveRestart = true;

            logger.LogWarning("RestartTermService failed on {Host} (attempt {Attempt}): {Detail}",
                machine.Hostname, progress.AttemptCount, progress.Detail);
        }

        var verifyRdp = false;
        if (heartbeat.AcknowledgedCommands.Count > 0)
        {
            var pending = DeserializeCommands(machine.PendingCommandsJson);
            var ackedRestart = heartbeat.AcknowledgedCommands.Any(c =>
                string.Equals(c, RemoteMachineCommands.RestartTermService, StringComparison.OrdinalIgnoreCase));

            foreach (var ack in heartbeat.AcknowledgedCommands)
                pending.RemoveAll(c => string.Equals(c, ack, StringComparison.OrdinalIgnoreCase));
            machine.PendingCommandsJson = pending.Count == 0 ? null : SerializeCommands(pending);

            if (ackedRestart)
            {
                progress ??= NewRestartProgress(heartbeat.TimestampUtc, DefaultConfigRefreshSeconds);
                progress.Phase = RestartRdsPhases.Acknowledged;
                progress.AcknowledgedUtc = heartbeat.TimestampUtc;
                progress.LastEventUtc = heartbeat.TimestampUtc;
                progress.ExpectedPickupUtc = null;
                progress.Detail = "TermService restart executed and acknowledged by agent";
                verifyRdp = true;
                hasActiveRestart = true;

                logger.LogInformation("RestartTermService acknowledged for {Host}; queueing RDP verification", machine.Hostname);
            }
        }

        if (!verifyRdp && progress is not null && IsWaitingForAgentPhase(progress.Phase))
        {
            verifyRdp = TryFallbackVerifyFromTermService(machine, progress, heartbeat.TimestampUtc);
            if (verifyRdp)
                hasActiveRestart = true;
        }

        if (hasActiveRestart && progress is not null)
            machine.RestartRdsProgressJson = SerializeProgress(progress);

        return verifyRdp;
    }

    /// <summary>
    /// Fallback when agent ack was not received: TermService reported Running after the expected pickup window.
    /// </summary>
    private bool TryFallbackVerifyFromTermService(Machine machine, RestartRdsProgress progress, DateTimeOffset now)
    {
        if (progress.ExpectedPickupUtc is null || now < progress.ExpectedPickupUtc)
            return false;

        if (!string.Equals(machine.TermServiceStatus, "Running", StringComparison.OrdinalIgnoreCase))
            return false;

        if (machine.TermServiceCheckedUtc is null || machine.TermServiceCheckedUtc <= progress.StartedUtc)
            return false;

        progress.Phase = RestartRdsPhases.Acknowledged;
        progress.AcknowledgedUtc = machine.TermServiceCheckedUtc;
        progress.LastEventUtc = now;
        progress.ExpectedPickupUtc = null;
        progress.Detail = "TermService running — verifying RDP (agent ack not received)";

        logger.LogInformation(
            "Restart fallback for {Host}: TermService Running after expected pickup; queueing RDP verification",
            machine.Hostname);
        return true;
    }

    public async Task VerifyRestartRdpAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null)
            return;

        var progress = DeserializeProgress(machine.RestartRdsProgressJson);
        if (progress is null || !IsActiveRestartPhase(progress.Phase))
            return;

        progress.Phase = RestartRdsPhases.Verifying;
        progress.LastEventUtc = DateTimeOffset.UtcNow;
        progress.ExpectedPickupUtc = null;
        progress.Detail = "Testing RDP — verifying connections…";
        machine.RestartRdsProgressJson = SerializeProgress(progress);
        await db.SaveChangesAsync(ct);

        try
        {
            await Task.Delay(RdpVerifyDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null)
            return;

        progress = DeserializeProgress(machine.RestartRdsProgressJson);
        if (progress is null)
            return;

        var target = ResolveProbeTarget(machine);
        logger.LogInformation("Post-restart RDP verification for {Host} targeting {Target}", hostname, target);

        var result = await RdpProbeService.ProbeAsync(target, ct: ct);
        machine.LastRdpProbeResultJson = RdpProbeService.ToJson(result);
        machine.LastRdpProbeUtc = DateTimeOffset.UtcNow;

        progress.RdpResponding = result.RdpResponding;
        progress.LastEventUtc = DateTimeOffset.UtcNow;

        if (result.RdpResponding)
        {
            progress.Phase = RestartRdsPhases.Succeeded;
            progress.Detail = $"Restart complete — RDP accepting connections ({result.ElapsedMs} ms)";
            logger.LogInformation("Post-restart RDP verification succeeded for {Host}", hostname);
        }
        else
        {
            progress.Phase = RestartRdsPhases.Failed;
            progress.Detail = $"Restart done but RDP not responding — {result.Error ?? "unknown"}";
            logger.LogWarning("Post-restart RDP verification failed for {Host}: {Error}", hostname, result.Error);
        }

        machine.RestartRdsProgressJson = SerializeProgress(progress);
        await db.SaveChangesAsync(ct);
    }

    public static string FormatRestartProgressLabel(RemoteMachineRow row)
    {
        var progress = row.RestartProgress;
        if (progress is null)
            return row.RestartTermServiceQueued ? "Restart queued" : "";

        if (row.ShowCountdown && row.CountdownExpired)
        {
            return progress.Phase switch
            {
                RestartRdsPhases.Retrying => progress.AttemptCount > 0
                    ? $"Retrying — past expected pickup (attempt {progress.AttemptCount})"
                    : "Retrying — past expected pickup",
                _ => "Queued — past expected pickup, still waiting"
            };
        }

        return progress.Phase switch
        {
            RestartRdsPhases.Queued when !row.IsOnline => "Queued (agent offline)",
            RestartRdsPhases.Queued => "Queued — waiting for agent",
            RestartRdsPhases.Retrying => progress.AttemptCount > 0
                ? $"Restart failed — retrying (attempt {progress.AttemptCount})"
                : "Restart failed — retrying",
            RestartRdsPhases.Acknowledged => "Restart done — preparing RDP test",
            RestartRdsPhases.Verifying => "Testing RDP…",
            RestartRdsPhases.Succeeded => "Restart complete — RDP accepting",
            RestartRdsPhases.Failed => "Restart done — RDP not responding",
            _ => progress.Phase
        };
    }

    public static string RestartProgressBadgeClass(RemoteMachineRow row)
    {
        var phase = row.RestartProgress?.Phase;
        return phase switch
        {
            RestartRdsPhases.Succeeded => "badge-active",
            RestartRdsPhases.Failed => "badge-expired",
            RestartRdsPhases.Retrying => "badge-warn",
            RestartRdsPhases.Verifying or RestartRdsPhases.Acknowledged => "badge-rdp",
            RestartRdsPhases.Queued when !row.IsOnline => "badge-ended",
            RestartRdsPhases.Queued when row.CountdownExpired => "badge-warn",
            _ => "badge-warn"
        };
    }

    public static bool ShowRestartProgress(RemoteMachineRow row) =>
        row.RestartProgress is not null || row.RestartTermServiceQueued;

    public static RestartStatusDto ToRestartStatusDto(RemoteMachineRow row) => new(
        row.Hostname,
        row.RestartProgress?.Phase,
        FormatRestartProgressLabel(row),
        row.RestartProgress?.Detail,
        row.ShowCountdown,
        row.CountdownUntilUtc,
        row.CountdownExpired,
        row.RdpResponding,
        row.RdpError,
        row.LastRdpProbeUtc,
        row.RestartProgress is not null && IsActiveRestartPhase(row.RestartProgress.Phase));

    internal static List<string> DeserializeCommands(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task<int> ResolveConfigRefreshSecondsAsync(string hostname, CancellationToken ct)
    {
        try
        {
            var agentConfig = await config.ResolveForHostAsync(hostname, ct);
            return Math.Max(60, agentConfig.ConfigRefreshSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve config refresh for {Host}; using default {Seconds}s",
                hostname, DefaultConfigRefreshSeconds);
            return DefaultConfigRefreshSeconds;
        }
    }

    private static string SerializeCommands(List<string> commands) =>
        JsonSerializer.Serialize(commands.Distinct(StringComparer.OrdinalIgnoreCase), JsonOptions);

    private static string ResolveProbeTarget(Machine machine) =>
        string.IsNullOrWhiteSpace(machine.LastIp) ? machine.Hostname : machine.LastIp;

    private static RestartRdsProgress NewRestartProgress(DateTimeOffset utc, int configRefreshSeconds) => new()
    {
        Phase = RestartRdsPhases.Queued,
        StartedUtc = utc,
        LastEventUtc = utc,
        ExpectedPickupUtc = utc.AddSeconds(configRefreshSeconds),
        ConfigRefreshSeconds = configRefreshSeconds,
        AttemptCount = 0
    };

    public static RestartRdsProgress? DeserializeProgress(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<RestartRdsProgress>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string SerializeProgress(RestartRdsProgress progress) =>
        JsonSerializer.Serialize(progress, JsonOptions);

    public static bool IsActiveRestartPhase(string? phase) => phase switch
    {
        RestartRdsPhases.Queued or RestartRdsPhases.Retrying or RestartRdsPhases.Acknowledged or RestartRdsPhases.Verifying => true,
        _ => false
    };

    public static bool IsWaitingForAgentPhase(string? phase) => phase switch
    {
        RestartRdsPhases.Queued or RestartRdsPhases.Retrying => true,
        _ => false
    };

    private static RemoteMachineRow ToRow(Machine m, DateTimeOffset onlineCutoff, DateTimeOffset now)
    {
        var pending = DeserializeCommands(m.PendingCommandsJson);
        var restartQueued = pending.Contains(RemoteMachineCommands.RestartTermService, StringComparer.OrdinalIgnoreCase);
        var progress = DeserializeProgress(m.RestartRdsProgressJson);

        if (progress is null && restartQueued)
        {
            progress = new RestartRdsProgress
            {
                Phase = RestartRdsPhases.Queued,
                StartedUtc = m.LastSeenUtc,
                LastEventUtc = m.LastSeenUtc,
                ExpectedPickupUtc = m.LastSeenUtc.AddSeconds(DefaultConfigRefreshSeconds),
                ConfigRefreshSeconds = DefaultConfigRefreshSeconds,
                Detail = "Restart queued — waiting for agent"
            };
        }
        else if (progress is not null && restartQueued && progress.Phase is RestartRdsPhases.Succeeded or RestartRdsPhases.Failed)
        {
            var refreshSeconds = progress.ConfigRefreshSeconds > 0
                ? progress.ConfigRefreshSeconds
                : DefaultConfigRefreshSeconds;
            var queuedAt = progress.LastEventUtc ?? now;
            progress = new RestartRdsProgress
            {
                Phase = RestartRdsPhases.Queued,
                StartedUtc = queuedAt,
                LastEventUtc = queuedAt,
                ExpectedPickupUtc = queuedAt.AddSeconds(refreshSeconds),
                ConfigRefreshSeconds = refreshSeconds,
                Detail = "Restart queued — waiting for agent"
            };
        }

        var showCountdown = progress is not null
            && IsWaitingForAgentPhase(progress.Phase)
            && progress.ExpectedPickupUtc is not null;

        var countdownUntil = showCountdown ? progress!.ExpectedPickupUtc : null;
        var countdownExpired = showCountdown && countdownUntil <= now;

        var rdp = RdpProbeService.TryParseStored(m.LastRdpProbeResultJson);
        PingResult? ping = null;
        if (!string.IsNullOrWhiteSpace(m.LastPingResultJson))
        {
            try { ping = JsonSerializer.Deserialize<PingResult>(m.LastPingResultJson, JsonOptions); }
            catch { /* ignore */ }
        }

        var isOnline = m.LastSeenUtc >= onlineCutoff;

        return new RemoteMachineRow(
            m.Hostname,
            string.IsNullOrWhiteSpace(m.FriendlyName) ? null : m.FriendlyName.Trim(),
            m.LastIp,
            m.LastSeenUtc,
            isOnline,
            m.TermServiceStatus,
            m.TermServiceCheckedUtc,
            ping?.Reachable,
            ping?.Detail,
            m.LastPingUtc,
            rdp?.RdpResponding,
            rdp?.Error ?? (m.LastRdpProbeUtc is null ? null : "—"),
            m.LastRdpProbeUtc,
            restartQueued,
            progress,
            showCountdown,
            countdownUntil,
            countdownExpired);
    }

    public sealed class RestartRdsProgress
    {
        public string Phase { get; set; } = RestartRdsPhases.Queued;
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? LastEventUtc { get; set; }
        public DateTimeOffset? ExpectedPickupUtc { get; set; }
        public DateTimeOffset? AcknowledgedUtc { get; set; }
        public int ConfigRefreshSeconds { get; set; } = DefaultConfigRefreshSeconds;
        public int AttemptCount { get; set; }
        public string? Detail { get; set; }
        public bool? RdpResponding { get; set; }
    }

    public sealed record RemoteMachineRow(
        string Hostname,
        string? FriendlyName,
        string? LastIp,
        DateTimeOffset LastSeenUtc,
        bool IsOnline,
        string? TermServiceStatus,
        DateTimeOffset? TermServiceCheckedUtc,
        bool? PingReachable,
        string? PingDetail,
        DateTimeOffset? LastPingUtc,
        bool? RdpResponding,
        string? RdpError,
        DateTimeOffset? LastRdpProbeUtc,
        bool RestartTermServiceQueued,
        RestartRdsProgress? RestartProgress,
        bool ShowCountdown,
        DateTimeOffset? CountdownUntilUtc,
        bool CountdownExpired)
    {
        public string DisplayName =>
            string.IsNullOrWhiteSpace(FriendlyName) ? Hostname : FriendlyName!;
    }

    public sealed record PingResult(
        string Hostname,
        string? Target,
        bool Reachable,
        string Detail);

    public sealed record RestartStatusDto(
        string Hostname,
        string? Phase,
        string Label,
        string? Detail,
        bool ShowCountdown,
        DateTimeOffset? CountdownUntilUtc,
        bool CountdownExpired,
        bool? RdpResponding,
        string? RdpError,
        DateTimeOffset? LastRdpProbeUtc,
        bool IsActive);
}

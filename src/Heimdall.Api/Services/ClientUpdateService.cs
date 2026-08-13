using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Heimdall.Api.Services;

/// <summary>Queues silent UpdateClient commands and tracks progress from agent reports / version bumps.</summary>
public sealed class ClientUpdateService(
    HeimdallDbContext db,
    ClientPackReadinessService packReadiness,
    ILogger<ClientUpdateService> logger)
{
    /// <summary>Shown when the agent cannot process UpdateClient (pre-v3 / legacy).</summary>
    public const string BootstrapRequiredDetail =
        "Needs one-time bootstrap install (Launch Control push or Install.lnk). This agent does not support UpdateClient.";

    /// <summary>Default minutes with Applying + no heartbeat (or stale progress) before Stuck.</summary>
    public const int DefaultApplyingStuckMinutes = 15;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<(int Queued, string Message)> QueueUpdatesAsync(
        IReadOnlyList<string> hostnames,
        CancellationToken ct = default)
    {
        var status = packReadiness.GetStatus();
        if (!status.DeployUnlocked || status.Status != ClientPackStatus.Ready)
            return (0, status.Message ?? "Client pack is not ready — Pack first.");

        var (_, zipSha) = packReadiness.EnsureZip(status.PackFolder, status.LiveSourceFingerprint ?? status.PackSourceFingerprint);
        var version = status.PackProductVersion ?? PublishedVersionService.DefaultVersion;

        var queued = 0;
        var needsBootstrap = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var raw in hostnames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hostname = raw.Trim();
            if (hostname.Length == 0)
                continue;

            var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
            if (machine is null)
            {
                logger.LogWarning("Deploy Client: unknown hostname {Host}", hostname);
                continue;
            }

            // Pre-UpdateClient agents (simple version < 3 / legacy / unknown) cannot silent-update.
            if (!VersionCompare.SupportsUpdateClient(machine.AgentVersion))
            {
                ClearPendingUpdateClient(machine);
                machine.PendingClientUpdateJson = null;
                machine.ClientUpdateProgressJson = JsonSerializer.Serialize(new ClientUpdateProgressDto
                {
                    Phase = ClientUpdatePhases.Failed,
                    Detail = BootstrapRequiredDetail,
                    TargetVersion = version,
                    UpdatedUtc = now
                }, JsonOptions);
                needsBootstrap++;
                logger.LogInformation(
                    "Deploy Client: {Host} needs bootstrap (AgentVersion={Ver})",
                    hostname, machine.AgentVersion ?? "(null)");
                continue;
            }

            var pending = RemoteMachineService.DeserializeCommands(machine.PendingCommandsJson);
            if (!pending.Contains(RemoteMachineCommands.UpdateClient, StringComparer.OrdinalIgnoreCase))
                pending.Add(RemoteMachineCommands.UpdateClient);
            machine.PendingCommandsJson = JsonSerializer.Serialize(pending, JsonOptions);

            var request = new ClientUpdateRequestDto
            {
                Version = version,
                ZipSha256 = zipSha,
                DownloadPath = "/api/agent/client-pack",
                QueuedUtc = now
            };
            machine.PendingClientUpdateJson = JsonSerializer.Serialize(request, JsonOptions);
            machine.ClientUpdateProgressJson = JsonSerializer.Serialize(new ClientUpdateProgressDto
            {
                Phase = ClientUpdatePhases.Queued,
                Detail = "Waiting for agent to pick up UpdateClient",
                TargetVersion = version,
                UpdatedUtc = now
            }, JsonOptions);
            queued++;
        }

        await db.SaveChangesAsync(ct);
        if (queued == 0 && needsBootstrap == 0)
            return (0, "No machines queued.");
        if (queued == 0 && needsBootstrap > 0)
        {
            OpsFileLog.Write("DeployClient", $"queued=0; needsBootstrap={needsBootstrap}");
            return (0, $"{needsBootstrap} machine(s) need one-time bootstrap (Launch Control / Install.lnk) — agent does not support UpdateClient.");
        }

        if (needsBootstrap > 0)
        {
            OpsFileLog.Write("DeployClient", $"queued={queued}; needsBootstrap={needsBootstrap}; version={version}");
            return (queued, $"Queued silent update for {queued} machine(s); {needsBootstrap} need bootstrap install first.");
        }

        OpsFileLog.Write("DeployClient", $"queued={queued}; version={version}");
        return (queued, $"Queued silent update for {queued} machine(s).");
    }

    private void ClearPendingUpdateClient(Machine machine)
    {
        var pending = RemoteMachineService.DeserializeCommands(machine.PendingCommandsJson);
        pending.RemoveAll(c => string.Equals(c, RemoteMachineCommands.UpdateClient, StringComparison.OrdinalIgnoreCase));
        machine.PendingCommandsJson = pending.Count == 0 ? null : JsonSerializer.Serialize(pending, JsonOptions);
    }

    public static ClientUpdateRequestDto? DeserializeRequest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ClientUpdateRequestDto>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static ClientUpdateProgressDto? DeserializeProgress(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ClientUpdateProgressDto>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static ClientDepositRequestDto? DeserializeDepositRequest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ClientDepositRequestDto>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task ApplyHeartbeatAsync(Machine machine, HeartbeatDto heartbeat, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var target = DeserializeRequest(machine.PendingClientUpdateJson);

        // Progress from command reports
        foreach (var report in heartbeat.CommandExecutionReports)
        {
            if (string.Equals(report.Command, RemoteMachineCommands.UpdateClient, StringComparison.OrdinalIgnoreCase))
            {
                ApplyUpdateClientReport(machine, report, target);
                continue;
            }

            if (string.Equals(report.Command, RemoteMachineCommands.RestartAgent, StringComparison.OrdinalIgnoreCase)
                || string.Equals(report.Command, RemoteMachineCommands.CleanupClientStaging, StringComparison.OrdinalIgnoreCase)
                || string.Equals(report.Command, RemoteMachineCommands.DepositClientPack, StringComparison.OrdinalIgnoreCase))
            {
                // Do not clobber an in-flight Deploy progress line.
                if (target is not null)
                    continue;

                machine.ClientUpdateProgressJson = JsonSerializer.Serialize(new ClientUpdateProgressDto
                {
                    Phase = report.Success ? ClientUpdatePhases.Succeeded : ClientUpdatePhases.Failed,
                    Detail = string.IsNullOrWhiteSpace(report.Detail)
                        ? report.Command
                        : $"{report.Command}: {report.Detail}",
                    TargetVersion = string.Equals(report.Command, RemoteMachineCommands.DepositClientPack, StringComparison.OrdinalIgnoreCase)
                        ? DeserializeDepositRequest(machine.PendingClientDepositJson)?.Version
                        : null,
                    UpdatedUtc = report.ExecutedUtc
                }, JsonOptions);
            }
        }

        if (heartbeat.AcknowledgedCommands.Any(c =>
                string.Equals(c, RemoteMachineCommands.DepositClientPack, StringComparison.OrdinalIgnoreCase)))
        {
            machine.PendingClientDepositJson = null;
        }

        // Ack clears pending command token; also clear payload when acked or version matches
        if (heartbeat.AcknowledgedCommands.Any(c =>
                string.Equals(c, RemoteMachineCommands.UpdateClient, StringComparison.OrdinalIgnoreCase)))
        {
            // Keep payload until version matches so a mid-install restart can re-read if needed —
            // but ack means agent spawned installer; mark Applying.
            machine.ClientUpdateProgressJson = JsonSerializer.Serialize(new ClientUpdateProgressDto
            {
                Phase = ClientUpdatePhases.Applying,
                Detail = "Installer spawned — waiting for new agent version",
                TargetVersion = target?.Version,
                UpdatedUtc = now
            }, JsonOptions);
        }

        if (target is not null
            && !string.IsNullOrWhiteSpace(heartbeat.AgentVersion)
            && VersionCompare.CoreVersionsMatch(heartbeat.AgentVersion, target.Version))
        {
            machine.PendingClientUpdateJson = null;
            var pending = RemoteMachineService.DeserializeCommands(machine.PendingCommandsJson);
            pending.RemoveAll(c => string.Equals(c, RemoteMachineCommands.UpdateClient, StringComparison.OrdinalIgnoreCase));
            machine.PendingCommandsJson = pending.Count == 0 ? null : JsonSerializer.Serialize(pending, JsonOptions);
            machine.ClientUpdateProgressJson = JsonSerializer.Serialize(new ClientUpdateProgressDto
            {
                Phase = ClientUpdatePhases.Succeeded,
                Detail = $"Running client version {VersionCompare.TryGetSimpleVersion(heartbeat.AgentVersion)}",
                TargetVersion = target.Version,
                UpdatedUtc = now
            }, JsonOptions);
        }

        await Task.CompletedTask;
    }

    private void ApplyUpdateClientReport(Machine machine, CommandExecutionReportDto report, ClientUpdateRequestDto? target)
    {
        var phase = ClientUpdatePhases.Applying;
        var detail = report.Detail;
        if (!report.Success)
        {
            if (detail is not null && detail.Contains("DeferredWaitingForIdle", StringComparison.OrdinalIgnoreCase))
                phase = ClientUpdatePhases.DeferredWaitingForIdle;
            else
            {
                phase = ClientUpdatePhases.Failed;
                // Old agents hit TermServiceHelper → "Unknown command: UpdateClient".
                if (detail is not null
                    && detail.Contains("Unknown command", StringComparison.OrdinalIgnoreCase)
                    && detail.Contains(RemoteMachineCommands.UpdateClient, StringComparison.OrdinalIgnoreCase))
                {
                    detail = BootstrapRequiredDetail;
                    ClearPendingUpdateClient(machine);
                    machine.PendingClientUpdateJson = null;
                }
            }
        }
        else if (detail is not null && detail.Contains("DeferredWaitingForIdle", StringComparison.OrdinalIgnoreCase))
        {
            phase = ClientUpdatePhases.DeferredWaitingForIdle;
        }
        else if (detail is not null
                 && detail.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
                 && !detail.Contains("Failed", StringComparison.OrdinalIgnoreCase))
        {
            phase = ClientUpdatePhases.Downloading;
        }
        else if (detail is not null && detail.Contains("Applying", StringComparison.OrdinalIgnoreCase))
        {
            phase = ClientUpdatePhases.Applying;
        }

        machine.ClientUpdateProgressJson = JsonSerializer.Serialize(new ClientUpdateProgressDto
        {
            Phase = phase,
            Detail = detail,
            TargetVersion = target?.Version,
            UpdatedUtc = report.ExecutedUtc
        }, JsonOptions);
    }

    /// <summary>
    /// Queue RestartAgent for selected hosts. Refuses hosts with an in-flight UpdateClient deploy.
    /// </summary>
    public async Task<(int Queued, int Blocked, string Message)> QueueRestartAgentAsync(
        IReadOnlyList<string> hostnames,
        CancellationToken ct = default)
    {
        return await QueueMaintenanceCommandAsync(
            hostnames,
            RemoteMachineCommands.RestartAgent,
            phase: "Queued",
            detail: "Waiting for agent to pick up RestartAgent",
            ct);
    }

    /// <summary>
    /// Queue CleanupClientStaging for selected hosts. Refuses hosts with an in-flight UpdateClient deploy.
    /// </summary>
    public async Task<(int Queued, int Blocked, string Message)> QueueCleanupStagingAsync(
        IReadOnlyList<string> hostnames,
        CancellationToken ct = default)
    {
        return await QueueMaintenanceCommandAsync(
            hostnames,
            RemoteMachineCommands.CleanupClientStaging,
            phase: "Queued",
            detail: "Waiting for agent to pick up CleanupClientStaging",
            ct);
    }

    /// <summary>
    /// Queue DepositClientPack: agent downloads the Ready pack to
    /// C:\Temp\Heimdall-Client-v{version}-{yyyyMMdd-HHmmss} for manual Install.lnk
    /// (does not replace the running agent). Requires pack Ready; skips hosts with
    /// UpdateClient or DepositClientPack already pending; unknown hosts count as errors.
    /// </summary>
    public async Task<DepositClientPackResponseDto> QueueDepositClientPackAsync(
        IReadOnlyList<string> hostnames,
        CancellationToken ct = default)
    {
        var results = new List<DepositClientPackHostResultDto>();
        var status = packReadiness.GetStatus();
        if (!status.DeployUnlocked || status.Status != ClientPackStatus.Ready)
        {
            var notReady = status.Message ?? "Client pack is not ready — Pack first.";
            foreach (var raw in hostnames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var hostname = raw.Trim();
                if (hostname.Length == 0)
                    continue;
                results.Add(new DepositClientPackHostResultDto
                {
                    Hostname = hostname,
                    Outcome = "error",
                    Detail = notReady
                });
            }

            return new DepositClientPackResponseDto
            {
                Queued = 0,
                Skipped = 0,
                Errors = results.Count,
                Message = notReady,
                Results = results
            };
        }

        var version = status.PackProductVersion ?? PublishedVersionService.DefaultVersion;
        var depositExample = ClientPackFolderNames.BuildDepositFolderName(version);
        var queued = 0;
        var skipped = 0;
        var errors = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var raw in hostnames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hostname = raw.Trim();
            if (hostname.Length == 0)
                continue;

            var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
            if (machine is null)
            {
                errors++;
                results.Add(new DepositClientPackHostResultDto
                {
                    Hostname = hostname,
                    Outcome = "error",
                    Detail = "Unknown hostname (no agent enrolled)"
                });
                logger.LogWarning("DepositClientPack: unknown hostname {Host}", hostname);
                continue;
            }

            if (HasPendingUpdateClient(machine))
            {
                skipped++;
                results.Add(new DepositClientPackHostResultDto
                {
                    Hostname = hostname,
                    Outcome = "skipped",
                    Detail = "UpdateClient deploy pending"
                });
                logger.LogInformation(
                    "DepositClientPack: skipped {Host} — UpdateClient deploy pending", hostname);
                continue;
            }

            var pending = RemoteMachineService.DeserializeCommands(machine.PendingCommandsJson);
            if (pending.Contains(RemoteMachineCommands.DepositClientPack, StringComparer.OrdinalIgnoreCase))
            {
                skipped++;
                results.Add(new DepositClientPackHostResultDto
                {
                    Hostname = hostname,
                    Outcome = "skipped",
                    Detail = "DepositClientPack already pending"
                });
                continue;
            }

            pending.Add(RemoteMachineCommands.DepositClientPack);
            machine.PendingCommandsJson = JsonSerializer.Serialize(pending, JsonOptions);
            machine.PendingClientDepositJson = JsonSerializer.Serialize(new ClientDepositRequestDto
            {
                Version = version,
                DownloadPath = "/api/agent/client-pack",
                QueuedUtc = now
            }, JsonOptions);
            machine.ClientUpdateProgressJson = JsonSerializer.Serialize(new ClientUpdateProgressDto
            {
                Phase = ClientUpdatePhases.Queued,
                Detail = $"DepositClientPack: Waiting for agent to download pack to C:\\Temp\\{depositExample}",
                TargetVersion = version,
                UpdatedUtc = now
            }, JsonOptions);
            queued++;
            results.Add(new DepositClientPackHostResultDto
            {
                Hostname = hostname,
                Outcome = "queued",
                Detail = $"Waiting for agent pickup → C:\\Temp\\{depositExample}"
            });
        }

        await db.SaveChangesAsync(ct);

        string message;
        if (queued == 0 && skipped == 0 && errors == 0)
            message = "No machines queued.";
        else if (queued == 0 && skipped > 0 && errors == 0)
            message = $"{skipped} machine(s) skipped (Deploy/Deposit already pending).";
        else if (queued == 0 && errors > 0 && skipped == 0)
            message = $"{errors} machine(s) could not be queued.";
        else if (skipped == 0 && errors == 0)
            message = $"Queued DepositClientPack for {queued} machine(s). Agents pull pack to C:\\Temp\\Heimdall-Client-v{ClientPackFolderNames.SanitizeVersion(version)}-{{yyyyMMdd-HHmmss}}.";
        else
            message = $"Queued {queued}; skipped {skipped}; errors {errors}.";

        OpsFileLog.Write(
            "DepositClientPack",
            $"queued={queued}; skipped={skipped}; errors={errors}; version={version}");

        return new DepositClientPackResponseDto
        {
            Queued = queued,
            Skipped = skipped,
            Errors = errors,
            Message = message,
            Results = results
        };
    }

    private async Task<(int Queued, int Blocked, string Message)> QueueMaintenanceCommandAsync(
        IReadOnlyList<string> hostnames,
        string command,
        string phase,
        string detail,
        CancellationToken ct)
    {
        var queued = 0;
        var blocked = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var raw in hostnames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hostname = raw.Trim();
            if (hostname.Length == 0)
                continue;

            var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
            if (machine is null)
            {
                logger.LogWarning("{Command}: unknown hostname {Host}", command, hostname);
                continue;
            }

            if (HasPendingUpdateClient(machine))
            {
                blocked++;
                logger.LogInformation(
                    "{Command}: blocked on {Host} — UpdateClient deploy pending",
                    command, hostname);
                continue;
            }

            var pending = RemoteMachineService.DeserializeCommands(machine.PendingCommandsJson);
            if (!pending.Contains(command, StringComparer.OrdinalIgnoreCase))
                pending.Add(command);
            machine.PendingCommandsJson = JsonSerializer.Serialize(pending, JsonOptions);
            machine.ClientUpdateProgressJson = JsonSerializer.Serialize(new ClientUpdateProgressDto
            {
                Phase = phase,
                Detail = $"{command}: {detail}",
                UpdatedUtc = now
            }, JsonOptions);
            queued++;
        }

        await db.SaveChangesAsync(ct);

        if (queued == 0 && blocked == 0)
            return (0, 0, "No machines queued.");
        if (queued == 0 && blocked > 0)
        {
            OpsFileLog.Write(command, $"queued=0; blocked={blocked}");
            return (0, blocked, $"{blocked} machine(s) have Deploy/UpdateClient pending — finish or clear Deploy first.");
        }

        if (blocked > 0)
        {
            OpsFileLog.Write(command, $"queued={queued}; blocked={blocked}");
            return (queued, blocked, $"Queued {command} for {queued} machine(s); {blocked} blocked (Deploy pending).");
        }

        OpsFileLog.Write(command, $"queued={queued}");
        return (queued, 0, $"Queued {command} for {queued} machine(s).");
    }

    private static bool HasPendingUpdateClient(Machine machine)
    {
        if (!string.IsNullOrWhiteSpace(machine.PendingClientUpdateJson))
            return true;
        var pending = RemoteMachineService.DeserializeCommands(machine.PendingCommandsJson);
        return pending.Contains(RemoteMachineCommands.UpdateClient, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mark machines stuck in Applying when heartbeats stop (or progress goes stale) for
    /// <paramref name="stuckAfterMinutes"/>. Clears pending UpdateClient so operators can re-queue.
    /// </summary>
    public async Task<int> MarkStuckApplyingAsync(int stuckAfterMinutes = DefaultApplyingStuckMinutes, CancellationToken ct = default)
    {
        if (stuckAfterMinutes < 1)
            stuckAfterMinutes = DefaultApplyingStuckMinutes;

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-stuckAfterMinutes);
        var machines = await db.Machines
            .Where(m => m.ClientUpdateProgressJson != null)
            .ToListAsync(ct);

        var marked = 0;
        foreach (var machine in machines)
        {
            var progress = DeserializeProgress(machine.ClientUpdateProgressJson);
            if (progress is null)
                continue;
            if (!string.Equals(progress.Phase, ClientUpdatePhases.Applying, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(progress.Phase, ClientUpdatePhases.Downloading, StringComparison.OrdinalIgnoreCase))
                continue;

            // Prefer LastSeenUtc (no heartbeat). Also treat stale progress UpdatedUtc as stuck when
            // the agent is still heartbeating an old version after a failed apply.
            var noHeartbeat = machine.LastSeenUtc < cutoff;
            var progressStale = progress.UpdatedUtc < cutoff;
            if (!noHeartbeat && !progressStale)
                continue;

            var target = DeserializeRequest(machine.PendingClientUpdateJson)?.Version ?? progress.TargetVersion;
            if (!noHeartbeat
                && !string.IsNullOrWhiteSpace(machine.AgentVersion)
                && !string.IsNullOrWhiteSpace(target)
                && VersionCompare.CoreVersionsMatch(machine.AgentVersion, target))
            {
                // Heartbeating at target — let ApplyHeartbeatAsync promote to Succeeded on next ingest.
                continue;
            }

            var reason = noHeartbeat
                ? $"Stuck: {progress.Phase} with no heartbeat for {stuckAfterMinutes}+ minutes (last seen {machine.LastSeenUtc:u})"
                : $"Stuck: {progress.Phase} for {stuckAfterMinutes}+ minutes without reaching target version {target ?? "(unknown)"} (still reporting {machine.AgentVersion ?? "unknown"})";

            ClearPendingUpdateClient(machine);
            machine.PendingClientUpdateJson = null;
            machine.ClientUpdateProgressJson = JsonSerializer.Serialize(new ClientUpdateProgressDto
            {
                Phase = ClientUpdatePhases.Stuck,
                Detail = reason,
                TargetVersion = target,
                UpdatedUtc = now
            }, JsonOptions);
            marked++;
            logger.LogWarning("Client update Stuck: {Host} — {Reason}", machine.Hostname, reason);
        }

        if (marked > 0)
            await db.SaveChangesAsync(ct);

        return marked;
    }
}

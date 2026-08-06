using System.Text.Json;
using Heimdall.Api.Data;
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
            return (0, $"{needsBootstrap} machine(s) need one-time bootstrap (Launch Control / Install.lnk) — agent does not support UpdateClient.");
        if (needsBootstrap > 0)
            return (queued, $"Queued silent update for {queued} machine(s); {needsBootstrap} need bootstrap install first.");
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

    public async Task ApplyHeartbeatAsync(Machine machine, HeartbeatDto heartbeat, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var target = DeserializeRequest(machine.PendingClientUpdateJson);

        // Progress from command reports
        foreach (var report in heartbeat.CommandExecutionReports)
        {
            if (!string.Equals(report.Command, RemoteMachineCommands.UpdateClient, StringComparison.OrdinalIgnoreCase))
                continue;

            var phase = ClientUpdatePhases.Applying;
            var detail = report.Detail;
            if (!report.Success)
            {
                if (detail is not null && detail.Contains("DeferredWaitingForIdle", StringComparison.OrdinalIgnoreCase))
                    phase = ClientUpdatePhases.DeferredWaitingForIdle;
                else if (detail is not null && detail.Contains("Downloading", StringComparison.OrdinalIgnoreCase))
                    phase = ClientUpdatePhases.Downloading;
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
            else if (detail is not null && detail.Contains("Downloading", StringComparison.OrdinalIgnoreCase))
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
}

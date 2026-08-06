namespace Heimdall.Shared.Contracts;

/// <summary>Queued silent client update payload delivered via AgentConfigDto.PendingClientUpdate.</summary>
public sealed class ClientUpdateRequestDto
{
    /// <summary>Expected simple client version after update (e.g. 3+). Silent UpdateClient requires agent ≥ 3.</summary>
    public required string Version { get; init; }

    /// <summary>SHA256 (hex, lowercase) of the zip bytes served at DownloadPath.</summary>
    public required string ZipSha256 { get; init; }

    /// <summary>Relative API path, e.g. /api/agent/client-pack</summary>
    public string DownloadPath { get; init; } = "/api/agent/client-pack";

    public DateTimeOffset QueuedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Client update workflow phases stored on Machine.ClientUpdateProgressJson.</summary>
public static class ClientUpdatePhases
{
    public const string Queued = "Queued";
    public const string Downloading = "Downloading";
    public const string DeferredWaitingForIdle = "DeferredWaitingForIdle";
    public const string Applying = "Applying";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

public sealed class ClientUpdateProgressDto
{
    public string Phase { get; init; } = ClientUpdatePhases.Queued;
    public string? Detail { get; init; }
    public string? TargetVersion { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

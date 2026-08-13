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
    /// <summary>Applying timed out (no heartbeat / no version bump) — operator visibility.</summary>
    public const string Stuck = "Stuck";
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

/// <summary>
/// Queued pack deposit payload (version for Temp folder naming), delivered via
/// <see cref="AgentConfigDto.PendingClientDeposit"/> alongside the DepositClientPack command token.
/// </summary>
public sealed class ClientDepositRequestDto
{
    /// <summary>Pack productVersion from the Ready pack (e.g. simple integer "4").</summary>
    public required string Version { get; init; }

    /// <summary>Relative API path, e.g. /api/agent/client-pack</summary>
    public string DownloadPath { get; init; } = "/api/agent/client-pack";

    public DateTimeOffset QueuedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Admin request to queue DepositClientPack on agents (API pull to C:\Temp\Heimdall-Client-v{version}-{stamp}).</summary>
public sealed class DepositClientPackRequestDto
{
    public List<string> Hostnames { get; init; } = [];
}

/// <summary>Per-host outcome from a DepositClientPack queue attempt.</summary>
public sealed class DepositClientPackHostResultDto
{
    public required string Hostname { get; init; }

    /// <summary>queued | skipped | error</summary>
    public required string Outcome { get; init; }

    public string? Detail { get; init; }
}

/// <summary>Summary returned by POST /api/admin/client-pack/deposit.</summary>
public sealed class DepositClientPackResponseDto
{
    public int Queued { get; init; }
    public int Skipped { get; init; }
    public int Errors { get; init; }
    public required string Message { get; init; }
    public List<DepositClientPackHostResultDto> Results { get; init; } = [];
}

namespace Heimdall.Shared.Contracts;

/// <summary>Agent command tokens delivered via config PendingCommands and cleared on heartbeat ack.</summary>
public static class RemoteMachineCommands
{
    public const string RestartTermService = "RestartTermService";

    /// <summary>
    /// Zero-payload graceful-stop token for the TUFLOW run the agent is currently tracking.
    /// See Heimdall.Agent.Collectors.TuflowRunHelper.TryExecuteCommand and TuflowLauncher's
    /// stop.request-file / CTRL_BREAK_EVENT mechanism.
    /// </summary>
    public const string TuflowStopGraceful = "TuflowStopGraceful";

    /// <summary>
    /// Silent client self-update: agent downloads the published pack from the API and replaces
    /// HeimdallAgent files (service restart only — never logoff/reboot). Payload is
    /// AgentConfigDto.PendingClientUpdate.
    /// </summary>
    public const string UpdateClient = "UpdateClient";

    /// <summary>
    /// Restart the HeimdallAgent Windows service (detached sc stop/start — not TermService/RDS).
    /// </summary>
    public const string RestartAgent = "RestartAgent";

    /// <summary>
    /// Delete staging deposits only: %ProgramData%\Heimdall\update\ and C:\Temp\Heimdall-Client*
    /// (legacy bare folder plus versioned Heimdall-Client-v* / timestamped deposits).
    /// Never touches Program Files Agent, queue, secrets, logs, or tuflow-runs.
    /// </summary>
    public const string CleanupClientStaging = "CleanupClientStaging";

    /// <summary>
    /// Download the current API client pack zip and extract to
    /// C:\Temp\Heimdall-Client-v{version}-{yyyyMMdd-HHmmss} for manual Install.lnk —
    /// does not replace the running agent. Version comes from PendingClientDeposit / pack VERSION.json.
    /// </summary>
    public const string DepositClientPack = "DepositClientPack";
}

/// <summary>Result of a single agent command execution attempt (success or failure).</summary>
public sealed class CommandExecutionReportDto
{
    public required string Command { get; init; }

    public bool Success { get; init; }

    public string? Detail { get; init; }

    public DateTimeOffset ExecutedUtc { get; init; }
}

/// <summary>Restart RDS workflow phases stored on Machine.RestartRdsProgressJson.</summary>
public static class RestartRdsPhases
{
    public const string Queued = "Queued";
    public const string Retrying = "Retrying";
    public const string Acknowledged = "Acknowledged";
    public const string Verifying = "Verifying";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

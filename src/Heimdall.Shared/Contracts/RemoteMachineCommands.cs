namespace Heimdall.Shared.Contracts;

/// <summary>Agent command tokens delivered via config PendingCommands and cleared on heartbeat ack.</summary>
public static class RemoteMachineCommands
{
    public const string RestartTermService = "RestartTermService";
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

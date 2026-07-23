namespace Heimdall.Shared.Contracts;

public sealed class SessionEventDto
{
    public required string EventId { get; init; }
    public required string Hostname { get; init; }
    public int SessionId { get; init; }
    public required string Username { get; init; }
    public string? Domain { get; init; }
    public SessionType SessionType { get; init; }
    public SessionState State { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public string? ClientName { get; init; }
    public string? ClientAddress { get; init; }
    public long ActiveSeconds { get; init; }
    public long DisconnectedSeconds { get; init; }
}

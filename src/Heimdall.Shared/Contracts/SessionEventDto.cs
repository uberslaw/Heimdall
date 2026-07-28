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
    /// <summary>Total active seconds (Local + Inbound RDP buckets).</summary>
    public long ActiveSeconds { get; init; }
    /// <summary>Total disconnected seconds (Local + Inbound RDP buckets).</summary>
    public long DisconnectedSeconds { get; init; }
    /// <summary>Active seconds accumulated while classified Local.</summary>
    public long LocalActiveSeconds { get; init; }
    /// <summary>Disconnected seconds accumulated while classified Local.</summary>
    public long LocalDisconnectedSeconds { get; init; }
    /// <summary>Active seconds accumulated while classified inbound RDP.</summary>
    public long InboundRdpActiveSeconds { get; init; }
    /// <summary>Disconnected seconds accumulated while classified inbound RDP.</summary>
    public long InboundRdpDisconnectedSeconds { get; init; }
}

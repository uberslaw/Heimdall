namespace Heimdall.Shared.Contracts;

/// <summary>Body for POST /api/admin/published-version — sets the "current published" client pack version.</summary>
public sealed class PublishedVersionDto
{
    public required string Version { get; init; }

    /// <summary>Optional label for who/what set it (e.g. "Launch Control @ COMPUTERNAME").</summary>
    public string? SetBy { get; init; }
}

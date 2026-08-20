namespace Heimdall.Api.Services;

/// <summary>
/// Catalog of named email allowlists. Add an entry here when wiring a new page gate;
/// store emails via <see cref="AccessAllowlistService"/> (DB override) or appsettings seed.
/// </summary>
public static class AccessAllowlistCatalog
{
    public const string Admin = "admin";
    public const string FloodFull = "floodFull";
    public const string FloodLive = "floodLive";

    public static IReadOnlyList<AccessAllowlistDefinition> All { get; } =
    [
        new(
            Admin,
            "Site admins",
            "Not a global superuser. Gates Admin-only tools (Site usage, software tags, Remote Access Groups preview, this page). Also unlocks Full Flood automatically. Edit via appsettings only — this panel is read-only.",
            ConfigPath: "Heimdall:StaffAccess:AdminEmails",
            Editable: false),
        new(
            FloodFull,
            "Full Flood",
            "Full Flood hub for non-admin teammates: Live, Historical, Enrollment, Run Queue, Fleet Sims, Run behaviour, Flood machine detail, TUFLOW Runs, and the Machine TUFLOW panel. Site admins are included automatically (see panel above) — add only additional team emails here. Same admin editors as Flood Live.",
            ConfigPath: "Heimdall:FloodTeamEmails",
            Editable: true),
        new(
            FloodLive,
            "Flood Live only",
            "Flood → Live tab and the shared Live SSE stream only. Does not unlock Historical, Enrollment, Run Queue, Sims, Run behaviour, or TUFLOW Runs. Full Flood (and site admins) already include Live.",
            ConfigPath: "Heimdall:FloodLiveEmails",
            Editable: true)
    ];

    public static AccessAllowlistDefinition? TryGet(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string FlagKey(string id) => $"Access.Emails.{id}";
}

public sealed record AccessAllowlistDefinition(
    string Id,
    string Title,
    string GrantsDescription,
    string ConfigPath,
    bool Editable);

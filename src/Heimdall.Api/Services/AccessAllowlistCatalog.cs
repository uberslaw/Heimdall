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
            "Admin-gated actions that already use StaffAccess:AdminEmails — Site usage, software tags, Remote Access Groups preview, and this Access lists page. Edit via appsettings (not the UI).",
            ConfigPath: "Heimdall:StaffAccess:AdminEmails",
            Editable: false),
        new(
            FloodFull,
            "Full Flood",
            "Full Flood hub access: Live, Historical, Enrollment, Run Queue, Fleet Sims, Run behaviour, Flood machine detail, TUFLOW Runs, and the Machine TUFLOW panel. Site admins are always included automatically.",
            ConfigPath: "Heimdall:FloodTeamEmails",
            Editable: true),
        new(
            FloodLive,
            "Flood Live only",
            "Flood → Live tab and the shared Live SSE stream only. Does not unlock Historical, Enrollment, Run Queue, Sims, Run behaviour, or TUFLOW Runs. Full Flood emails already include Live.",
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

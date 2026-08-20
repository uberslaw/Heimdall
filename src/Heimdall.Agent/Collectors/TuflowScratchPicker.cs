using Heimdall.Shared;
using Heimdall.Shared.Contracts;

namespace Heimdall.Agent.Collectors;

/// <summary>
/// Picks a fixed local volume for TUFLOW scratch (prefer non-C by free space).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class TuflowScratchPicker
{
    public sealed record ScratchChoice(string Drive, string FolderPath, double FreeGb);

    public static ScratchChoice? TryPick(
        string runId,
        double minFreeGb,
        bool allowScratchOnC)
    {
        var volumes = HardwareInventoryCollector.TryCollectVolumes();
        if (volumes.Count == 0)
            return null;

        var eligible = volumes
            .Where(v =>
            {
                var letter = DriveLetter(v.Name);
                if (letter is null) return false;
                if (!TuflowLaunchPath.AllowedLocalDriveLetters.Contains(letter.Value))
                    return false;
                if (letter == 'C' && !allowScratchOnC)
                    return false;
                return v.FreeGb + 0.001 >= minFreeGb;
            })
            .OrderByDescending(v => DriveLetter(v.Name) == 'C' ? 0 : 1)
            .ThenByDescending(v => v.FreeGb)
            .ToList();

        // Fallback: allow C if nothing else and policy forbids C but we have no choice — only when allowScratchOnC.
        if (eligible.Count == 0 && allowScratchOnC)
        {
            eligible = volumes
                .Where(v =>
                {
                    var letter = DriveLetter(v.Name);
                    return letter is not null
                           && TuflowLaunchPath.AllowedLocalDriveLetters.Contains(letter.Value)
                           && v.FreeGb + 0.001 >= Math.Min(minFreeGb, 10);
                })
                .OrderByDescending(v => v.FreeGb)
                .ToList();
        }

        // Last resort: best non-C even below min, then C if allowed.
        if (eligible.Count == 0)
        {
            eligible = volumes
                .Where(v =>
                {
                    var letter = DriveLetter(v.Name);
                    if (letter is null) return false;
                    if (!TuflowLaunchPath.AllowedLocalDriveLetters.Contains(letter.Value)) return false;
                    if (letter == 'C' && !allowScratchOnC) return false;
                    return true;
                })
                .OrderByDescending(v => DriveLetter(v.Name) == 'C' ? 0 : 1)
                .ThenByDescending(v => v.FreeGb)
                .ToList();
        }

        var pick = eligible.FirstOrDefault();
        if (pick is null)
            return null;

        var drive = pick.Name.TrimEnd('\\');
        if (!drive.EndsWith(':'))
            drive += ":";
        var folder = Path.Combine(drive + "\\", "Heimdall", "tuflow-scratch", runId);
        Directory.CreateDirectory(folder);
        return new ScratchChoice(drive, folder, pick.FreeGb);
    }

    private static char? DriveLetter(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var c = char.ToUpperInvariant(name.Trim()[0]);
        return char.IsAsciiLetter(c) ? c : null;
    }
}

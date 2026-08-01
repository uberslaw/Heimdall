using Heimdall.Shared.Contracts;

namespace Heimdall.Api.Services;

/// <summary>
/// Windows / SOE / config process names to hide from user-facing app activity views.
/// Delegates core Windows detection to <see cref="WindowsCoreCatalog"/> and
/// <see cref="ProcessClassification"/>.
/// </summary>
public static class ProcessNoiseFilter
{
    public static IReadOnlyCollection<string> WindowsNoise => WindowsCoreCatalog.Names;

    public static bool IsWindowsNoise(string processName) =>
        ProcessClassification.Classify(processName).Group == AppGroup.CoreWindows;

    public static HashSet<string> BuildExcludeSet(
        IEnumerable<string>? configExcludes = null,
        IEnumerable<string>? soeProcessNames = null)
    {
        var set = new HashSet<string>(WindowsCoreCatalog.Names, StringComparer.OrdinalIgnoreCase);
        if (soeProcessNames is not null)
        {
            foreach (var name in soeProcessNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    set.Add(name.Trim());
            }
        }

        if (configExcludes is not null)
        {
            foreach (var name in configExcludes)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    set.Add(name.Trim());
            }
        }

        return set;
    }

    public static bool IsExcluded(string processName, HashSet<string> excludeSet) =>
        !string.IsNullOrWhiteSpace(processName) && excludeSet.Contains(processName.Trim());
}

using Heimdall.Shared.Contracts;

namespace Heimdall.Api.Services;

public sealed record ProcessClassificationResult(
    AppGroup Group,
    bool ExcludedFromDefaultTracking,
    bool AllowForPresence);

/// <summary>Classifies processes into Core Windows / SOE / Specialization and default tracking policy.</summary>
public static class ProcessClassification
{
    public static ProcessClassificationResult Classify(
        string? processName,
        IReadOnlySet<string>? soeProcessNames = null)
    {
        var name = processName?.Trim() ?? "";
        if (name.Length == 0)
            return new ProcessClassificationResult(AppGroup.Specialization, false, false);

        if (WindowsCoreCatalog.IsCoreWindows(name))
        {
            var allowPresence = WindowsCoreCatalog.AllowForPresence(name);
            return new ProcessClassificationResult(AppGroup.CoreWindows, !allowPresence, allowPresence);
        }

        if (soeProcessNames?.Contains(name) == true || SoeCatalog.Contains(name))
            return new ProcessClassificationResult(AppGroup.Soe, true, false);

        return new ProcessClassificationResult(AppGroup.Specialization, false, false);
    }

    public static bool IsProposableForTracking(
        string? processName,
        IReadOnlySet<string>? soeProcessNames = null) =>
        Classify(processName, soeProcessNames).Group == AppGroup.Specialization;

    public static string GroupLabel(AppGroup group) => group switch
    {
        AppGroup.CoreWindows => "Core Windows",
        AppGroup.Soe => "SOE",
        _ => "Specialization"
    };

    public static int GroupSortOrder(AppGroup group) => group switch
    {
        AppGroup.CoreWindows => 0,
        AppGroup.Soe => 1,
        _ => 2
    };
}

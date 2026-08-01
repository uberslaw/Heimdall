using Heimdall.Shared.Contracts;

namespace Heimdall.Api.Services;

public sealed record ProcessClassificationResult(
    AppGroup Group,
    bool ExcludedFromDefaultTracking,
    bool AllowForPresence);

/// <summary>Runtime inputs for classifying a process (DB overrides + SOE membership).</summary>
public sealed class ProcessClassificationContext
{
    public IReadOnlyDictionary<string, AppGroup> UserAssignments { get; init; } =
        new Dictionary<string, AppGroup>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> SoeProcessNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static ProcessClassificationContext Empty { get; } = new();
}

/// <summary>Classifies processes into Core Windows / SOE / Specialization and default tracking policy.</summary>
public static class ProcessClassification
{
    public static ProcessClassificationResult Classify(
        string? processName,
        ProcessClassificationContext? context = null)
    {
        var name = processName?.Trim() ?? "";
        if (name.Length == 0)
            return new ProcessClassificationResult(AppGroup.Specialization, false, false);

        context ??= ProcessClassificationContext.Empty;

        if (context.UserAssignments.TryGetValue(name, out var userGroup))
            return ResultForGroup(userGroup, name);

        if (WindowsCoreCatalog.IsCoreWindows(name))
        {
            var allowPresence = WindowsCoreCatalog.AllowForPresence(name);
            return new ProcessClassificationResult(AppGroup.CoreWindows, !allowPresence, allowPresence);
        }

        if (context.SoeProcessNames.Contains(name) || SoeCatalog.Contains(name))
            return new ProcessClassificationResult(AppGroup.Soe, true, false);

        return new ProcessClassificationResult(AppGroup.Specialization, false, false);
    }

    public static ProcessClassificationResult Classify(
        string? processName,
        IReadOnlySet<string>? soeProcessNames) =>
        Classify(processName, new ProcessClassificationContext
        {
            SoeProcessNames = soeProcessNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        });

    public static bool IsProposableForTracking(
        string? processName,
        ProcessClassificationContext? context = null) =>
        Classify(processName, context).Group == AppGroup.Specialization;

    public static bool IsProposableForTracking(
        string? processName,
        IReadOnlySet<string>? soeProcessNames) =>
        IsProposableForTracking(processName, new ProcessClassificationContext
        {
            SoeProcessNames = soeProcessNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        });

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

    private static ProcessClassificationResult ResultForGroup(AppGroup group, string name) => group switch
    {
        AppGroup.CoreWindows => new ProcessClassificationResult(
            AppGroup.CoreWindows,
            !WindowsCoreCatalog.AllowForPresence(name),
            WindowsCoreCatalog.AllowForPresence(name)),
        AppGroup.Soe => new ProcessClassificationResult(AppGroup.Soe, true, false),
        _ => new ProcessClassificationResult(AppGroup.Specialization, false, false)
    };
}

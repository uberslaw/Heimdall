namespace Heimdall.Shared;

/// <summary>
/// Filters Win32_VideoController / hardware-inventory GPU name lists for Cost / Machine display.
/// Drops Microsoft remote/basic display stubs; when a discrete NVIDIA/AMD (or Intel Arc) GPU is
/// present, also drops Intel UHD/Iris/HD integrated adapters. Multiple real GPUs stay joined with "; ".
/// </summary>
public static class GpuInventoryFilter
{
    /// <summary>Normalize a "; "-joined GPU string (or return null when nothing remains).</summary>
    public static string? Normalize(string? joinedNames)
    {
        if (string.IsNullOrWhiteSpace(joinedNames))
            return null;

        var parts = joinedNames
            .Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static s => s.Length > 0);
        return Join(FilterNames(parts));
    }

    /// <summary>Filter raw adapter names; preserves order of first occurrence.</summary>
    public static IReadOnlyList<string> FilterNames(IEnumerable<string?> names)
    {
        var list = new List<string>();
        foreach (var raw in names)
        {
            var name = Clean(raw);
            if (name is null)
                continue;
            if (IsStubAdapter(name))
                continue;
            if (list.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            list.Add(name);
        }

        if (list.Count == 0)
            return list;

        if (list.Any(IsDiscreteOrVendorGpu))
            list = list.Where(n => !IsIntelIntegrated(n)).ToList();

        return list;
    }

    public static string? Join(IReadOnlyList<string> names) =>
        names.Count == 0 ? null : string.Join("; ", names);

    internal static bool IsStubAdapter(string name)
    {
        // Microsoft Basic Display / Remote Display / Remote Desktop stubs — not inventory GPUs.
        if (name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Contains("Remote Desktop", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Contains("Hyper-V Video", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Equals("Basic Render Driver", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    internal static bool IsDiscreteOrVendorGpu(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Quadro", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Tesla", StringComparison.OrdinalIgnoreCase))
            return true;

        // RTX / GTX as tokens (avoid matching unrelated acronyms mid-word).
        if (ContainsToken(name, "RTX") || ContainsToken(name, "GTX"))
            return true;

        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Instinct", StringComparison.OrdinalIgnoreCase))
            return true;

        // Intel Arc is discrete; do not treat as iGPU.
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    internal static bool IsIntelIntegrated(string name)
    {
        if (!name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
            return false;

        return name.Contains("UHD", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Iris", StringComparison.OrdinalIgnoreCase)
               || name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Graphics", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsToken(string name, string token)
    {
        var idx = name.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var beforeOk = idx == 0 || !char.IsLetterOrDigit(name[idx - 1]);
            var after = idx + token.Length;
            var afterOk = after >= name.Length || !char.IsLetterOrDigit(name[after]);
            if (beforeOk && afterOk)
                return true;
            idx = name.IndexOf(token, idx + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var t = value.Trim();
        return t.Length == 0 ? null : t;
    }
}

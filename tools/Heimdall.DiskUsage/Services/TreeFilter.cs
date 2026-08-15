using Heimdall.DiskUsage.Models;

namespace Heimdall.DiskUsage.Services;

public static class TreeFilter
{
    /// <summary>
    /// Rebuild VisibleChildren for the whole tree.
    /// A node is shown if it matches the size filter, or any descendant matches (so you can drill to hits).
    /// </summary>
    public static void Apply(FolderNode root, long? minBytes, long? maxBytes)
    {
        ApplyNode(root, minBytes, maxBytes);
    }

    static bool ApplyNode(FolderNode node, long? minBytes, long? maxBytes)
    {
        node.VisibleChildren.Clear();
        var anyChildVisible = false;

        foreach (var child in node.Children)
        {
            if (ApplyNode(child, minBytes, maxBytes))
            {
                node.VisibleChildren.Add(child);
                anyChildVisible = true;
            }
        }

        var selfMatch = Matches(node.SizeBytes, minBytes, maxBytes);
        // Root is always "visible" as the scan container; leaves/folders need match or visible kids.
        var show = node.Parent is null || selfMatch || anyChildVisible;
        node.IsVisible = show;
        return show;
    }

    static bool Matches(long size, long? minBytes, long? maxBytes)
    {
        if (minBytes is long min && size < min) return false;
        if (maxBytes is long max && size > max) return false;
        return true;
    }
}

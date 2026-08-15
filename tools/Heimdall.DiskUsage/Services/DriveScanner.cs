using System.IO;
using System.Runtime.Versioning;
using Heimdall.DiskUsage.Models;

namespace Heimdall.DiskUsage.Services;

/// <summary>
/// Full-tree folder size scan for a single local root.
/// Prefers NTFS MFT enumeration (fast); falls back to Directory.EnumerateFiles when MFT is unavailable.
/// When excludeSystem is on: skips Windows / Program Files / Program Files (x86) in the main walk,
/// but still measures Windows\ccmcache and shows it under the drive root.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriveScanner
{
    static readonly HashSet<string> SystemTopNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "Program Files",
        "Program Files (x86)",
    };

    public sealed record Progress(
        string Message,
        long BytesSeen,
        long FilesSeen,
        long FoldersSeen);

    public static FolderNode Scan(
        string rootPath,
        bool excludeSystemFolders,
        IProgress<Progress>? progress,
        CancellationToken ct)
    {
        // CRITICAL: "C:" means "current directory on C:", not the drive root.
        // Always use "C:\" (trailing slash) for filesystem APIs on drive roots.
        var rootFs = ToFileSystemPath(rootPath);
        if (!Directory.Exists(rootFs))
            throw new DirectoryNotFoundException($"Path not found: {rootFs}");

        if (NtfsMftScanner.TryScan(rootFs, excludeSystemFolders, progress, ct, out var mftRoot, out var mftReason)
            && mftRoot is not null)
        {
            return mftRoot;
        }

        progress?.Report(new Progress(
            $"MFT unavailable ({mftReason}). Using directory walk…",
            0, 0, 0));

        return ScanWithDirectoryWalk(rootFs, excludeSystemFolders, progress, ct);
    }

    static FolderNode ScanWithDirectoryWalk(
        string rootFs,
        bool excludeSystemFolders,
        IProgress<Progress>? progress,
        CancellationToken ct)
    {
        var rootKey = ToKey(rootFs);
        var rootDisplay = IsDriveRootKey(rootKey) ? rootKey + @"\" : rootKey;

        var root = new FolderNode
        {
            FullPath = rootKey,
            Name = rootDisplay,
            Parent = null
        };

        var nodeByPath = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase)
        {
            [rootKey] = root
        };

        long bytesSeen = 0;
        var filesSeen = 0;
        var foldersSeen = 1;
        var steps = 0;
        var lastReport = DateTime.UtcNow;

        void Report(string message, bool force = false)
        {
            if (progress is null) return;
            var now = DateTime.UtcNow;
            if (!force && (now - lastReport).TotalMilliseconds < 400) return;
            lastReport = now;
            progress.Report(new Progress(message, bytesSeen, filesSeen, foldersSeen));
        }

        FolderNode GetOrCreate(string dirPath)
        {
            var key = ToKey(dirPath);
            if (nodeByPath.TryGetValue(key, out var existing))
                return existing;

            FolderNode? parent = null;
            if (!IsDriveRootKey(key))
            {
                var parentPath = Path.GetDirectoryName(ToFileSystemPath(key));
                if (!string.IsNullOrEmpty(parentPath))
                    parent = GetOrCreate(parentPath);
            }

            var name = IsDriveRootKey(key)
                ? key + @"\"
                : Path.GetFileName(key);
            if (string.IsNullOrEmpty(name))
                name = key;

            var node = new FolderNode
            {
                FullPath = key,
                Name = name,
                Parent = parent
            };
            nodeByPath[key] = node;
            parent?.Children.Add(node);
            foldersSeen++;
            return node;
        }

        void AddFileSize(string filePath, long size)
        {
            filesSeen++;
            bytesSeen += size;
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir)) return;

            var node = GetOrCreate(dir);
            node.OwnFilesBytes += size;
            node.FileCount++;

            for (var n = node; n is not null; n = n.Parent)
                n.SizeBytes += size;
        }

        void Walk(string startDirFs, bool skipSystemChildrenOfRoot)
        {
            var stack = new Stack<string>();
            stack.Push(startDirFs);
            GetOrCreate(startDirFs);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var currentFs = ToFileSystemPath(stack.Pop());
                var currentKey = ToKey(currentFs);

                if (++steps % 256 == 0)
                    Report($"Scanning… {currentFs}");

                foreach (var file in SafeEnumerateFiles(currentFs))
                {
                    ct.ThrowIfCancellationRequested();
                    long size;
                    try { size = new FileInfo(file).Length; }
                    catch { continue; }
                    AddFileSize(file, size);
                }

                foreach (var sub in SafeEnumerateDirectories(currentFs))
                {
                    if (skipSystemChildrenOfRoot
                        && string.Equals(currentKey, rootKey, StringComparison.OrdinalIgnoreCase)
                        && IsSystemTopName(sub))
                    {
                        continue;
                    }

                    GetOrCreate(sub);
                    stack.Push(sub);
                }
            }
        }

        Report($"Scanning {root.Name} (directory walk)…", force: true);
        Walk(rootFs, skipSystemChildrenOfRoot: excludeSystemFolders);

        if (excludeSystemFolders)
        {
            var ccmcachePath = Path.Combine(rootFs, "Windows", "ccmcache");
            if (Directory.Exists(ccmcachePath))
            {
                Report("Scanning Windows\\ccmcache…", force: true);

                var ccmKey = ToKey(ccmcachePath);
                var ccmRoot = new FolderNode
                {
                    FullPath = ccmKey,
                    Name = @"Windows\ccmcache",
                    Parent = root
                };
                root.Children.Add(ccmRoot);
                nodeByPath[ccmKey] = ccmRoot;

                var stack = new Stack<(string Path, FolderNode Parent)>();
                stack.Push((ccmcachePath, ccmRoot));

                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var (current, parentNode) = stack.Pop();
                    var currentFs = ToFileSystemPath(current);
                    if (++steps % 256 == 0)
                        Report($"Scanning ccmcache… {currentFs}");

                    foreach (var file in SafeEnumerateFiles(currentFs))
                    {
                        long size;
                        try { size = new FileInfo(file).Length; }
                        catch { continue; }

                        filesSeen++;
                        bytesSeen += size;
                        parentNode.OwnFilesBytes += size;
                        parentNode.FileCount++;
                        for (var n = parentNode; n is not null; n = n.Parent)
                            n.SizeBytes += size;
                    }

                    foreach (var sub in SafeEnumerateDirectories(currentFs))
                    {
                        var child = new FolderNode
                        {
                            FullPath = ToKey(sub),
                            Name = Path.GetFileName(sub.TrimEnd('\\')),
                            Parent = parentNode
                        };
                        parentNode.Children.Add(child);
                        nodeByPath[child.FullPath] = child;
                        foldersSeen++;
                        stack.Push((sub, child));
                    }
                }
            }
        }

        SortChildrenRecursive(root);
        Report($"Done (walk) — {FolderNode.FormatSize(root.SizeBytes)}", force: true);
        return root;
    }

    public static IReadOnlyList<DriveInfo> GetLocalDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Path form safe for Directory/File APIs. Drive roots must be "C:\" not "C:".
    /// </summary>
    public static string ToFileSystemPath(string path)
    {
        var p = (path ?? "").Trim();
        if (p.Length == 0)
            throw new ArgumentException("Path is empty.", nameof(path));

        // "C:" alone is current-dir-on-drive — force root.
        if (p.Length == 2 && p[1] == ':')
            p += @"\";

        var full = Path.GetFullPath(p);
        if (full.Length == 2 && full[1] == ':')
            full += @"\";

        // If GetFullPath("C:") resolved to a cwd under C:, caller may have passed "C:" —
        // we already appended slash before GetFullPath. If they passed "C:\" we're good.
        // Extra guard: if result looks like a drive root with slash, keep it.
        if (full.Length >= 3 && full[1] == ':' && full[2] == '\\' && full.TrimEnd('\\').Length == 2)
            return full.Length == 3 ? full : full[..3]; // "C:\"

        return full;
    }

    /// <summary>Stable dictionary key: drive root as "C:", other paths without trailing slash.</summary>
    public static string ToKey(string path)
    {
        var fs = ToFileSystemPath(path).TrimEnd('\\');
        return fs;
    }

    static bool IsDriveRootKey(string key) =>
        key.Length == 2 && key[1] == ':';

    static void SortChildrenRecursive(FolderNode node)
    {
        node.Children.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        foreach (var child in node.Children)
            SortChildrenRecursive(child);
    }

    static bool IsSystemTopName(string path)
    {
        try
        {
            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            return !string.IsNullOrWhiteSpace(name) && SystemTopNames.Contains(name);
        }
        catch
        {
            return false;
        }
    }

    static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        IEnumerable<string> raw;
        try { raw = Directory.EnumerateDirectories(ToFileSystemPath(path)); }
        catch { yield break; }

        foreach (var d in raw)
        {
            if (IsReparsePoint(d)) continue;
            yield return d;
        }
    }

    static IEnumerable<string> SafeEnumerateFiles(string path)
    {
        IEnumerable<string> raw;
        try { raw = Directory.EnumerateFiles(ToFileSystemPath(path)); }
        catch { yield break; }

        foreach (var f in raw)
            yield return f;
    }

    static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }
}

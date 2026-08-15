using System.IO;
using Heimdall.DiskUsage.Services;

var sw = System.Diagnostics.Stopwatch.StartNew();
var ok = NtfsMftScanner.TryScan(@"C:\", true,
    new Progress<DriveScanner.Progress>(p => Console.WriteLine(p.Message)),
    CancellationToken.None, out var root, out var reason);
sw.Stop();
Console.WriteLine($"ok={ok} reason={reason} elapsed={sw.Elapsed}");
if (root is not null)
{
    Console.WriteLine($"children={root.Children.Count} size={Heimdall.DiskUsage.Models.FolderNode.FormatSize(root.SizeBytes)}");
    foreach (var c in root.Children.Take(15))
        Console.WriteLine($"  {c.Name} [{Heimdall.DiskUsage.Models.FolderNode.FormatSize(c.SizeBytes)}]");
}

using System.IO.Compression;
using System.Text.Json;
using Heimdall.Agent.Services;
using Heimdall.Shared;
using Heimdall.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Heimdall.Agent.Collectors;

/// <summary>
/// Downloads the current API client pack into
/// C:\Temp\Heimdall-Client-v{version}-{timestamp} for manual install.
/// Does not stop/replace HeimdallAgent.
/// </summary>
internal static class ClientPackDepositHelper
{
    private static readonly object Gate = new();
    private static bool _inFlight;

    public static bool IsDepositInFlight
    {
        get { lock (Gate) return _inFlight; }
    }

    /// <summary>
    /// Returns Ack=true when deposit finished (success or hard fail worth clearing).
    /// Ack=false keeps the command pending (e.g. UpdateClient in flight).
    /// </summary>
    public static async Task<(bool Ack, bool Success, string Detail)> TryDepositAsync(
        HeimdallApiClient api,
        ILogger logger,
        CancellationToken ct,
        ClientDepositRequestDto? depositRequest = null)
    {
        if (ClientUpdateHelper.IsUpdateInFlight)
            return (false, false, "Skipped: UpdateClient is in progress — will retry after deploy");

        lock (Gate)
        {
            if (_inFlight)
                return (false, true, "Deposit already in progress");
            _inFlight = true;
        }

        try
        {
            var stamp = DateTime.Now;
            var stampText = stamp.ToString("yyyyMMdd-HHmmss");
            var tempRoot = Path.Combine(
                Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\",
                "Temp");
            Directory.CreateDirectory(tempRoot);

            var stagingRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Heimdall", "update");
            Directory.CreateDirectory(stagingRoot);
            var zipPath = Path.Combine(stagingRoot, "deposit-" + stampText + ".zip");
            var extractDir = Path.Combine(stagingRoot, "deposit-extract-" + Guid.NewGuid().ToString("n"));

            var downloadPath = string.IsNullOrWhiteSpace(depositRequest?.DownloadPath)
                ? "/api/agent/client-pack"
                : depositRequest.DownloadPath;

            logger.LogInformation("DepositClientPack: downloading pack to {Zip}", zipPath);
            var (ok, headerVersion) = await api.DownloadClientPackAsync(downloadPath, zipPath, ct);
            if (!ok)
                return (true, false, "Pack download from API failed");

            if (!File.Exists(zipPath) || new FileInfo(zipPath).Length < 64)
                return (true, false, "Downloaded pack zip missing or empty");

            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            var packRoot = ResolvePackRoot(extractDir);
            if (packRoot is null)
                return (true, false, "Install.lnk / Install-WorkstationCollector.cmd not found in pack zip");

            var version = FirstNonEmpty(
                depositRequest?.Version,
                headerVersion,
                TryReadProductVersion(packRoot));

            var depositDir = Path.Combine(tempRoot, ClientPackFolderNames.BuildDepositFolderName(version, stamp));
            if (Directory.Exists(depositDir))
            {
                depositDir = Path.Combine(
                    tempRoot,
                    ClientPackFolderNames.BuildDepositFolderName(version, stamp) + "-" + Guid.NewGuid().ToString("n")[..6]);
            }

            Directory.CreateDirectory(depositDir);
            CopyDirectory(packRoot, depositDir);

            try { Directory.Delete(extractDir, recursive: true); } catch { /* best-effort */ }
            try { File.Delete(zipPath); } catch { /* best-effort */ }

            var detail = $"Deposited pack v{ClientPackFolderNames.SanitizeVersion(version)} to {depositDir} — run Install.lnk there when ready";
            logger.LogWarning("DepositClientPack: {Detail}", detail);
            return (true, true, detail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DepositClientPack failed");
            return (true, false, ex.Message);
        }
        finally
        {
            lock (Gate) { _inFlight = false; }
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return null;
    }

    private static string? TryReadProductVersion(string packFolder)
    {
        var path = Path.Combine(packFolder, "VERSION.json");
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("productVersion", out var v))
                return v.GetString();
        }
        catch
        {
            /* ignore */
        }

        return null;
    }

    private static string? ResolvePackRoot(string extractDir)
    {
        var nested = Path.Combine(extractDir, "Heimdall-Client");
        if (Directory.Exists(nested) && HasInstallEntry(nested))
            return nested;
        if (HasInstallEntry(extractDir))
            return extractDir;
        var found = Directory.EnumerateFiles(extractDir, "Install-WorkstationCollector.cmd", SearchOption.AllDirectories)
            .FirstOrDefault();
        return found is null ? null : Path.GetDirectoryName(found);
    }

    private static bool HasInstallEntry(string dir) =>
        File.Exists(Path.Combine(dir, "Install-WorkstationCollector.cmd"))
        || File.Exists(Path.Combine(dir, "Install.lnk"))
        || File.Exists(Path.Combine(dir, "Install.cmd"));

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destDir, rel));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);
            var parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.Copy(file, dest, overwrite: true);
        }
    }
}

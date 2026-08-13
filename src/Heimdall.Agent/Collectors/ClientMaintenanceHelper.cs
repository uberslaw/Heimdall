using System.Diagnostics;
using Heimdall.Shared;
using Heimdall.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Heimdall.Agent.Collectors;

/// <summary>
/// Client Version remote ops: restart HeimdallAgent (detached) and cleanup staging deposits.
/// </summary>
internal static class ClientMaintenanceHelper
{
    private const string AgentServiceName = "HeimdallAgent";

    public static bool TryExecuteCommand(string command, ILogger logger, out string detail)
    {
        if (string.Equals(command, RemoteMachineCommands.RestartAgent, StringComparison.OrdinalIgnoreCase))
            return TryScheduleAgentRestart(logger, out detail);

        if (string.Equals(command, RemoteMachineCommands.CleanupClientStaging, StringComparison.OrdinalIgnoreCase))
            return TryCleanupStaging(logger, out detail);

        detail = $"Unknown command: {command}";
        return false;
    }

    /// <summary>
    /// Spawn detached cmd that delays then sc stop/start so this process can exit cleanly.
    /// </summary>
    private static bool TryScheduleAgentRestart(ILogger logger, out string detail)
    {
        if (ClientUpdateHelper.IsDurableInstallLockHeld(out var lockDetail))
        {
            detail = "Skipped: durable install.lock present — " + lockDetail;
            logger.LogWarning("{Detail}", detail);
            return false;
        }

        try
        {
            // Delay so heartbeat can ack before the service stops.
            var args =
                "/c timeout /t 3 /nobreak >nul & " +
                $"sc stop {AgentServiceName} & " +
                "timeout /t 2 /nobreak >nul & " +
                $"sc start {AgentServiceName}";

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var proc = Process.Start(psi);
            if (proc is null)
            {
                detail = "could not start restart helper process";
                return false;
            }

            detail = $"RestartAgent scheduled (helper pid {proc.Id}) — sc stop/start {AgentServiceName} after brief delay";
            logger.LogWarning("{Detail}", detail);
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            logger.LogError(ex, "RestartAgent failed to schedule");
            return false;
        }
    }

    private static bool TryCleanupStaging(ILogger logger, out string detail)
    {
        if (ClientUpdateHelper.IsUpdateInFlight)
        {
            detail = "Skipped: UpdateClient is in progress — retry cleanup after deploy finishes";
            logger.LogWarning("{Detail}", detail);
            return false;
        }

        if (ClientUpdateHelper.IsDurableInstallLockHeld(out var lockDetail))
        {
            detail = "Skipped: durable install.lock present — " + lockDetail;
            logger.LogWarning("{Detail}", detail);
            return false;
        }

        var updateRoot = ClientUpdateHelper.GetUpdateRoot();
        var tempRoot = Path.Combine(
            Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\",
            "Temp");

        var removed = new List<string>();
        var locked = new List<string>();
        var missing = new List<string>();

        // Prefer file-level cleanup so we never wipe a race-created install.lock mid-flight.
        CleanupUpdateRootContents(updateRoot, removed, locked, missing, logger);

        // Legacy C:\Temp\Heimdall-Client plus versioned Heimdall-Client-v* / timestamped deposits.
        var tempTargets = new List<string>();
        if (Directory.Exists(tempRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(tempRoot))
            {
                var name = Path.GetFileName(dir);
                if (ClientPackFolderNames.IsTempClientPackFolderName(name))
                    tempTargets.Add(dir);
            }
        }

        if (tempTargets.Count == 0)
        {
            // Still record the legacy path as absent when nothing matched.
            missing.Add(Path.Combine(tempRoot, ClientPackFolderNames.FolderPrefix + "*"));
        }
        else
        {
            foreach (var path in tempTargets.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                CleanupTree(path, removed, locked, missing, logger);
        }

        var parts = new List<string>();
        if (removed.Count > 0)
            parts.Add("removed: " + string.Join("; ", removed));
        if (locked.Count > 0)
            parts.Add("locked (left): " + string.Join("; ", locked));
        if (missing.Count > 0 && removed.Count == 0 && locked.Count == 0)
            parts.Add("nothing to clean (paths absent)");
        else if (missing.Count > 0)
            parts.Add("absent: " + string.Join("; ", missing));

        detail = parts.Count == 0 ? "Cleanup completed (nothing found)" : string.Join(" | ", parts);
        logger.LogInformation("CleanupClientStaging: {Detail}", detail);

        // Partial success (some locked) still acks so the command does not retry forever.
        return locked.Count == 0 || removed.Count > 0;
    }

    /// <summary>
    /// Delete extract/zip deposits under update\, but never remove a live install.lock / install-state.json.
    /// </summary>
    private static void CleanupUpdateRootContents(
        string updateRoot,
        List<string> removed,
        List<string> locked,
        List<string> missing,
        ILogger logger)
    {
        if (!Directory.Exists(updateRoot))
        {
            missing.Add(updateRoot);
            return;
        }

        // Abort if a lock appeared after the earlier gate (installer just started).
        if (ClientUpdateHelper.IsDurableInstallLockHeld(out _))
        {
            locked.Add(Path.Combine(updateRoot, ClientUpdateHelper.InstallLockFileName));
            logger.LogWarning("CleanupClientStaging: install.lock appeared — leaving {Root}", updateRoot);
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(updateRoot).ToList())
        {
            var name = Path.GetFileName(dir);
            // Phase 2 LKG: never delete committed / in-flight backup trees.
            if (name.Equals(ClientUpdateHelper.LkgDirectoryName, StringComparison.OrdinalIgnoreCase)
                || name.Equals(ClientUpdateHelper.LkgStagingDirectoryName, StringComparison.OrdinalIgnoreCase)
                || name.Equals("lkg.old", StringComparison.OrdinalIgnoreCase))
                continue;
            CleanupTree(dir, removed, locked, missing, logger);
        }

        foreach (var file in Directory.EnumerateFiles(updateRoot).ToList())
        {
            var name = Path.GetFileName(file);
            if (name.Equals(ClientUpdateHelper.InstallLockFileName, StringComparison.OrdinalIgnoreCase)
                || name.Equals(ClientUpdateHelper.InstallStateFileName, StringComparison.OrdinalIgnoreCase))
            {
                // Stale lock/state may remain after a failed install — leave for Launch Control / operator.
                locked.Add(file + " (preserved)");
                continue;
            }

            CleanupTree(file, removed, locked, missing, logger);
        }
    }

    private static void CleanupTree(
        string path,
        List<string> removed,
        List<string> locked,
        List<string> missing,
        ILogger logger)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            missing.Add(path);
            return;
        }

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else
                File.Delete(path);
            removed.Add(path);
            return;
        }
        catch (Exception deleteEx)
        {
            logger.LogDebug(deleteEx, "CleanupClientStaging: delete failed for {Path}; trying rename", path);
        }

        try
        {
            var parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(parent))
            {
                locked.Add(path);
                return;
            }

            var quarantine = Path.Combine(
                parent,
                Path.GetFileName(path) + ".old." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("n")[..8]);
            Directory.Move(path, quarantine);
            // Best-effort delete quarantine; if still locked, leave renamed aside.
            try
            {
                Directory.Delete(quarantine, recursive: true);
                removed.Add(path + " (via rename)");
            }
            catch
            {
                removed.Add(path + " (renamed aside)");
            }
        }
        catch (Exception renameEx)
        {
            logger.LogDebug(renameEx, "CleanupClientStaging: leaving locked {Path}", path);
            locked.Add(path);
        }
    }
}

using System.Runtime.Versioning;
using System.ServiceProcess;
using Heimdall.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Heimdall.Agent.Collectors;

[SupportedOSPlatform("windows")]
internal static class TermServiceHelper
{
    private const string ServiceName = "TermService";

    public static string GetStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Refresh();
            return sc.Status.ToString();
        }
        catch
        {
            return "Unknown";
        }
    }

    public static bool TryRestart(ILogger logger, out string detail)
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Refresh();
            logger.LogWarning("Restarting {Service} (current status: {Status})", ServiceName, sc.Status);

            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }

            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            detail = $"Restarted; status={sc.Status}";
            logger.LogInformation("{Service} restart complete: {Detail}", ServiceName, detail);
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            logger.LogError(ex, "Failed to restart {Service}", ServiceName);
            return false;
        }
    }

    public static bool TryExecuteCommand(string command, ILogger logger, out string detail)
    {
        if (string.Equals(command, RemoteMachineCommands.RestartTermService, StringComparison.OrdinalIgnoreCase))
            return TryRestart(logger, out detail);

        detail = $"Unknown command: {command}";
        return false;
    }
}

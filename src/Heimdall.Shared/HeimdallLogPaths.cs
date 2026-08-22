namespace Heimdall.Shared;

/// <summary>
/// Known on-disk locations for Heimdall logs and diagnostic dumps.
/// Always-on runtime logs live under ProgramData (survive Temp cleanup / service-down triage).
/// Grab-and-go collect dumps go under Temp.
/// </summary>
public static class HeimdallLogPaths
{
    /// <summary>Grab-and-go diagnostic bundles (Admin Collect + offline script).</summary>
    public const string DiagnosticsDumpRoot = @"C:\Temp\Heimdall.API\Logs";

    public static string ProgramDataHeimdall =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Heimdall");

    public static string LogsRoot => Path.Combine(ProgramDataHeimdall, "logs");

    /// <summary>Rolling API ILogger files: heimdall-api-yyyyMMdd.log</summary>
    public static string ApiLogsDir => Path.Combine(LogsRoot, "api");

    /// <summary>Append-only admin/ops actions: ops-yyyyMMdd.log</summary>
    public static string OpsLogsDir => Path.Combine(LogsRoot, "ops");

    /// <summary>ApiHeal scheduled-task watchdog: api-heal-yyyyMMdd.log</summary>
    public static string ApiHealLogsDir => Path.Combine(LogsRoot, "api-heal");

    public static string ApiHealLogFileName(DateTime localDate) =>
        $"api-heal-{localDate:yyyyMMdd}.log";

    public static string ApiLogFileName(DateTime utcDate) =>
        $"heimdall-api-{utcDate:yyyyMMdd}.log";

    public static string OpsLogFileName(DateTime utcDate) =>
        $"ops-{utcDate:yyyyMMdd}.log";

    public static string TodayApiLogPath() =>
        Path.Combine(ApiLogsDir, ApiLogFileName(DateTime.UtcNow));

    public static string TodayOpsLogPath() =>
        Path.Combine(OpsLogsDir, OpsLogFileName(DateTime.UtcNow));
}

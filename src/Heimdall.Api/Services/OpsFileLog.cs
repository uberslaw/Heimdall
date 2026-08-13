using System.Text;
using Heimdall.Shared;

namespace Heimdall.Api.Services;

/// <summary>
/// Append-only ops/admin action log under %ProgramData%\Heimdall\logs\ops\.
/// Not a full audit DB — enough to review pack/deploy/deposit/restart/cleanup/mode switches.
/// </summary>
public static class OpsFileLog
{
    private static readonly object Gate = new();
    private static int _prunedDay = -1;

    public static void Write(string action, string? detail = null, string? actor = null)
    {
        try
        {
            Directory.CreateDirectory(HeimdallLogPaths.OpsLogsDir);
            PruneIfNeeded();

            var sb = new StringBuilder();
            sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append("Z | ");
            sb.Append(Sanitize(action));
            if (!string.IsNullOrWhiteSpace(actor))
            {
                sb.Append(" | actor=");
                sb.Append(Sanitize(actor));
            }

            sb.Append(" | host=");
            sb.Append(Environment.MachineName);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                sb.Append(" | ");
                sb.Append(Sanitize(detail).Replace('\r', ' ').Replace('\n', ' '));
            }

            lock (Gate)
            {
                File.AppendAllText(HeimdallLogPaths.TodayOpsLogPath(), sb + Environment.NewLine);
            }
        }
        catch
        {
            // Never fail the calling admin action because of ops logging.
        }
    }

    private static void PruneIfNeeded()
    {
        var day = DateTime.UtcNow.DayOfYear + DateTime.UtcNow.Year * 1000;
        if (_prunedDay == day)
            return;
        _prunedDay = day;
        try
        {
            var cutoff = DateTime.UtcNow.Date.AddDays(-14);
            foreach (var file in Directory.EnumerateFiles(HeimdallLogPaths.OpsLogsDir, "ops-*.log"))
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var stamp = name.Length >= 8 ? name[^8..] : null;
                    if (stamp is not null
                        && DateTime.TryParseExact(stamp, "yyyyMMdd", null,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var d)
                        && d.Date < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string Sanitize(string value) =>
        value.Replace('|', '/').Trim();
}

using System.Text.Json;

namespace Heimdall.Agent.Services;

/// <summary>
/// Persisted weekly opportunistic inventory schedule. Counter survives sleep/reboot/day;
/// resets when the ISO week changes after a successful inventory.
/// </summary>
public sealed class WeeklyInventoryState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string WeekKey { get; set; } = "";
    public DateTimeOffset? ScheduledUtc { get; set; }
    public int FailedIdleAttempts { get; set; }
    public DateTimeOffset? NextRetryUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }

    public static string CurrentWeekKey(DateTimeOffset utc)
    {
        var cal = System.Globalization.ISOWeek.GetWeekOfYear(utc.UtcDateTime);
        return $"{utc.Year:D4}-W{cal:D2}";
    }

    public static WeeklyInventoryState LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var state = JsonSerializer.Deserialize<WeeklyInventoryState>(json, JsonOptions);
                if (state is not null)
                    return state;
            }
        }
        catch
        {
            // fall through to new
        }

        return new WeeklyInventoryState();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>
    /// Ensures a random schedule in the current week exists. Returns true when the agent should
    /// attempt inventory now (scheduled time reached / retry due, and not yet completed this week).
    /// </summary>
    public bool ShouldAttempt(DateTimeOffset now, out bool isRetry)
    {
        isRetry = false;
        var week = CurrentWeekKey(now);
        if (!string.Equals(WeekKey, week, StringComparison.Ordinal))
        {
            WeekKey = week;
            FailedIdleAttempts = 0;
            NextRetryUtc = null;
            CompletedUtc = null;
            // Random offset within the remaining portion of the week (or full week if early).
            var rand = Random.Shared.NextDouble();
            ScheduledUtc = now.AddHours(rand * 24 * 5); // within ~5 days from first check
            return false;
        }

        if (CompletedUtc is not null)
            return false;

        if (FailedIdleAttempts >= 6)
            return false;

        if (NextRetryUtc is DateTimeOffset retry && now >= retry)
        {
            isRetry = true;
            return true;
        }

        if (ScheduledUtc is DateTimeOffset scheduled && now >= scheduled && NextRetryUtc is null)
            return true;

        return false;
    }

    public void RecordIdleFailure(DateTimeOffset now)
    {
        FailedIdleAttempts++;
        NextRetryUtc = now.AddHours(2);
        ScheduledUtc ??= now;
    }

    public void RecordSuccess(DateTimeOffset now)
    {
        CompletedUtc = now;
        NextRetryUtc = null;
    }
}

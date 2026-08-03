using Heimdall.Api.Data;

namespace Heimdall.Api.Services;

/// <summary>
/// Aggregates ProcessRun intervals for user-facing "time in use" metrics.
/// Uses wall-clock union (not sum) so concurrent instances of the same process do not inflate totals.
/// </summary>
public static class ProcessRunMetrics
{
    public static double RunDurationSeconds(ProcessRun run, DateTimeOffset? clipFromUtc = null, DateTimeOffset? clipToUtc = null)
    {
        var (start, end) = GetClippedInterval(run, clipFromUtc, clipToUtc);
        return Math.Max(0, (end - start).TotalSeconds);
    }

    public static double SumDurationSeconds(
        IEnumerable<ProcessRun> runs,
        DateTimeOffset? clipFromUtc = null,
        DateTimeOffset? clipToUtc = null) =>
        runs.Sum(r => RunDurationSeconds(r, clipFromUtc, clipToUtc));

    /// <summary>
    /// Wall-clock seconds covered by at least one run (merged overlapping intervals).
    /// </summary>
    public static double UnionDurationSeconds(
        IEnumerable<ProcessRun> runs,
        DateTimeOffset? clipFromUtc = null,
        DateTimeOffset? clipToUtc = null)
    {
        var intervals = runs
            .Select(r => GetClippedInterval(r, clipFromUtc, clipToUtc))
            .Where(i => i.End > i.Start)
            .ToList();

        return UnionDurationSeconds(intervals);
    }

    /// <summary>
    /// Time-weighted average number of concurrent process instances.
    /// Equals total per-run seconds ÷ union wall-clock seconds.
    /// </summary>
    public static double AvgConcurrentProcesses(
        IEnumerable<ProcessRun> runs,
        DateTimeOffset? clipFromUtc = null,
        DateTimeOffset? clipToUtc = null)
    {
        var runList = runs as IReadOnlyList<ProcessRun> ?? runs.ToList();
        if (runList.Count == 0)
            return 0;

        var sumSeconds = SumDurationSeconds(runList, clipFromUtc, clipToUtc);
        var unionSeconds = UnionDurationSeconds(runList, clipFromUtc, clipToUtc);
        if (unionSeconds <= 0)
            return 0;

        return sumSeconds / unionSeconds;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) GetClippedInterval(
        ProcessRun run,
        DateTimeOffset? clipFromUtc,
        DateTimeOffset? clipToUtc)
    {
        var start = run.StartedAtUtc;
        var end = run.EndedAtUtc ?? run.LastSeenAtUtc;

        if (clipFromUtc is { } from && start < from)
            start = from;
        if (clipToUtc is { } to && end > to)
            end = to;

        return (start, end);
    }

    private static double UnionDurationSeconds(List<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        if (intervals.Count == 0)
            return 0;

        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));

        var mergedStart = intervals[0].Start;
        var mergedEnd = intervals[0].End;
        var total = 0.0;

        for (var i = 1; i < intervals.Count; i++)
        {
            var (start, end) = intervals[i];
            if (start <= mergedEnd)
            {
                if (end > mergedEnd)
                    mergedEnd = end;
            }
            else
            {
                total += (mergedEnd - mergedStart).TotalSeconds;
                mergedStart = start;
                mergedEnd = end;
            }
        }

        total += (mergedEnd - mergedStart).TotalSeconds;
        return Math.Max(0, total);
    }
}

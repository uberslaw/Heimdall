using System.Collections.Concurrent;
using Heimdall.Shared;
using Microsoft.Extensions.Logging;

namespace Heimdall.Api.Logging;

/// <summary>
/// Thin daily rolling file sink for API ILogger → %ProgramData%\Heimdall\logs\api\.
/// Keeps console / Event Log providers from the host; does not replace them.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retainDays;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();
    private readonly object _pruneLock = new();
    private bool _pruned;
    private bool _disposed;

    public RollingFileLoggerProvider(string? directory = null, int retainDays = 14)
    {
        _directory = directory ?? HeimdallLogPaths.ApiLogsDir;
        _retainDays = Math.Max(1, retainDays);
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch
        {
            // Best-effort; writes will fail softly.
        }
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(name, this));

    internal void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        if (_disposed)
            return;

        EnsurePruned();

        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [{level}] {category}" +
                   (eventId.Id != 0 ? $" ({eventId.Id})" : "") +
                   $": {message}";
        if (exception is not null)
            line += Environment.NewLine + exception;

        var path = Path.Combine(_directory, HeimdallLogPaths.ApiLogFileName(DateTime.UtcNow));
        lock (_writeLock)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // Never throw from logging.
            }
        }
    }

    private void EnsurePruned()
    {
        if (_pruned)
            return;
        lock (_pruneLock)
        {
            if (_pruned)
                return;
            _pruned = true;
            try
            {
                if (!Directory.Exists(_directory))
                    return;
                var cutoff = DateTime.UtcNow.Date.AddDays(-_retainDays);
                foreach (var file in Directory.EnumerateFiles(_directory, "heimdall-api-*.log"))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        // heimdall-api-yyyyMMdd
                        var stamp = name.Length >= 8 ? name[^8..] : null;
                        if (stamp is not null
                            && DateTime.TryParseExact(stamp, "yyyyMMdd", null,
                                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                                out var day)
                            && day.Date < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        // ignore per-file prune failures
                    }
                }
            }
            catch
            {
                // ignore prune failures
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _loggers.Clear();
    }

    private sealed class RollingFileLogger(string category, RollingFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && !provider._disposed;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
        }
    }
}

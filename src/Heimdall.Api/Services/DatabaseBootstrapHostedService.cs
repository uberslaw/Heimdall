using Heimdall.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Applies schema patches and reference seed after the generic host has accepted the Windows
/// service start. Doing this before <c>app.Run()</c> kept <c>StartServiceCtrlDispatcher</c> from
/// running inside the SCM pipe timeout, so Launch Control reported Win32 1053 and the service
/// stayed stopped. Full-table repairs run afterwards and do not block startup.
/// </summary>
public sealed class DatabaseBootstrapHostedService(
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<DatabaseBootstrapHostedService> logger) : IHostedService
{
    private const int SeedAttempts = 4;
    private Task? _maintenance;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        OpsFileLog.Write("api-startup", "bootstrap begin");
        var ready = new List<string>(2);
        foreach (var mode in new[] { HeimdallDatabaseMode.Live, HeimdallDatabaseMode.Sandbox })
        {
            if (await TrySeedAsync(mode, cancellationToken))
                ready.Add(mode);
        }

        logger.LogInformation(
            "Database bootstrap finished in {Ms} ms (ready: {Modes})",
            sw.ElapsedMilliseconds,
            ready.Count == 0 ? "none" : string.Join(", ", ready));
        OpsFileLog.Write(
            "api-startup",
            $"bootstrap done {sw.ElapsedMilliseconds}ms ready={(ready.Count == 0 ? "none" : string.Join(",", ready))}");

        if (ready.Count > 0)
        {
            // SQLite async APIs often complete inline. Starting the task directly would run the
            // FleetMetricSnapshots scan on the host startup thread and hold Kestrel until it finished.
            _maintenance = Task.Run(() => RunMaintenanceAsync(ready, lifetime.ApplicationStopping));
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_maintenance is null)
            return;

        try
        {
            await _maintenance.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown wins over a long one-shot repair.
        }
    }

    private async Task<bool> TrySeedAsync(string mode, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= SeedAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                OpsFileLog.Write("api-startup", $"bootstrap cancelled mode={mode}");
                return false;
            }

            try
            {
                await using var db = Open(mode, busySeconds: 15);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await SeedData.EnsureSeededAsync(db, ct);
                logger.LogInformation("Database {Mode} schema ready in {Ms} ms", mode, sw.ElapsedMilliseconds);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogError("Database {Mode} schema ensure cancelled", mode);
                OpsFileLog.Write("api-startup", $"bootstrap cancelled mode={mode}");
                return false;
            }
            catch (Exception ex) when (IsDatabaseLocked(ex) && attempt < SeedAttempts)
            {
                logger.LogWarning(
                    ex,
                    "Database {Mode} is locked (attempt {Attempt}/{Attempts}). Another Heimdall.Api process may still be exiting; end that process if this persists.",
                    mode,
                    attempt,
                    SeedAttempts);
                OpsFileLog.Write("api-startup", $"db locked mode={mode} attempt={attempt}: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                // Stay running. A throw here used to kill the process before SCM was answered,
                // which Launch Control reports as Win32 1053.
                logger.LogCritical(
                    ex,
                    "Database {Mode} schema ensure failed. The Windows service will stay up.",
                    mode);
                OpsFileLog.Write("api-startup", $"bootstrap FAILED mode={mode} {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        return false;
    }

    private async Task RunMaintenanceAsync(IReadOnlyList<string> modes, CancellationToken ct)
    {
        foreach (var mode in modes)
        {
            if (ct.IsCancellationRequested)
                return;

            try
            {
                await using var db = Open(mode, busySeconds: 30);
                var sessions = await SeedData.RepairLegacySessionsOnceAsync(db, ct);
                var gpu = await SeedData.NullInsaneGpuPercentsOnceAsync(db, ct);
                var junk = await SeedData.PurgeIneligibleCatalogAsync(db, ct);
                logger.LogInformation(
                    "Database {Mode} maintenance: sessionRepair={Session} gpuRepair={Gpu} catalogJunk={Junk}",
                    mode,
                    sessions,
                    gpu,
                    junk);
                OpsFileLog.Write(
                    "api-startup",
                    $"maintenance mode={mode} sessionRepair={sessions} gpuRepair={gpu} catalogJunk={junk}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Database {Mode} maintenance failed. Schema ensure already finished.", mode);
                OpsFileLog.Write("api-startup", $"maintenance FAILED mode={mode} {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private HeimdallDbContext Open(string mode, int busySeconds)
    {
        var conn = WithBusyTimeout(
            HeimdallDatabaseMode.GetConnectionStringForMode(configuration, mode),
            busySeconds);
        var options = new DbContextOptionsBuilder<HeimdallDbContext>().UseSqlite(conn).Options;
        return new HeimdallDbContext(options);
    }

    private static string WithBusyTimeout(string connectionString, int seconds)
    {
        if (connectionString.Contains("Default Timeout=", StringComparison.OrdinalIgnoreCase))
            return connectionString;

        return connectionString.Trim().TrimEnd(';') + $";Default Timeout={seconds}";
    }

    private static bool IsDatabaseLocked(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite && sqlite.SqliteErrorCode is 5 or 6)
                return true;
            if (current.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("database is busy", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

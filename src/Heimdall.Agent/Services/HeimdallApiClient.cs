using System.Net;
using System.Net.Http.Json;
using Heimdall.Shared.Contracts;

namespace Heimdall.Agent.Services;

public enum TelemetryUploadResult
{
    Ok,
    /// <summary>Transient failure — keep in offline queue and retry later.</summary>
    Retryable,
    /// <summary>Client/permanent error — drop the queued item so it cannot block the drain.</summary>
    Permanent
}

public sealed class HeimdallApiClient(HttpClient http, IConfiguration config, ILogger<HeimdallApiClient> logger)
{
    public async Task<bool> UploadAsync(IngestBatchDto batch, CancellationToken ct) =>
        await UploadIngestAsync(batch, ct) == TelemetryUploadResult.Ok;

    public async Task<TelemetryUploadResult> UploadIngestAsync(IngestBatchDto batch, CancellationToken ct)
    {
        try
        {
            ApplyKey();
            using var response = await http.PostAsJsonAsync("/api/ingest", batch, ct);
            return Classify(response.StatusCode, "Ingest");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ingest upload error");
            return TelemetryUploadResult.Retryable;
        }
    }

    public async Task<AgentConfigDto?> GetConfigAsync(string hostname, CancellationToken ct)
    {
        try
        {
            ApplyKey();
            return await http.GetFromJsonAsync<AgentConfigDto>($"/api/config/{Uri.EscapeDataString(hostname)}", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Config refresh failed");
            return null;
        }
    }

    /// <summary>Fast, independent poll (not tied to ConfigRefreshSeconds) for the live-sampling on/off flag. Null on failure — caller keeps its previous state rather than flapping on transient network errors.</summary>
    public async Task<ResourceSamplingStatusDto?> GetResourceSamplingStatusAsync(string hostname, CancellationToken ct)
    {
        try
        {
            ApplyKey();
            return await http.GetFromJsonAsync<ResourceSamplingStatusDto>(
                $"/api/resource-sampling/{Uri.EscapeDataString(hostname)}/status", ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Resource sampling status poll failed");
            return null;
        }
    }

    /// <summary>
    /// Fast, independent poll (not tied to ConfigRefreshSeconds) for a queued TUFLOW start/stop — same
    /// pattern as GetResourceSamplingStatusAsync above. Null on failure — caller just tries again next
    /// tick rather than treating a transient network blip as "nothing pending".
    /// </summary>
    public async Task<TuflowPendingDto?> GetTuflowPendingAsync(string hostname, CancellationToken ct)
    {
        try
        {
            ApplyKey();
            return await http.GetFromJsonAsync<TuflowPendingDto>(
                $"/api/tuflow/{Uri.EscapeDataString(hostname)}/pending", ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "TUFLOW pending poll failed");
            return null;
        }
    }

    /// <summary>Fast poll for a queued disk-usage scan (~20s), same pattern as TUFLOW pending.</summary>
    public async Task<DiskUsagePendingDto?> GetDiskUsagePendingAsync(string hostname, CancellationToken ct)
    {
        try
        {
            ApplyKey();
            return await http.GetFromJsonAsync<DiskUsagePendingDto>(
                $"/api/disk-usage/{Uri.EscapeDataString(hostname)}/pending", ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Disk usage pending poll failed");
            return null;
        }
    }

    /// <summary>Mid-scan progress — dropped on failure; next tick supersedes.</summary>
    public async Task ReportDiskUsageProgressAsync(string hostname, DiskUsageScanProgressDto dto, CancellationToken ct)
    {
        try
        {
            ApplyKey();
            using var response = await http.PostAsJsonAsync(
                $"/api/disk-usage/{Uri.EscapeDataString(hostname)}/progress", dto, ct);
            _ = response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Disk usage progress report failed (dropped)");
        }
    }

    public async Task<bool> ReportResourceSampleAsync(ResourceSampleReportDto dto, CancellationToken ct) =>
        await ReportResourceSampleResultAsync(dto, ct) == TelemetryUploadResult.Ok;

    public async Task<TelemetryUploadResult> ReportResourceSampleResultAsync(ResourceSampleReportDto dto, CancellationToken ct)
    {
        try
        {
            ApplyKey();
            using var response = await http.PostAsJsonAsync("/api/resource-sampling/report", dto, ct);
            return Classify(response.StatusCode, "Resource sample");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Resource sample report failed");
            return TelemetryUploadResult.Retryable;
        }
    }

    public async Task<bool> ReportFleetSnapshotAsync(FleetSnapshotDto dto, CancellationToken ct) =>
        await ReportFleetSnapshotResultAsync(dto, ct) == TelemetryUploadResult.Ok;

    public async Task<TelemetryUploadResult> ReportFleetSnapshotResultAsync(FleetSnapshotDto dto, CancellationToken ct)
    {
        try
        {
            ApplyKey();
            using var response = await http.PostAsJsonAsync("/api/fleet/snapshot", dto, ct);
            return Classify(response.StatusCode, "Fleet snapshot");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Fleet snapshot report failed");
            return TelemetryUploadResult.Retryable;
        }
    }

    /// <summary>
    /// Downloads the client pack zip. Returns success and optional productVersion from
    /// <c>X-Heimdall-Client-Version</c> (folder naming / diagnostics).
    /// </summary>
    public async Task<(bool Ok, string? ClientVersion)> DownloadClientPackAsync(
        string downloadPath,
        string destFile,
        CancellationToken ct)
    {
        try
        {
            ApplyKey();
            var path = string.IsNullOrWhiteSpace(downloadPath) ? "/api/agent/client-pack" : downloadPath;
            if (!path.StartsWith('/'))
                path = "/" + path;

            using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Client pack download failed: {Status}", response.StatusCode);
                return (false, null);
            }

            string? version = null;
            if (response.Headers.TryGetValues("X-Heimdall-Client-Version", out var values))
                version = values.FirstOrDefault();
            else if (response.Content.Headers.TryGetValues("X-Heimdall-Client-Version", out var contentValues))
                version = contentValues.FirstOrDefault();

            await using var fs = File.Create(destFile);
            await response.Content.CopyToAsync(fs, ct);
            return (true, string.IsNullOrWhiteSpace(version) ? null : version.Trim());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Client pack download error");
            return (false, null);
        }
    }

    private TelemetryUploadResult Classify(HttpStatusCode status, string label)
    {
        var code = (int)status;
        if (code is >= 200 and <= 299)
            return TelemetryUploadResult.Ok;

        // Auth / validation / not found — do not block the offline drain forever.
        if (status is HttpStatusCode.BadRequest
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound
            or HttpStatusCode.Conflict
            or HttpStatusCode.RequestEntityTooLarge
            or HttpStatusCode.UnprocessableEntity)
        {
            logger.LogWarning("{Label} permanent failure: {Status}", label, status);
            return TelemetryUploadResult.Permanent;
        }

        logger.LogWarning("{Label} failed: {Status}", label, status);
        return TelemetryUploadResult.Retryable;
    }

    private void ApplyKey()
    {
        var key = config["Heimdall:ApiKey"] ?? "heimdall-poc-key";
        http.DefaultRequestHeaders.Remove("X-Heimdall-Key");
        http.DefaultRequestHeaders.Add("X-Heimdall-Key", key);
    }
}

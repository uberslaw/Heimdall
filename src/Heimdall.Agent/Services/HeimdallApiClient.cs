using System.Net.Http.Json;
using Heimdall.Shared.Contracts;

namespace Heimdall.Agent.Services;

public sealed class HeimdallApiClient(HttpClient http, IConfiguration config, ILogger<HeimdallApiClient> logger)
{
    public async Task<bool> UploadAsync(IngestBatchDto batch, CancellationToken ct)
    {
        try
        {
            ApplyKey();
            using var response = await http.PostAsJsonAsync("/api/ingest", batch, ct);
            if (response.IsSuccessStatusCode)
                return true;

            logger.LogWarning("Ingest failed: {Status}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ingest upload error");
            return false;
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

    /// <summary>
    /// Live metric samples are near-real-time only — unlike UploadAsync, a failed report is dropped, not
    /// queued offline. A stale queued "point in time" reading would just show wrong data later; better to
    /// skip and let the next 10s reading (or next calibration burst) supersede it.
    /// </summary>
    public async Task<bool> ReportResourceSampleAsync(ResourceSampleReportDto dto, CancellationToken ct)
    {
        try
        {
            ApplyKey();
            using var response = await http.PostAsJsonAsync("/api/resource-sampling/report", dto, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Resource sample report failed (dropped)");
            return false;
        }
    }

    /// <summary>
    /// Always-on fleet snapshot for Historical Dashboard enrollment. Like live samples, failures are
    /// dropped (not queued) — the next 30s tick supersedes a missed point.
    /// </summary>
    public async Task<bool> ReportFleetSnapshotAsync(FleetSnapshotDto dto, CancellationToken ct)
    {
        try
        {
            ApplyKey();
            using var response = await http.PostAsJsonAsync("/api/fleet/snapshot", dto, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Fleet snapshot report failed (dropped)");
            return false;
        }
    }

    public async Task<bool> DownloadClientPackAsync(string downloadPath, string destFile, CancellationToken ct)
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
                return false;
            }

            await using var fs = File.Create(destFile);
            await response.Content.CopyToAsync(fs, ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Client pack download error");
            return false;
        }
    }

    private void ApplyKey()
    {
        var key = config["Heimdall:ApiKey"] ?? "heimdall-poc-key";
        http.DefaultRequestHeaders.Remove("X-Heimdall-Key");
        http.DefaultRequestHeaders.Add("X-Heimdall-Key", key);
    }
}

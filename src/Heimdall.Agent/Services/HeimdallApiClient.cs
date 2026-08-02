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

    private void ApplyKey()
    {
        var key = config["Heimdall:ApiKey"] ?? "heimdall-poc-key";
        http.DefaultRequestHeaders.Remove("X-Heimdall-Key");
        http.DefaultRequestHeaders.Add("X-Heimdall-Key", key);
    }
}

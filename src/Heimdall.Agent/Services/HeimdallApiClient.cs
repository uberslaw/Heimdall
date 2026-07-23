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

    private void ApplyKey()
    {
        var key = config["Heimdall:ApiKey"] ?? "heimdall-poc-key";
        http.DefaultRequestHeaders.Remove("X-Heimdall-Key");
        http.DefaultRequestHeaders.Add("X-Heimdall-Key", key);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Heimdall.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Heimdall.Agent.Collectors;

/// <summary>
/// Applies SetApiBaseUrl: rewrite Program Files agent appsettings.json, then schedule service restart.
/// </summary>
internal static class ApiBaseUrlHelper
{
    public static bool TryApply(
        string? pendingUrl,
        ILogger logger,
        out string detail,
        out bool scheduleRestart)
    {
        scheduleRestart = false;
        detail = "";

        var url = Normalize(pendingUrl);
        if (url is null)
        {
            detail = "Invalid or empty PendingApiBaseUrl (need http:// or https://)";
            return false;
        }

        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                         ?? AppContext.BaseDirectory;
            var settingsPath = Path.Combine(exeDir, "appsettings.json");
            if (!File.Exists(settingsPath))
            {
                detail = $"appsettings.json not found at {settingsPath}";
                return false;
            }

            var text = File.ReadAllText(settingsPath);
            var root = JsonNode.Parse(text) as JsonObject
                       ?? throw new InvalidOperationException("appsettings.json root is not an object");
            var heimdall = root["Heimdall"] as JsonObject;
            if (heimdall is null)
            {
                heimdall = new JsonObject();
                root["Heimdall"] = heimdall;
            }

            var old = heimdall["ApiBaseUrl"]?.GetValue<string>();
            heimdall["ApiBaseUrl"] = url;

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(settingsPath, root.ToJsonString(options));

            detail = string.IsNullOrWhiteSpace(old)
                ? $"ApiBaseUrl set to {url}"
                : $"ApiBaseUrl {old} → {url}";
            logger.LogWarning("SetApiBaseUrl: {Detail} (wrote {Path})", detail, settingsPath);
            scheduleRestart = true;
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            logger.LogError(ex, "SetApiBaseUrl failed");
            return false;
        }
    }

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var url = raw.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;
        if (string.IsNullOrWhiteSpace(uri.Host))
            return null;
        return url;
    }
}

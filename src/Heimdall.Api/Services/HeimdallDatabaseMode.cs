namespace Heimdall.Api.Services;

/// <summary>
/// Live vs sandbox database mode for the dashboard UI. Agent ingest/config APIs always use the live DB.
/// </summary>
public static class HeimdallDatabaseMode
{
    public const string Live = "live";
    public const string Sandbox = "sandbox";
    public const string CookieName = "hd_db_mode";
    public const string ConfigKey = "Heimdall:DatabaseMode";

    public static string Normalize(string? value) =>
        string.Equals(value, Sandbox, StringComparison.OrdinalIgnoreCase) ? Sandbox : Live;

    public static string ResolveCookieMode(IConfiguration configuration, IRequestCookieCollection? cookies)
    {
        if (cookies?.TryGetValue(CookieName, out var cookie) == true && !string.IsNullOrWhiteSpace(cookie))
            return Normalize(cookie);

        return Normalize(configuration[ConfigKey]);
    }

    /// <summary>
    /// Effective mode for UI and staff APIs. Development host defaults to sandbox unless cookie/config is explicitly live.
    /// </summary>
    public static string ResolveEffectiveMode(
        IConfiguration configuration,
        IRequestCookieCollection? cookies,
        IWebHostEnvironment environment)
    {
        if (environment.IsDevelopment() && !IsExplicitlyLive(cookies, configuration))
            return Sandbox;

        return ResolveCookieMode(configuration, cookies);
    }

    public static bool ShowDevChrome(
        IConfiguration configuration,
        IRequestCookieCollection? cookies,
        IWebHostEnvironment environment) =>
        environment.IsDevelopment() || ResolveCookieMode(configuration, cookies) == Sandbox;

    public static bool IsExplicitlyLive(IRequestCookieCollection? cookies, IConfiguration configuration) =>
        (cookies?.TryGetValue(CookieName, out var cookie) == true
         && string.Equals(cookie, Live, StringComparison.OrdinalIgnoreCase))
        || string.Equals(configuration[ConfigKey], Live, StringComparison.OrdinalIgnoreCase);

    public static string GetLiveConnectionString(IConfiguration configuration) =>
        NormalizeSqliteConnectionString(
            configuration.GetConnectionString("Heimdall")
            ?? $"Data Source={Path.Combine(AppContext.BaseDirectory, "heimdall.db")}");

    public static string GetSandboxConnectionString(IConfiguration configuration)
    {
        var explicitSandbox = configuration.GetConnectionString("HeimdallSandbox");
        if (!string.IsNullOrWhiteSpace(explicitSandbox))
            return NormalizeSqliteConnectionString(explicitSandbox);

        var livePath = ExtractDataSourcePath(GetLiveConnectionString(configuration));
        var directory = Path.GetDirectoryName(livePath);
        if (string.IsNullOrEmpty(directory))
            directory = AppContext.BaseDirectory;

        return $"Data Source={Path.Combine(directory, "heimdall-dev.db")}";
    }

    public static string GetConnectionStringForMode(IConfiguration configuration, string mode) =>
        Normalize(mode) == Sandbox
            ? GetSandboxConnectionString(configuration)
            : GetLiveConnectionString(configuration);

    public static string GetDisplayDatabasePath(IConfiguration configuration, string mode)
    {
        var path = ExtractDataSourcePath(GetConnectionStringForMode(configuration, mode));
        return Path.GetFullPath(path);
    }

    /// <summary>Agent-facing routes must never read/write the sandbox database.</summary>
    public static bool IsAgentApiPath(PathString path)
    {
        if (!path.StartsWithSegments("/api", out var remainder) || !remainder.HasValue)
            return false;

        var segment = remainder.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return segment is "ingest" or "config" or "resource-sampling" or "remote";
    }

    public static string NormalizeSqliteConnectionString(string value) =>
        value.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"Data Source={value}";

    public static string ExtractDataSourcePath(string connectionString)
    {
        const string prefix = "Data Source=";
        if (!connectionString.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return connectionString;

        return connectionString[prefix.Length..].Trim();
    }
}

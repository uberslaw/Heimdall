namespace Heimdall.Api.Services;

/// <summary>
/// Picks the SQLite file per request: live for agent APIs, cookie/config mode for UI and staff APIs.
/// </summary>
public sealed class HeimdallDbConnectionResolver(
    IConfiguration configuration,
    IHttpContextAccessor httpContextAccessor,
    IWebHostEnvironment environment)
{
    public string ResolveConnectionString()
    {
        var ctx = httpContextAccessor.HttpContext;
        if (ctx is not null && HeimdallDatabaseMode.IsAgentApiPath(ctx.Request.Path))
            return HeimdallDatabaseMode.GetLiveConnectionString(configuration);

        var mode = ctx is not null
            ? HeimdallDatabaseMode.ResolveEffectiveMode(configuration, ctx.Request.Cookies, environment)
            : HeimdallDatabaseMode.Live;

        return HeimdallDatabaseMode.GetConnectionStringForMode(configuration, mode);
    }
}

using System.Reflection;
using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Required for SCM registration; without this, Windows service start times out (Error 1053).
builder.Host.UseWindowsService(options => options.ServiceName = "HeimdallApi");

builder.Services.Configure<StaffAccessOptions>(builder.Configuration.GetSection("Heimdall:StaffAccess"));
builder.Services.AddSingleton<WindowsStaffIdentityService>();
builder.Services.AddScoped<StaffAccessGuard>();

var staffAccessOpts = builder.Configuration.GetSection("Heimdall:StaffAccess").Get<StaffAccessOptions>() ?? new();
if (staffAccessOpts.RequireWindowsAuth)
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
    builder.Services.AddAuthorization();
}

builder.Services.AddRazorPages();
builder.Services.AddDbContext<HeimdallDbContext>(options =>
{
    var dbPath = builder.Configuration.GetConnectionString("Heimdall")
                 ?? $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "heimdall.db")}";
    options.UseSqlite(dbPath.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
        ? dbPath
        : $"Data Source={dbPath}");
});
builder.Services.AddScoped<IngestService>();
builder.Services.AddScoped<ConfigService>();
builder.Services.AddScoped<ProcessGroupService>();
builder.Services.AddScoped<AppListService>();
builder.Services.AddScoped<StatsQueryService>();
builder.Services.AddScoped<SocratizeQueryService>();
builder.Services.AddScoped<RemoteMachineService>();
builder.Services.AddScoped<RemoteAccessGroupService>();
builder.Services.AddScoped<LiveSamplingService>();
builder.Services.AddScoped<SessionDrilldownService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HeimdallDbContext>();
    await SeedData.EnsureSeededAsync(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// HTTP-only is fine for local POC; enable HTTPS redirection when terminating TLS.
if (app.Configuration.GetValue("Heimdall:UseHttpsRedirection", false))
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
if (staffAccessOpts.RequireWindowsAuth)
    app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.MapGet("/ui-theme", (HttpContext ctx, string theme, string? returnUrl) =>
{
    var normalized = UiTheme.Normalize(theme);
    ctx.Response.Cookies.Append(UiTheme.CookieName, normalized, new CookieOptions
    {
        Path = "/",
        MaxAge = TimeSpan.FromDays(365),
        SameSite = SameSiteMode.Lax,
        IsEssential = true
    });

    var dest = "/";
    if (!string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal))
    {
        dest = returnUrl;
    }

    return Results.Redirect(dest);
});

app.MapGet("/ui-gold", (HttpContext ctx, string gold, string? returnUrl) =>
{
    var normalized = UiGoldVariant.Normalize(gold);
    ctx.Response.Cookies.Append(UiGoldVariant.CookieName, normalized, new CookieOptions
    {
        Path = "/",
        MaxAge = TimeSpan.FromDays(365),
        SameSite = SameSiteMode.Lax,
        IsEssential = true
    });

    var dest = "/";
    if (!string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal))
    {
        dest = returnUrl;
    }

    return Results.Redirect(dest);
});

var apiKey = builder.Configuration["Heimdall:ApiKey"] ?? "heimdall-poc-key";

bool IsAuthorized(HttpRequest request) =>
    request.Headers.TryGetValue("X-Heimdall-Key", out var key) &&
    string.Equals(key.ToString(), apiKey, StringComparison.Ordinal);

app.MapPost("/api/ingest", async (IngestBatchDto batch, IngestService ingest, HttpRequest request) =>
{
    if (!IsAuthorized(request))
        return Results.Unauthorized();

    await ingest.IngestAsync(batch, request.HttpContext.RequestAborted);
    return Results.Accepted();
});

app.MapGet("/api/config/{hostname}", async (string hostname, ConfigService config, HttpRequest request) =>
{
    if (!IsAuthorized(request))
        return Results.Unauthorized();

    var dto = await config.ResolveForHostAsync(hostname, request.HttpContext.RequestAborted);
    return Results.Ok(dto);
});

app.MapGet("/api/remote/{hostname}/restart-status", async (string hostname, RemoteMachineService remote, CancellationToken ct) =>
{
    var row = await remote.GetRowAsync(hostname, ct);
    if (row is null)
        return Results.NotFound();

    return Results.Json(RemoteMachineService.ToRestartStatusDto(row));
});

// --- Agent-facing resource sampling (dedicated fast poll — independent of the slow ConfigRefreshSeconds cycle) ---

app.MapGet("/api/resource-sampling/{hostname}/status", async (string hostname, LiveSamplingService sampling, HttpRequest request, CancellationToken ct) =>
{
    if (!IsAuthorized(request))
        return Results.Unauthorized();

    var active = await sampling.IsHostnameActiveAsync(hostname, ct);
    var favorites = active ? await sampling.GetFavoriteProcessNamesAsync(hostname, ct) : [];
    return Results.Ok(new ResourceSamplingStatusDto { Active = active, FavoriteProcessNames = favorites });
});

app.MapPost("/api/resource-sampling/report", async (ResourceSampleReportDto dto, LiveSamplingService sampling, HttpRequest request, CancellationToken ct) =>
{
    if (!IsAuthorized(request))
        return Results.Unauthorized();

    var ok = await sampling.ReportSampleAsync(dto, ct);
    return ok ? Results.Accepted() : Results.NotFound();
});

// --- Staff Access (Windows-verified email when RequireWindowsAuth — see StaffAccessGuard) ---

app.MapPost("/api/staff/groups/{groupId:int}/viewer/heartbeat", async (int groupId, ViewerHeartbeatRequestDto body, HttpContext ctx, StaffAccessGuard guard, RemoteAccessGroupService groups, LiveSamplingService sampling, CancellationToken ct) =>
{
    if (!await guard.EnsureWindowsAuthAsync(ctx))
        return Results.Empty;

    var email = guard.TryGetVerifiedEmail(ctx);
    if (email is null || !await groups.IsEmailInGroupAsync(email, groupId, ct))
        return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(body.ViewerId))
        return Results.BadRequest();

    await sampling.JoinOrHeartbeatAsync(groupId, body.ViewerId, email, ct);
    return Results.Ok();
});

app.MapPost("/api/staff/groups/{groupId:int}/viewer/leave", async (int groupId, ViewerHeartbeatRequestDto body, HttpContext ctx, LiveSamplingService sampling, CancellationToken ct) =>
{
    // Best-effort: sendBeacon on unload cannot reliably carry the auth cookie context for a strict check
    // in every browser, and there is nothing sensitive in "stop counting this viewerId" — if the caller
    // doesn't know a valid ViewerId for this group the leave is a harmless no-op.
    if (string.IsNullOrWhiteSpace(body.ViewerId))
        return Results.BadRequest();

    await sampling.LeaveAsync(groupId, body.ViewerId, ct);
    return Results.Ok();
});

app.MapGet("/api/staff/groups/{groupId:int}/metrics", async (int groupId, HttpContext ctx, StaffAccessGuard guard, RemoteAccessGroupService groups, LiveSamplingService sampling, CancellationToken ct) =>
{
    if (!await guard.EnsureWindowsAuthAsync(ctx))
        return Results.Empty;

    var email = guard.TryGetVerifiedEmail(ctx);
    if (email is null || !await groups.IsEmailInGroupAsync(email, groupId, ct))
        return Results.Unauthorized();

    var hostnames = await groups.GroupHostnamesAsync(groupId, ct);
    var metrics = await sampling.GetLatestMetricsAsync(hostnames, ct);
    return Results.Ok(metrics);
});

// --- Sessions page "Open" drill-down (ad-hoc, hostname-keyed fan-in — see LiveSamplingService) ---

app.MapGet("/api/sessions/drilldown/{hostname}", async (string hostname, SessionDrilldownService drilldown, CancellationToken ct) =>
{
    var dto = await drilldown.GetAsync(hostname, ct);
    return dto is null ? Results.NotFound() : Results.Ok(dto);
});

app.MapPost("/api/sessions/drilldown/{hostname}/viewer/heartbeat", async (string hostname, ViewerHeartbeatRequestDto body, LiveSamplingService sampling, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.ViewerId))
        return Results.BadRequest();

    await sampling.JoinOrHeartbeatHostAsync(hostname, body.ViewerId, ct);
    return Results.Ok();
});

app.MapPost("/api/sessions/drilldown/{hostname}/viewer/leave", async (string hostname, ViewerHeartbeatRequestDto body, LiveSamplingService sampling, CancellationToken ct) =>
{
    // Best-effort like the Staff viewer leave — a stale/unknown ViewerId is a harmless no-op.
    if (string.IsNullOrWhiteSpace(body.ViewerId))
        return Results.BadRequest();

    await sampling.LeaveHostAsync(hostname, body.ViewerId, ct);
    return Results.Ok();
});

app.MapGet("/api/health", () =>
{
    var asm = System.Reflection.Assembly.GetExecutingAssembly();
    var productVersion =
        asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? asm.GetName().Version?.ToString()
        ?? "unknown";
    return Results.Ok(new
    {
        status = "ok",
        service = "Heimdall",
        productVersion,
        machineName = Environment.MachineName,
        utc = DateTime.UtcNow
    });
});

app.Run();

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
builder.Services.Configure<EntraOptions>(builder.Configuration.GetSection(EntraOptions.SectionName));
builder.Services.AddSingleton<ActiveDirectoryStaffEmailResolver>();
builder.Services.AddSingleton<WindowsStaffIdentityService>();
builder.Services.AddSingleton<EntraGraphService>();
builder.Services.AddScoped<EntraTeamMembershipSyncService>();
builder.Services.AddScoped<StaffAccessGuard>();

var staffAccessOpts = builder.Configuration.GetSection("Heimdall:StaffAccess").Get<StaffAccessOptions>() ?? new();
if (staffAccessOpts.RequireWindowsAuth)
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
    builder.Services.AddAuthorization();
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<HeimdallDbConnectionResolver>();
builder.Services.AddRazorPages();
// Discovery SaveAll posts Edits[*] for every visible row when editing (6+ fields each). Default
// FormOptions.ValueCountLimit is 1024 and returns HTTP 400 once the catalog grows past ~150 rows.
// Approve previously used formaction inside SaveAll and hit the same limit — keep this raised, and
// keep per-row Approve/Set on small forms with edit fields disabled until Edit mode.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.ValueCountLimit = 16_384;
});
builder.Services.AddDbContext<HeimdallDbContext>((serviceProvider, options) =>
{
    var resolver = serviceProvider.GetRequiredService<HeimdallDbConnectionResolver>();
    options.UseSqlite(resolver.ResolveConnectionString());
});
builder.Services.AddScoped<IngestService>();
builder.Services.AddScoped<ConfigService>();
builder.Services.AddScoped<ProcessGroupService>();
builder.Services.AddScoped<ProcessCatalogService>();
builder.Services.AddScoped<AppListService>();
builder.Services.AddScoped<StatsQueryService>();
builder.Services.AddScoped<SocratizeQueryService>();
builder.Services.AddScoped<RemoteMachineService>();
builder.Services.AddScoped<MachineBookingService>();
builder.Services.AddScoped<FloodAccessGuard>();
builder.Services.AddScoped<TuflowRunService>();
builder.Services.AddScoped<RemoteAccessGroupService>();
builder.Services.AddScoped<LiveSamplingService>();
builder.Services.AddScoped<SessionDrilldownService>();
builder.Services.AddScoped<PublishedVersionService>();
builder.Services.AddSingleton<ClientPackReadinessService>();
builder.Services.AddScoped<ClientUpdateService>();
builder.Services.AddScoped<FleetDashboardService>();
builder.Services.AddScoped<MachineUtilisationService>();
builder.Services.AddHostedService<CatalogBackfillHostedService>();
builder.Services.AddHostedService<FleetSnapshotRetentionHostedService>();

var app = builder.Build();

foreach (var mode in new[] { HeimdallDatabaseMode.Live, HeimdallDatabaseMode.Sandbox })
{
    var conn = HeimdallDatabaseMode.GetConnectionStringForMode(app.Configuration, mode);
    var optionsBuilder = new DbContextOptionsBuilder<HeimdallDbContext>();
    optionsBuilder.UseSqlite(conn);
    await using var db = new HeimdallDbContext(optionsBuilder.Options);
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

app.MapGet("/database-mode", (HttpContext ctx, string mode, string? returnUrl) =>
{
    var normalized = HeimdallDatabaseMode.Normalize(mode);
    ctx.Response.Cookies.Append(HeimdallDatabaseMode.CookieName, normalized, new CookieOptions
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

app.MapGet("/ui-theme-custom", async (HttpContext ctx, int id, string? returnUrl, HeimdallDbContext db) =>
{
    var exists = await db.CustomThemes.AnyAsync(t => t.Id == id);
    var value = exists ? UiTheme.CustomToken(id) : UiTheme.Cosmic;
    ctx.Response.Cookies.Append(UiTheme.CookieName, value, new CookieOptions
    {
        Path = "/",
        MaxAge = TimeSpan.FromDays(365),
        SameSite = SameSiteMode.Lax,
        IsEssential = true
    });

    var dest = "/Theme";
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

app.MapGet("/api/tuflow/{hostname}/pending", async (string hostname, TuflowRunService runs, HttpRequest request) =>
{
    if (!IsAuthorized(request))
        return Results.Unauthorized();

    var dto = await runs.GetPendingAsync(hostname, request.HttpContext.RequestAborted);
    return Results.Ok(dto);
});

// --- Published client version (Client Version page baseline; set by Launch Control "Create client pack" — best-effort —
// or manually from the Client Version page) ---

app.MapPost("/api/admin/published-version", async (PublishedVersionDto dto, PublishedVersionService publishedVersion, HttpRequest request) =>
{
    if (!IsAuthorized(request))
        return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(dto.Version))
        return Results.BadRequest();

    await publishedVersion.SetAsync(dto.Version.Trim(), dto.SetBy, request.HttpContext.RequestAborted);
    return Results.Ok();
});

app.MapGet("/api/admin/client-pack/status", (ClientPackReadinessService pack, HttpRequest request) =>
{
    // Same network trust model as other admin Razor pages / published-version (ApiKey optional for browser; key accepted).
    _ = request;
    var status = pack.GetStatus();
    return Results.Json(new
    {
        status = status.Status.ToString(),
        message = status.Message,
        repoRoot = status.RepoRoot,
        packFolder = status.PackFolder,
        liveSourceFingerprint = status.LiveSourceFingerprint,
        packSourceFingerprint = status.PackSourceFingerprint,
        packProductVersion = status.PackProductVersion,
        zipSha256 = status.ZipSha256,
        canPack = status.CanPack,
        deployUnlocked = status.DeployUnlocked,
        apiInstallNote = status.ApiInstallNote,
        checkedUtc = status.CheckedUtc,
        isPacking = status.IsPacking,
        packingElapsed = status.PackingElapsedSeconds,
        packStage = status.PackStage,
        packStageLabel = status.PackStageLabel,
        lastPackExitCode = status.LastPackExitCode,
        lastPackMessage = status.LastPackMessage,
        lastPackLogTail = status.LastPackLogTail,
        lastPackLogPath = status.LastPackLogPath,
        lastPackFinishedUtc = status.LastPackFinishedUtc
    });
});

app.MapPost("/api/admin/client-pack/pack", (ClientPackReadinessService pack) =>
{
    var (started, message) = pack.TryStartPack();
    return started ? Results.Accepted(value: new { message }) : Results.BadRequest(new { message });
});

app.MapPost("/api/admin/client-pack/cancel", (ClientPackReadinessService pack) =>
{
    var (cancelled, message) = pack.TryCancelPack();
    return cancelled ? Results.Ok(new { message }) : Results.BadRequest(new { message });
});

app.MapGet("/api/agent/client-pack", (ClientPackReadinessService pack, HttpRequest request) =>
{
    if (!IsAuthorized(request))
        return Results.Unauthorized();

    var status = pack.GetStatus();
    if (status.Status is not ClientPackStatus.Ready and not ClientPackStatus.Stale and not ClientPackStatus.MissingPack)
    {
        // Allow download when folder exists even if stale (queued updates still need the zip).
    }

    try
    {
        var packFolder = pack.ResolvePackFolder();
        if (!File.Exists(Path.Combine(packFolder, "payload", "Heimdall.Agent.exe")))
            return Results.NotFound();

        var (zipPath, _) = pack.EnsureZip(packFolder);
        return Results.File(zipPath, "application/zip", "heimdall-client-agent.zip");
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
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

// --- Fleet util snapshots (always-on 30s for every known Machine) ---

app.MapPost("/api/fleet/snapshot", async (FleetSnapshotDto dto, FleetDashboardService fleet, HttpRequest request, CancellationToken ct) =>
{
    if (!IsAuthorized(request))
        return Results.Unauthorized();

    var ok = await fleet.IngestSnapshotAsync(dto, ct);
    return ok ? Results.Accepted() : Results.NotFound();
});

// --- Staff Access (Windows-verified email when RequireWindowsAuth — see StaffAccessGuard) ---

app.MapPost("/api/staff/groups/{groupId:int}/viewer/heartbeat", async (int groupId, ViewerHeartbeatRequestDto body, HttpContext ctx, StaffAccessGuard guard, RemoteAccessGroupService groups, LiveSamplingService sampling, CancellationToken ct) =>
{
    if (!await guard.EnsureWindowsAuthAsync(ctx))
        return Results.Empty;

    if (!await guard.CanAccessGroupAsync(ctx, groupId, groups, ct))
        return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(body.ViewerId))
        return Results.BadRequest();

    var email = guard.TryGetVerifiedEmail(ctx) ?? (guard.IsAdminPreviewActive(ctx) ? "admin-preview" : null);
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

    if (!await guard.CanAccessGroupAsync(ctx, groupId, groups, ct))
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

using System.Reflection;
using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Required for SCM registration; without this, Windows service start times out (Error 1053).
builder.Host.UseWindowsService(options => options.ServiceName = "HeimdallApi");

// Always-on rolling file logs under %ProgramData%\Heimdall\logs\api\ (known location if UI is down).
builder.Logging.AddProvider(new Heimdall.Api.Logging.RollingFileLoggerProvider());

builder.Services.Configure<StaffAccessOptions>(builder.Configuration.GetSection("Heimdall:StaffAccess"));
builder.Services.Configure<UsageAnalyticsOptions>(builder.Configuration.GetSection(UsageAnalyticsOptions.SectionName));
builder.Services.Configure<EntraOptions>(builder.Configuration.GetSection(EntraOptions.SectionName));
builder.Services.AddSingleton<EntraSecretStore>();
builder.Services.AddSingleton<IPostConfigureOptions<EntraOptions>, EntraOptionsPostConfigure>();
builder.Services.AddSingleton<ActiveDirectoryStaffEmailResolver>();
builder.Services.AddSingleton<WindowsStaffIdentityService>();
builder.Services.AddSingleton<EntraGraphService>();
builder.Services.AddScoped<DirectoryAuthSettingsService>();
builder.Services.AddScoped<EntraTeamMembershipSyncService>();
builder.Services.AddScoped<StaffAccessGuard>();
builder.Services.AddScoped<SiteUsageAnalyticsService>();

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
builder.Services.AddScoped<AdvertisedSoftwareService>();
builder.Services.AddScoped<AppListService>();
builder.Services.AddScoped<SpecReviewService>();
builder.Services.AddScoped<StatsQueryService>();
builder.Services.AddScoped<SocratizeQueryService>();
builder.Services.AddScoped<RemoteMachineService>();
builder.Services.AddScoped<MachineBookingService>();
builder.Services.AddScoped<MachineSoftwareCapabilityService>();
builder.Services.AddScoped<AccessAllowlistService>();
builder.Services.AddScoped<FloodAccessGuard>();
builder.Services.AddSingleton<FloodLiveHub>();
builder.Services.AddHostedService<FloodLiveBroadcastService>();
builder.Services.Configure<CodeMeterOptions>(
    builder.Configuration.GetSection(CodeMeterOptions.SectionName));
builder.Services.AddSingleton<CodeMeterLicenseHub>();
builder.Services.AddScoped<CodeMeterQueryService>();
builder.Services.AddHostedService<CodeMeterPollHostedService>();
builder.Services.AddSingleton<ApiBuildStamp>();
builder.Services.AddScoped<TuflowRunService>();
builder.Services.AddScoped<TuflowQueueService>();
builder.Services.Configure<TuflowBehaviourOptions>(
    builder.Configuration.GetSection(TuflowBehaviourOptions.SectionName));
builder.Services.AddScoped<TuflowBehaviourService>();
builder.Services.AddScoped<RemoteAccessGroupService>();
builder.Services.AddScoped<LiveSamplingService>();
builder.Services.AddScoped<SessionDrilldownService>();
builder.Services.AddScoped<PublishedVersionService>();
builder.Services.AddSingleton<ClientPackReadinessService>();
builder.Services.AddScoped<ClientUpdateService>();
builder.Services.AddScoped<StorageScanService>();
builder.Services.AddSingleton<DiagnosticBundleService>();
builder.Services.AddScoped<FleetDashboardService>();
builder.Services.AddScoped<MachineUtilisationService>();
builder.Services.AddScoped<FinanceQueryService>();
builder.Services.AddHostedService<CatalogBackfillHostedService>();
builder.Services.AddHostedService<FleetSnapshotRetentionHostedService>();
builder.Services.AddHostedService<SiteUsageRetentionHostedService>();
builder.Services.AddHostedService<ClientUpdateStuckHostedService>();
builder.Services.AddHostedService<SpecReviewHostedService>();
builder.Services.AddHostedService<StorageScanHostedService>();

var app = builder.Build();

foreach (var mode in new[] { HeimdallDatabaseMode.Live, HeimdallDatabaseMode.Sandbox })
{
    var conn = HeimdallDatabaseMode.GetConnectionStringForMode(app.Configuration, mode);
    var optionsBuilder = new DbContextOptionsBuilder<HeimdallDbContext>();
    optionsBuilder.UseSqlite(conn);
    await using var db = new HeimdallDbContext(optionsBuilder.Options);
    await SeedData.EnsureSeededAsync(db);
}

RdpProtocolHandler.EnsureRegistered(app.Logger);

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
app.UseMiddleware<SiteUsageAnalyticsMiddleware>();
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

    OpsFileLog.Write(
        "DatabaseMode",
        $"mode={normalized}",
        actor: ctx.User?.Identity?.Name);

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

// Fast disk-usage poll (~20s) — independent of ConfigRefreshSeconds so Scan is not stuck behind a 5 min refresh.
app.MapGet("/api/disk-usage/{hostname}/pending", async (string hostname, HeimdallDbContext db, HttpRequest request) =>
{
    if (!IsAuthorized(request))
        return Results.Unauthorized();

    var machine = await db.Machines.AsNoTracking()
        .FirstOrDefaultAsync(m => m.Hostname == hostname, request.HttpContext.RequestAborted);
    DiskUsageScanRequestDto? pending = null;
    if (!string.IsNullOrWhiteSpace(machine?.PendingDiskUsageScanJson))
    {
        try
        {
            pending = JsonSerializer.Deserialize<DiskUsageScanRequestDto>(machine.PendingDiskUsageScanJson);
        }
        catch
        {
            /* ignore corrupt queue */
        }
    }

    return Results.Ok(new DiskUsagePendingDto { PendingDiskUsageScan = pending });
});

app.MapPost("/api/disk-usage/{hostname}/progress", async (
    string hostname,
    DiskUsageScanProgressDto dto,
    IngestService ingest,
    HttpRequest request) =>
{
    if (!IsAuthorized(request))
        return Results.Unauthorized();

    await ingest.ApplyDiskUsageScanProgressAsync(hostname, dto, request.HttpContext.RequestAborted);
    return Results.Accepted();
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

app.MapPost("/api/admin/client-pack/pack", (ClientPackReadinessService pack, HttpRequest request) =>
{
    var force = string.Equals(request.Query["force"], "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(request.Query["force"], "true", StringComparison.OrdinalIgnoreCase);
    var (started, message, outcome) = pack.TryStartPack(force);
    if (started)
        return Results.Accepted(value: new { message, outcome });
    if (outcome == "already-ready")
        return Results.Ok(new { message, outcome, skipped = true });
    return Results.BadRequest(new { message, outcome });
});

app.MapPost("/api/admin/client-pack/refresh", (ClientPackReadinessService pack) =>
{
    var status = pack.RefreshFromDisk();
    return Results.Ok(new
    {
        message = status.Message,
        status = status.Status.ToString(),
        deployUnlocked = status.DeployUnlocked,
        packFolder = status.PackFolder,
        packProductVersion = status.PackProductVersion,
        liveSourceFingerprint = status.LiveSourceFingerprint,
        packSourceFingerprint = status.PackSourceFingerprint
    });
});

app.MapPost("/api/admin/client-pack/cancel", (ClientPackReadinessService pack) =>
{
    var (cancelled, message) = pack.TryCancelPack();
    return cancelled ? Results.Ok(new { message }) : Results.BadRequest(new { message });
});

// Queue DepositClientPack: agents download the Ready pack to
// C:\Temp\Heimdall-Client-v{version}-{yyyyMMdd-HHmmss} for manual Install.lnk
// (does not silent-install / replace HeimdallAgent).
app.MapPost("/api/admin/client-pack/deposit", async (
    DepositClientPackRequestDto? body,
    ClientUpdateService clientUpdates,
    HttpRequest request,
    CancellationToken ct) =>
{
    if (!IsAuthorized(request))
        return Results.Unauthorized();

    var hostnames = body?.Hostnames ?? [];
    if (hostnames.Count == 0)
        return Results.BadRequest(new DepositClientPackResponseDto
        {
            Queued = 0,
            Skipped = 0,
            Errors = 0,
            Message = "Provide at least one hostname in hostnames[].",
            Results = []
        });

    var result = await clientUpdates.QueueDepositClientPackAsync(hostnames, ct);
    if (result.Queued == 0 && result.Errors > 0 && result.Skipped == 0)
        return Results.BadRequest(result);
    return Results.Ok(result);
});

app.MapGet("/api/agent/client-pack", (ClientPackReadinessService pack, HttpRequest request, HttpResponse response) =>
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
        var version = status.PackProductVersion
            ?? ClientPackFingerprint.TryReadProductVersion(packFolder)
            ?? "unknown";
        var safeVer = Heimdall.Shared.ClientPackFolderNames.SanitizeVersion(version);
        response.Headers["X-Heimdall-Client-Version"] = safeVer;
        return Results.File(zipPath, "application/zip", $"heimdall-client-v{safeVer}.zip");
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

// --- Flood Live shared stream (SSE) — one rebuild fans out to all viewers ---
app.MapGet("/api/flood/live/stream", async (
    HttpContext ctx,
    FloodAccessGuard flood,
    FloodLiveHub hub,
    CancellationToken ct) =>
{
    if (flood.ForbidIfLiveDenied(ctx) is not null)
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    ctx.Response.Headers.CacheControl = "no-cache, no-store";
    ctx.Response.Headers.Connection = "keep-alive";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    ctx.Response.ContentType = "text/event-stream";

    var jsonOpts = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    async Task WriteEventAsync(FloodLivePayload payload, CancellationToken token)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload, jsonOpts);
        await ctx.Response.WriteAsync("id: " + payload.Version + "\n", token);
        await ctx.Response.WriteAsync("event: live\n", token);
        await ctx.Response.WriteAsync("data: " + json + "\n\n", token);
        await ctx.Response.Body.FlushAsync(token);
    }

    var reader = hub.Subscribe(out var current);
    try
    {
        if (current.Version > 0)
            await WriteEventAsync(current, ct);

        while (!ct.IsCancellationRequested)
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                while (await reader.WaitToReadAsync(waitCts.Token))
                {
                    while (reader.TryRead(out var payload))
                        await WriteEventAsync(payload, ct);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await ctx.Response.WriteAsync(": ping\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
    }
    finally
    {
        hub.Unsubscribe(reader);
    }

    return Results.Empty;
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

// --- First-party site usage (browser beacons; no agent API key) ---

app.MapPost("/api/usage/beacon", async (HttpContext ctx, SiteUsageAnalyticsService usage, CancellationToken ct) =>
{
    if (!usage.TryAcceptBeacon(ctx, out var reject))
    {
        return reject switch
        {
            "disabled" => Results.NoContent(),
            "rate" => Results.StatusCode(StatusCodes.Status429TooManyRequests),
            _ => Results.Forbid()
        };
    }

    var maxBytes = Math.Clamp(usage.Options.BeaconMaxBodyBytes, 1024, 64_000);
    if (ctx.Request.ContentLength is long len && len > maxBytes)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    UsageBeaconPayload? payload;
    try
    {
        ctx.Request.EnableBuffering();
        using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
        // Cap read via ContentLength check above; also guard unbounded streams.
        var json = await reader.ReadToEndAsync(ct);
        if (json.Length > maxBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        payload = string.IsNullOrWhiteSpace(json)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<UsageBeaconPayload>(json);
    }
    catch
    {
        return Results.BadRequest();
    }

    if (payload?.Events is null || payload.Events.Count == 0)
        return Results.NoContent();

    await usage.IngestBeaconAsync(ctx, payload, ct);
    return Results.NoContent();
}).DisableAntiforgery();

app.Run();

using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Heimdall.Api.Services;

public sealed class SiteUsageAnalyticsService(
    HeimdallDbContext db,
    IOptions<UsageAnalyticsOptions> options,
    WindowsStaffIdentityService windowsIdentity,
    ILogger<SiteUsageAnalyticsService> logger)
{
    public const string SessionCookieName = "hd_usage_sid";
    public const string PageViewItemKey = "Heimdall.Usage.PageViewId";
    public const string SessionItemKey = "Heimdall.Usage.SessionId";

    private static readonly ConcurrentDictionary<string, RateBucket> RateBuckets = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SecretQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "apikey", "api_key", "token", "access_token", "password", "pwd", "secret",
        "client_secret", "authorization", "auth", "sig", "signature", "x-heimdall-key"
    };

    public UsageAnalyticsOptions Options => options.Value;

    public bool IsEnabled => Options.Enabled;

    public static bool ShouldTrackPath(PathString path)
    {
        var p = path.Value ?? "";
        if (p.Length == 0)
            return false;

        if (p.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/api", StringComparison.OrdinalIgnoreCase))
            return false;

        if (p.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/_vs/", StringComparison.OrdinalIgnoreCase))
            return false;

        if (p.Equals("/api/health", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/database-mode", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/ui-theme", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/ui-gold", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/Error", StringComparison.OrdinalIgnoreCase))
            return false;

        // Skip paths that look like static assets (extension present).
        var lastSlash = p.LastIndexOf('/');
        var leaf = lastSlash >= 0 ? p[(lastSlash + 1)..] : p;
        if (leaf.Contains('.', StringComparison.Ordinal)
            && !leaf.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public string EnsureSessionId(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(SessionItemKey, out var existing) && existing is string s && s.Length > 0)
            return s;

        if (ctx.Request.Cookies.TryGetValue(SessionCookieName, out var cookie)
            && !string.IsNullOrWhiteSpace(cookie)
            && cookie.Length <= 64)
        {
            var trimmed = cookie.Trim();
            ctx.Items[SessionItemKey] = trimmed;
            return trimmed;
        }

        var created = Guid.NewGuid().ToString("N");
        ctx.Response.Cookies.Append(SessionCookieName, created, new CookieOptions
        {
            Path = "/",
            MaxAge = TimeSpan.FromDays(180),
            SameSite = SameSiteMode.Lax,
            HttpOnly = false,
            IsEssential = true
        });
        ctx.Items[SessionItemKey] = created;
        return created;
    }

    public string ResolveUserName(HttpContext ctx, string sessionId)
    {
        var windows = windowsIdentity.GetWindowsPrincipalName(ctx);
        if (!string.IsNullOrWhiteSpace(windows))
            return windows.Trim();

        if (ctx.User?.Identity?.IsAuthenticated == true
            && !string.IsNullOrWhiteSpace(ctx.User.Identity.Name))
            return ctx.User.Identity.Name.Trim();

        var staffEmail = StaffAuthService.TryGetEmail(ctx);
        if (!string.IsNullOrWhiteSpace(staffEmail))
            return staffEmail.Trim();

        var shortSid = sessionId.Length >= 8 ? sessionId[..8] : sessionId;
        return "anonymous:" + shortSid;
    }

    public static string SanitizeQuery(QueryString query)
    {
        if (!query.HasValue)
            return "";

        var raw = query.Value!;
        if (raw.StartsWith('?'))
            raw = raw[1..];
        if (raw.Length == 0)
            return "";

        var parts = new List<string>();
        foreach (var segment in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = segment.IndexOf('=');
            var key = eq >= 0 ? segment[..eq] : segment;
            var decodedKey = Uri.UnescapeDataString(key);
            if (SecretQueryKeys.Contains(decodedKey))
            {
                parts.Add(key + "=***");
                continue;
            }

            parts.Add(segment);
        }

        var joined = string.Join('&', parts);
        return joined.Length > 500 ? joined[..500] : joined;
    }

    public static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    public async Task RecordPageViewAsync(HttpContext ctx, CancellationToken ct = default)
    {
        if (!IsEnabled || !ShouldTrackPath(ctx.Request.Path))
            return;

        try
        {
            var sessionId = EnsureSessionId(ctx);
            var pageViewId = Guid.NewGuid().ToString("N");
            ctx.Items[PageViewItemKey] = pageViewId;

            var path = Truncate(ctx.Request.Path.Value ?? "/", 400);
            var query = SanitizeQuery(ctx.Request.QueryString);
            var userName = ResolveUserName(ctx, sessionId);
            var ua = Truncate(ctx.Request.Headers.UserAgent.ToString(), 240);
            var referrer = Truncate(ctx.Request.Headers.Referer.ToString(), 400);
            string? ip = null;
            if (Options.LogClientIp)
                ip = Truncate(ctx.Connection.RemoteIpAddress?.ToString(), 64);

            db.SiteUsageEvents.Add(new SiteUsageEvent
            {
                OccurredUtc = DateTimeOffset.UtcNow,
                EventType = "pageview",
                Path = string.IsNullOrEmpty(path) ? "/" : path,
                Query = string.IsNullOrEmpty(query) ? null : query,
                UserName = userName,
                SessionId = sessionId,
                PageViewId = pageViewId,
                IpAddress = string.IsNullOrEmpty(ip) ? null : ip,
                UserAgent = string.IsNullOrEmpty(ua) ? null : ua,
                Referrer = string.IsNullOrEmpty(referrer) ? null : referrer
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Site usage pageview log failed.");
        }
    }

    public bool TryAcceptBeacon(HttpContext ctx, out string? rejectReason)
    {
        rejectReason = null;
        if (!IsEnabled)
        {
            rejectReason = "disabled";
            return false;
        }

        // Same-origin preference: allow missing Origin (sendBeacon / older) when Referer host matches.
        var host = ctx.Request.Host.Host;
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin)
            && Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            && !string.Equals(originUri.Host, host, StringComparison.OrdinalIgnoreCase))
        {
            rejectReason = "origin";
            return false;
        }

        var referer = ctx.Request.Headers.Referer.ToString();
        if (string.IsNullOrEmpty(origin)
            && !string.IsNullOrEmpty(referer)
            && Uri.TryCreate(referer, UriKind.Absolute, out var refUri)
            && !string.Equals(refUri.Host, host, StringComparison.OrdinalIgnoreCase))
        {
            rejectReason = "referer";
            return false;
        }

        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var max = Math.Max(10, Options.BeaconMaxEventsPerMinute);
        var bucket = RateBuckets.GetOrAdd(ip, _ => new RateBucket());
        if (!bucket.TryConsume(max))
        {
            rejectReason = "rate";
            return false;
        }

        return true;
    }

    public async Task<int> IngestBeaconAsync(HttpContext ctx, UsageBeaconPayload payload, CancellationToken ct = default)
    {
        if (!IsEnabled || payload.Events is null || payload.Events.Count == 0)
            return 0;

        var sessionId = Truncate(
            !string.IsNullOrWhiteSpace(payload.SessionId)
                ? payload.SessionId
                : EnsureSessionId(ctx),
            64);
        if (string.IsNullOrEmpty(sessionId))
            sessionId = EnsureSessionId(ctx);

        var userName = ResolveUserName(ctx, sessionId);
        string? ip = null;
        if (Options.LogClientIp)
            ip = Truncate(ctx.Connection.RemoteIpAddress?.ToString(), 64);
        var ua = Truncate(ctx.Request.Headers.UserAgent.ToString(), 240);
        var now = DateTimeOffset.UtcNow;
        var accepted = 0;
        const int maxEvents = 40;

        foreach (var evt in payload.Events.Take(maxEvents))
        {
            if (evt is null)
                continue;

            var type = (evt.Type ?? "").Trim().ToLowerInvariant();
            if (type is not ("duration" or "click" or "pageview"))
                continue;

            var path = Truncate(evt.Path, 400);
            if (string.IsNullOrEmpty(path))
                path = "/";
            if (!ShouldTrackPath(path))
                continue;

            var pageViewId = Truncate(evt.PageViewId ?? payload.PageViewId, 64);
            if (string.IsNullOrEmpty(pageViewId))
                pageViewId = null;

            var row = new SiteUsageEvent
            {
                OccurredUtc = now,
                EventType = type,
                Path = path,
                Query = string.IsNullOrEmpty(Truncate(evt.Query, 500)) ? null : Truncate(evt.Query, 500),
                UserName = userName,
                SessionId = sessionId,
                PageViewId = pageViewId,
                IpAddress = string.IsNullOrEmpty(ip) ? null : ip,
                UserAgent = string.IsNullOrEmpty(ua) ? null : ua
            };

            if (type == "duration")
            {
                var secs = evt.DurationSeconds ?? 0;
                if (secs < 0)
                    secs = 0;
                if (secs > 86_400 * 7)
                    secs = 86_400 * 7;

                // Upsert: one duration row per page view (heartbeats update, not insert).
                if (!string.IsNullOrEmpty(pageViewId))
                {
                    var existing = await db.SiteUsageEvents
                        .FirstOrDefaultAsync(e => e.EventType == "duration" && e.PageViewId == pageViewId, ct);
                    if (existing is not null)
                    {
                        if (secs >= (existing.DurationSeconds ?? 0))
                        {
                            existing.DurationSeconds = secs;
                            existing.OccurredUtc = now;
                            existing.UserName = userName;
                        }
                        accepted++;
                        continue;
                    }
                }

                row.DurationSeconds = secs;
            }
            else if (type == "click")
            {
                row.LinkHref = NullIfEmpty(Truncate(evt.Href, 500));
                row.LinkText = NullIfEmpty(Truncate(evt.Text, 120));
                if (row.LinkHref is null && row.LinkText is null)
                    continue;
            }

            db.SiteUsageEvents.Add(row);
            accepted++;
        }

        if (accepted > 0)
            await db.SaveChangesAsync(ct);

        return accepted;
    }

    public async Task<int> PurgeOlderThanAsync(int retentionDays, CancellationToken ct = default)
    {
        var days = Math.Max(1, retentionDays);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        return await db.SiteUsageEvents
            .Where(e => e.OccurredUtc < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private sealed class RateBucket
    {
        private readonly object _gate = new();
        private DateTime _windowStartUtc = DateTime.UtcNow;
        private int _count;

        public bool TryConsume(int maxPerMinute)
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if ((now - _windowStartUtc).TotalMinutes >= 1)
                {
                    _windowStartUtc = now;
                    _count = 0;
                }

                if (_count >= maxPerMinute)
                    return false;
                _count++;
                return true;
            }
        }
    }
}

public sealed class UsageBeaconPayload
{
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("pageViewId")]
    public string? PageViewId { get; set; }

    [JsonPropertyName("events")]
    public List<UsageBeaconEventDto>? Events { get; set; }
}

public sealed class UsageBeaconEventDto
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("query")]
    public string? Query { get; set; }

    [JsonPropertyName("pageViewId")]
    public string? PageViewId { get; set; }

    [JsonPropertyName("durationSeconds")]
    public int? DurationSeconds { get; set; }

    [JsonPropertyName("href")]
    public string? Href { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

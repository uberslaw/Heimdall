using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Heimdall.Api.Services;

/// <summary>Runs cmu32 --list-network and parses Used= / checkout lines for TUFLOW products.</summary>
public sealed class CodeMeterQueryService(
    IOptions<CodeMeterOptions> options,
    ILogger<CodeMeterQueryService> logger)
{
    private static readonly Regex UsedRegex = new(@"Used=\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HandleRegex = new(@"Handle:\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UserRegex = new(@"User:\s*(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ClientAddressRegex = new(@"Client address:\s*(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<CodeMeterLicenseSnapshot> QueryAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var sw = Stopwatch.StartNew();
        var serverNotes = new List<string>();

        var hpc = await QueryProductAsync(opts.Hpc, "HPC", serverNotes, ct);
        var classic = await QueryProductAsync(opts.Classic, "Classic", serverNotes, ct);

        sw.Stop();
        var partial = hpc.Partial || classic.Partial || serverNotes.Exists(n => n.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || n.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || n.Contains("error", StringComparison.OrdinalIgnoreCase));

        return new CodeMeterLicenseSnapshot(
            QueriedAtUtc: DateTimeOffset.UtcNow,
            PollDurationMs: sw.Elapsed.TotalMilliseconds,
            Hpc: hpc,
            Classic: classic,
            ServerNotes: serverNotes,
            Partial: partial,
            Available: hpc.Ok || classic.Ok);
    }

    private async Task<CodeMeterProductSnapshot> QueryProductAsync(
        CodeMeterProductOptions product,
        string label,
        List<string> serverNotes,
        CancellationToken ct)
    {
        if (product.Servers.Count == 0)
        {
            serverNotes.Add($"{label}: no servers configured");
            return CodeMeterProductSnapshot.Empty(product.ProductCode, product.TotalLicenses);
        }

        var opts = options.Value;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opts.QueryTimeoutSeconds, 5, 300));
        var perFqdnUsed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // HA: keep checkouts per license-server FQDN; never sum the same client across redundant servers.
        var perFqdnCheckouts = new Dictionary<string, List<CodeMeterCheckout>>(StringComparer.OrdinalIgnoreCase);
        var anyOk = false;
        var anyFail = false;

        foreach (var server in product.Servers)
        {
            if (string.IsNullOrWhiteSpace(server.Fqdn) || string.IsNullOrWhiteSpace(server.Serial))
                continue;

            ct.ThrowIfCancellationRequested();
            var (ok, lines, err) = await RunCmuAsync(server.Fqdn, server.Serial, product.ProductCode, timeout, ct);
            var shortHost = ShortHost(server.Fqdn);
            if (!ok || lines is null)
            {
                anyFail = true;
                serverNotes.Add($"{label} {shortHost}/{server.Serial}: {err ?? "failed"}");
                continue;
            }

            anyOk = true;
            var used = SumUsed(lines);
            if (used >= 0)
            {
                // Multiple serials on the same FQDN are one server view — sum within FQDN.
                perFqdnUsed.TryGetValue(server.Fqdn, out var prev);
                perFqdnUsed[server.Fqdn] = prev + used;
            }

            if (!perFqdnCheckouts.TryGetValue(server.Fqdn, out var bucket))
            {
                bucket = [];
                perFqdnCheckouts[server.Fqdn] = bucket;
            }

            bucket.AddRange(ParseCheckouts(lines));
            serverNotes.Add($"{label} {shortHost}/{server.Serial}: ok");
        }

        // HA: never sum FQDNs — take the max Used across server views.
        int? poolUsed = perFqdnUsed.Count == 0 ? null : perFqdnUsed.Values.Max();
        var total = Math.Max(0, product.TotalLicenses);
        int? available = poolUsed is null ? null : Math.Max(0, total - poolUsed.Value);

        // Per FQDN: dedupe handles, then for each client IP take MAX seat count across FQDNs
        // (same client mirrored on az + bne must not become 2 seats).
        var perFqdnDeduped = perFqdnCheckouts.ToDictionary(
            kv => kv.Key,
            kv => DedupCheckouts(kv.Value).ToList(),
            StringComparer.OrdinalIgnoreCase);
        var (canonical, seatsByIp) = MergeHaCheckouts(perFqdnDeduped);

        return new CodeMeterProductSnapshot(
            product.ProductCode,
            total,
            poolUsed,
            available,
            canonical,
            seatsByIp,
            Partial: anyFail && anyOk,
            anyOk);
    }

    /// <summary>
    /// For each client IP, seat count = max(count on any single FQDN). Evidence rows come from that FQDN.
    /// Checkouts with no client IP are taken from the FQDN with the most such rows (rare).
    /// </summary>
    internal static (IReadOnlyList<CodeMeterCheckout> Canonical, IReadOnlyDictionary<string, int> SeatsByIp)
        MergeHaCheckouts(IReadOnlyDictionary<string, List<CodeMeterCheckout>> perFqdn)
    {
        var seatsByIp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var evidenceByIp = new Dictionary<string, List<CodeMeterCheckout>>(StringComparer.OrdinalIgnoreCase);
        List<CodeMeterCheckout>? bestNoIp = null;

        foreach (var list in perFqdn.Values)
        {
            var withIp = list.Where(c => !string.IsNullOrWhiteSpace(c.ClientAddress))
                .GroupBy(c => c.ClientAddress!, StringComparer.OrdinalIgnoreCase);
            foreach (var g in withIp)
            {
                var rows = g.ToList();
                var n = rows.Count;
                if (!seatsByIp.TryGetValue(g.Key, out var cur) || n > cur)
                {
                    seatsByIp[g.Key] = n;
                    evidenceByIp[g.Key] = rows;
                }
            }

            var noIp = list.Where(c => string.IsNullOrWhiteSpace(c.ClientAddress)).ToList();
            if (noIp.Count > 0 && (bestNoIp is null || noIp.Count > bestNoIp.Count))
                bestNoIp = noIp;
        }

        var canonical = evidenceByIp.Values.SelectMany(x => x).ToList();
        if (bestNoIp is { Count: > 0 })
            canonical.AddRange(bestNoIp);

        return (canonical, seatsByIp);
    }

    private async Task<(bool Ok, string[]? Lines, string? Error)> RunCmuAsync(
        string fqdn,
        string serial,
        int productCode,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var path = ResolveCmu32Path(options.Value.Cmu32Path);
        if (!OperatingSystem.IsWindows())
            return (false, null, "Windows only");
        if (path is null)
            return (false, null, "cmu32 not found");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                ArgumentList =
                {
                    "--list-network",
                    "--server", fqdn,
                    "--serial", serial,
                    "--productcode", productCode.ToString()
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            if (!proc.Start())
                return (false, null, "failed to start");

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, null, "timeout");
            }

            var text = stdout.ToString();
            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(text))
            {
                var err = stderr.ToString().Trim();
                return (false, null, string.IsNullOrEmpty(err) ? $"exit {proc.ExitCode}" : err);
            }

            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            return (true, lines, null);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "cmu32 query failed for {Fqdn} serial {Serial} product {Product}", fqdn, serial, productCode);
            return (false, null, ex.Message);
        }
    }

    internal static int SumUsed(IEnumerable<string> lines)
    {
        var total = 0;
        var found = false;
        foreach (var line in lines)
        {
            var m = UsedRegex.Match(line);
            if (!m.Success) continue;
            if (int.TryParse(m.Groups[1].Value, out var n))
            {
                total += n;
                found = true;
            }
        }

        return found ? total : -1;
    }

    internal static List<CodeMeterCheckout> ParseCheckouts(IEnumerable<string> lines)
    {
        var list = new List<CodeMeterCheckout>();
        string? handle = null;
        string? user = null;
        foreach (var line in lines)
        {
            var hm = HandleRegex.Match(line);
            if (hm.Success)
            {
                handle = hm.Groups[1].Value;
                var um = UserRegex.Match(line);
                user = um.Success ? um.Groups[1].Value : null;
            }

            var cm = ClientAddressRegex.Match(line);
            if (cm.Success && handle is not null)
            {
                list.Add(new CodeMeterCheckout(handle, user, NormalizeIp(cm.Groups[1].Value)));
                handle = null;
                user = null;
            }
        }

        return list;
    }

    internal static IReadOnlyList<CodeMeterCheckout> DedupCheckouts(IEnumerable<CodeMeterCheckout> checkouts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<CodeMeterCheckout>();
        foreach (var c in checkouts)
        {
            var key = $"{c.Handle}|{c.ClientAddress}|{c.User}";
            if (!seen.Add(key)) continue;
            list.Add(c);
        }

        return list;
    }

    public static string? NormalizeIp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var zone = s.IndexOf('%');
        if (zone >= 0) s = s[..zone];
        if (s.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
            s = s[7..];
        if (IPAddress.TryParse(s, out var ip))
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? ip.ToString()
                : ip.ToString();
        return s;
    }

    internal static string? ResolveCmu32Path(string? configured)
    {
        foreach (var candidate in new[]
                 {
                     configured,
                     @"C:\Program Files\CodeMeter\Runtime\bin\cmu32.exe",
                     @"C:\Program Files (x86)\CodeMeter\Runtime\bin\cmu32.exe"
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string ShortHost(string fqdn)
    {
        var dot = fqdn.IndexOf('.');
        return dot > 0 ? fqdn[..dot] : fqdn;
    }
}

public sealed record CodeMeterCheckout(string Handle, string? User, string? ClientAddress);

public sealed record CodeMeterProductSnapshot(
    int ProductCode,
    int TotalLicenses,
    int? PoolUsed,
    int? PoolAvailable,
    IReadOnlyList<CodeMeterCheckout> Checkouts,
    IReadOnlyDictionary<string, int> SeatsByClientIp,
    bool Partial,
    bool Ok)
{
    public static CodeMeterProductSnapshot Empty(int productCode, int total) =>
        new(productCode, total, null, null, [], new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), false, false);
}

public sealed record CodeMeterLicenseSnapshot(
    DateTimeOffset QueriedAtUtc,
    double PollDurationMs,
    CodeMeterProductSnapshot Hpc,
    CodeMeterProductSnapshot Classic,
    IReadOnlyList<string> ServerNotes,
    bool Partial,
    bool Available)
{
    public static CodeMeterLicenseSnapshot Disabled { get; } = new(
        DateTimeOffset.UnixEpoch,
        0,
        CodeMeterProductSnapshot.Empty(926, 32),
        CodeMeterProductSnapshot.Empty(920, 32),
        ["disabled"],
        false,
        false);

    /// <summary>Seats for a machine LastIp only — never by username. Count is HA-safe (max across servers).</summary>
    public (int HpcSeats, int ClassicSeats) SeatsForIp(string? lastIp)
    {
        var ip = CodeMeterQueryService.NormalizeIp(lastIp);
        if (ip is null) return (0, 0);
        Hpc.SeatsByClientIp.TryGetValue(ip, out var hpc);
        Classic.SeatsByClientIp.TryGetValue(ip, out var classic);
        return (hpc, classic);
    }

    /// <summary>
    /// Display seats for one client IP: max(HPC, Classic).
    /// TUFLOW GPU/HPC checkouts typically hold both products; summing would double-count the same run.
    /// </summary>
    public int EffectiveSeatsForIp(string? lastIp)
    {
        var (hpc, classic) = SeatsForIp(lastIp);
        return Math.Max(hpc, classic);
    }

    /// <summary>CodeMeter User @ Client address lines for tooltips (proof of attribution).</summary>
    public string SeatDetailForIp(string? lastIp, bool hpc)
    {
        var ip = CodeMeterQueryService.NormalizeIp(lastIp);
        if (ip is null) return "";
        var list = (hpc ? Hpc.Checkouts : Classic.Checkouts)
            .Where(c => IpsMatch(c.ClientAddress, ip))
            .ToList();
        if (list.Count == 0) return "";
        return string.Join("; ",
            list.Select(c =>
            {
                var user = string.IsNullOrWhiteSpace(c.User) ? "?" : c.User;
                return $"{user} @ {c.ClientAddress}";
            }).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public (int Hpc, int Classic) UnmatchedSeats(IEnumerable<string?> knownIps)
    {
        var set = FloodIpSet(knownIps);

        var hpc = Hpc.SeatsByClientIp
            .Where(kv => !set.Contains(kv.Key))
            .Sum(kv => kv.Value);
        var classic = Classic.SeatsByClientIp
            .Where(kv => !set.Contains(kv.Key))
            .Sum(kv => kv.Value);
        // Checkouts with no client IP cannot be attributed to a Flood machine.
        hpc += Hpc.Checkouts.Count(c => string.IsNullOrWhiteSpace(c.ClientAddress));
        classic += Classic.Checkouts.Count(c => string.IsNullOrWhiteSpace(c.ClientAddress));
        return (hpc, classic);
    }

    /// <summary>
    /// Outside-Flood seat count without double-counting HPC+Classic on the same client IP.
    /// Per unmatched IP (and for orphan checkouts with no IP): max(HPC, Classic), then sum.
    /// </summary>
    public int UnmatchedEffectiveSeats(IEnumerable<string?> knownIps)
    {
        var set = FloodIpSet(knownIps);
        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Hpc.SeatsByClientIp)
        {
            if (!set.Contains(kv.Key)) ips.Add(kv.Key);
        }

        foreach (var kv in Classic.SeatsByClientIp)
        {
            if (!set.Contains(kv.Key)) ips.Add(kv.Key);
        }

        var n = 0;
        foreach (var ip in ips)
        {
            Hpc.SeatsByClientIp.TryGetValue(ip, out var h);
            Classic.SeatsByClientIp.TryGetValue(ip, out var c);
            n += Math.Max(h, c);
        }

        var orphanH = Hpc.Checkouts.Count(c => string.IsNullOrWhiteSpace(c.ClientAddress));
        var orphanC = Classic.Checkouts.Count(c => string.IsNullOrWhiteSpace(c.ClientAddress));
        return n + Math.Max(orphanH, orphanC);
    }

    /// <summary>
    /// Tooltip for seats checked out outside Flood enrollment.
    /// Uses CodeMeter User + Client address; enriches with Heimdall hostname/office when LastIp matches.
    /// Lines include seat counts (e.g. "HPC ×3: user @ host").
    /// </summary>
    public string? UnmatchedSeatDetail(
        IEnumerable<string?> floodIps,
        IReadOnlyDictionary<string, CodeMeterIpHint> ipHints,
        int maxLines = 24)
    {
        var set = FloodIpSet(floodIps);
        var lines = new List<string>();

        void AddProduct(string product, CodeMeterProductSnapshot snap)
        {
            var unmatched = snap.Checkouts
                .Where(c =>
                {
                    var ip = CodeMeterQueryService.NormalizeIp(c.ClientAddress);
                    return ip is null || !set.Contains(ip);
                })
                .GroupBy(
                    c => FormatUnmatchedKey(product, c, ipHints),
                    StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var n = g.Count();
                    return n <= 1 ? g.Key : $"{product} ×{n}: {StripProductPrefix(g.Key, product)}";
                })
                .OrderByDescending(s =>
                {
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"×(\d+)");
                    return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 1;
                })
                .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            lines.AddRange(unmatched);
        }

        AddProduct("HPC", Hpc);
        AddProduct("Classic", Classic);
        if (lines.Count == 0) return null;

        var (unHpc, unClassic) = UnmatchedSeats(floodIps);
        var effective = UnmatchedEffectiveSeats(floodIps);
        lines.Insert(0,
            $"Outside Flood — {effective} seats (max HPC/Classic per IP; not additive). CodeMeter products: HPC {unHpc}, Classic {unClassic}");

        if (lines.Count <= maxLines + 1)
            return string.Join("\n", lines);

        var keep = lines.Take(maxLines + 1).ToList();
        var extra = lines.Count - keep.Count;
        return string.Join("\n", keep) + $"\n…and {extra} more";
    }

    private static string StripProductPrefix(string key, string product)
    {
        var prefix = product + ": ";
        return key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? key[prefix.Length..]
            : key;
    }

    private static string FormatUnmatchedKey(
        string product,
        CodeMeterCheckout c,
        IReadOnlyDictionary<string, CodeMeterIpHint> ipHints) =>
        $"{product}: {FormatUnmatchedWhoWhere(c, ipHints)}";

    private static HashSet<string> FloodIpSet(IEnumerable<string?> knownIps)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ip in knownIps)
        {
            var n = CodeMeterQueryService.NormalizeIp(ip);
            if (n is not null) set.Add(n);
        }
        return set;
    }

    private static string FormatUnmatchedWhoWhere(
        CodeMeterCheckout c,
        IReadOnlyDictionary<string, CodeMeterIpHint> ipHints)
    {
        var cmUser = string.IsNullOrWhiteSpace(c.User) ? "?" : c.User.Trim();
        var bare = cmUser.Contains('\\') ? cmUser[(cmUser.LastIndexOf('\\') + 1)..] : cmUser;
        var ip = CodeMeterQueryService.NormalizeIp(c.ClientAddress);

        string hostPart;
        if (ip is null)
        {
            hostPart = "(no client IP)";
        }
        else if (ipHints.TryGetValue(ip, out var hint))
        {
            var name = string.IsNullOrWhiteSpace(hint.FriendlyName) ? hint.Hostname : hint.FriendlyName!;
            hostPart = string.IsNullOrWhiteSpace(hint.Office)
                ? $"{name} ({ip})"
                : $"{name} ({ip}, {hint.Office})";
        }
        else
        {
            hostPart = ip;
        }

        return $"{bare} @ {hostPart}";
    }

    private static string FormatUnmatchedLine(
        string product,
        CodeMeterCheckout c,
        IReadOnlyDictionary<string, CodeMeterIpHint> ipHints) =>
        $"{product}: {FormatUnmatchedWhoWhere(c, ipHints)}";

    private static bool IpsMatch(string? a, string b) =>
        a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Heimdall estate hint for a CodeMeter client IP (hostname enrichment).</summary>
public sealed record CodeMeterIpHint(string Hostname, string? FriendlyName, string? Office);

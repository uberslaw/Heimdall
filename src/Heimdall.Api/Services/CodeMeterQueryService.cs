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
        var checkouts = new List<CodeMeterCheckout>();
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

            checkouts.AddRange(ParseCheckouts(lines));
            serverNotes.Add($"{label} {shortHost}/{server.Serial}: ok");
        }

        // HA: never sum FQDNs — take the max Used across server views.
        int? poolUsed = perFqdnUsed.Count == 0 ? null : perFqdnUsed.Values.Max();
        var total = Math.Max(0, product.TotalLicenses);
        int? available = poolUsed is null ? null : Math.Max(0, total - poolUsed.Value);

        var distinct = DedupCheckouts(checkouts);
        return new CodeMeterProductSnapshot(
            product.ProductCode,
            total,
            poolUsed,
            available,
            distinct,
            Partial: anyFail && anyOk,
            anyOk);
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
    bool Partial,
    bool Ok)
{
    public static CodeMeterProductSnapshot Empty(int productCode, int total) =>
        new(productCode, total, null, null, [], false, false);
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

    public (int HpcSeats, int ClassicSeats) SeatsForIp(string? lastIp)
    {
        var ip = CodeMeterQueryService.NormalizeIp(lastIp);
        if (ip is null) return (0, 0);
        var hpc = Hpc.Checkouts.Count(c => IpsMatch(c.ClientAddress, ip));
        var classic = Classic.Checkouts.Count(c => IpsMatch(c.ClientAddress, ip));
        return (hpc, classic);
    }

    public (int Hpc, int Classic) UnmatchedSeats(IEnumerable<string?> knownIps)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ip in knownIps)
        {
            var n = CodeMeterQueryService.NormalizeIp(ip);
            if (n is not null) set.Add(n);
        }

        var hpc = Hpc.Checkouts.Count(c => c.ClientAddress is null || !set.Contains(c.ClientAddress));
        var classic = Classic.Checkouts.Count(c => c.ClientAddress is null || !set.Contains(c.ClientAddress));
        return (hpc, classic);
    }

    private static bool IpsMatch(string? a, string b) =>
        a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

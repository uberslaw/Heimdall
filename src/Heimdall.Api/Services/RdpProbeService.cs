using System.Net.Sockets;
using System.Text.Json;

namespace Heimdall.Api.Services;

/// <summary>
/// C# port of scripts/Test RDP Service Accepting Connections.ps1 — TCP 3389 + RDP negotiation probe.
/// Runs from the API host; no WinRM or admin on the target required (only network path to :3389).
/// </summary>
public static class RdpProbeService
{
    private static readonly byte[] RdpNegotiationPacket =
    [
        0x03, 0x00, 0x00, 0x13,
        0x0e, 0xe0, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00
    ];

    public sealed record RdpProbeResult(
        string ComputerName,
        bool RdpResponding,
        string? Error,
        int Port,
        int ElapsedMs);

    public static async Task<RdpProbeResult> ProbeAsync(
        string computerName,
        int port = 3389,
        int timeoutMs = 2000,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(timeoutMs);

            await client.ConnectAsync(computerName, port, connectCts.Token);
            await using var stream = client.GetStream();
            stream.ReadTimeout = timeoutMs;
            stream.WriteTimeout = timeoutMs;

            await stream.WriteAsync(RdpNegotiationPacket, ct);

            var response = new byte[4];
            var bytesRead = await stream.ReadAsync(response.AsMemory(0, response.Length), ct);
            var isAlive = bytesRead >= 2 && response[0] == 0x03 && response[1] == 0x00;

            sw.Stop();
            return new RdpProbeResult(
                computerName,
                isAlive,
                isAlive ? null : "Invalid RDP response",
                port,
                (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new RdpProbeResult(computerName, false, "Connection timeout", port, (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new RdpProbeResult(computerName, false, ex.Message, port, (int)sw.ElapsedMilliseconds);
        }
    }

    public static string ToJson(RdpProbeResult result) =>
        JsonSerializer.Serialize(result, JsonOptions);

    public static RdpProbeResult? TryParseStored(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<RdpProbeResult>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

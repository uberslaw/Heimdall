using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace Heimdall.Api.Services;

/// <summary>
/// Builds a minimal .rdp file response so Windows opens mstsc via file association
/// (browsers cannot invoke <c>mstsc /v:</c> directly).
/// </summary>
public static class RdpConnectFile
{
    /// <summary>Keep in lockstep with IsSafeRdpHost in scripts/Heimdall-LaunchRdp.vbs.</summary>
    private static readonly Regex SafeRdpTarget = new(
        @"^(?:[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*|\d{1,3}(?:\.\d{1,3}){3})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Returns a FileContentResult with <c>application/x-rdp</c>, or null if the hostname is invalid.
    /// Opening this file auto-connects (mstsc default verb).
    /// </summary>
    public static FileContentResult? TryCreate(string? hostname)
    {
        if (!TryNormalizeTarget(hostname, out var host))
            return null;

        var content =
            "full address:s:" + host + "\r\n" +
            "prompt for credentials:i:1\r\n" +
            "authentication level:i:2\r\n";
        var bytes = Encoding.ASCII.GetBytes(content);
        return new FileContentResult(bytes, "application/x-rdp")
        {
            FileDownloadName = SanitizeFileName(host) + ".rdp"
        };
    }

    /// <summary>
    /// Launcher that opens mstsc in <c>/edit</c> mode (Display, Local Resources, then Connect)
    /// with the target IP/hostname pre-filled. A plain .rdp download always auto-connects
    /// because browsers can only trigger the Open verb, not Edit.
    /// </summary>
    public static FileContentResult? TryCreateSettingsLauncher(string? hostname)
    {
        if (!TryNormalizeTarget(hostname, out var host))
            return null;

        var safe = SanitizeFileName(host);
        var cmd = new StringBuilder();
        cmd.AppendLine("@echo off");
        cmd.AppendLine("setlocal");
        cmd.AppendLine($"set \"RDP=%TEMP%\\Heimdall-{safe}.rdp\"");
        var first = true;
        foreach (var line in RdpSettingLines(host))
        {
            cmd.Append(first ? "> \"%RDP%\" echo " : ">> \"%RDP%\" echo ");
            cmd.AppendLine(line);
            first = false;
        }

        cmd.AppendLine("start \"\" \"%SystemRoot%\\System32\\mstsc.exe\" /edit \"%RDP%\"");

        return new FileContentResult(Encoding.ASCII.GetBytes(cmd.ToString()), "application/x-bat")
        {
            FileDownloadName = safe + "-RDP-Settings.cmd"
        };
    }

    public static bool TryNormalizeTarget(string? hostname, out string host)
    {
        host = "";
        if (string.IsNullOrWhiteSpace(hostname))
            return false;

        host = hostname.Trim();
        return host.Length is > 0 and <= 253 && SafeRdpTarget.IsMatch(host);
    }

    /// <summary>Keys shown on mstsc General / Display / Local Resources when opened with /edit.</summary>
    private static IEnumerable<string> RdpSettingLines(string host)
    {
        yield return "full address:s:" + host;
        yield return "prompt for credentials:i:1";
        yield return "authentication level:i:2";
        yield return "negotiate security layer:i:1";
        yield return "server port:i:3389";
        yield return "screen mode id:i:2";
        yield return "use multimon:i:0";
        yield return "desktopwidth:i:1920";
        yield return "desktopheight:i:1080";
        yield return "session bpp:i:32";
        yield return "compression:i:1";
        yield return "keyboardhook:i:2";
        yield return "audiomode:i:0";
        yield return "redirectclipboard:i:1";
        yield return "redirectprinters:i:1";
        yield return "redirectcomports:i:0";
        yield return "redirectsmartcards:i:0";
        yield return "drivestoredirect:s:";
        yield return "displayconnectionbar:i:1";
        yield return "autoreconnection enabled:i:1";
        yield return "bandwidthautodetect:i:1";
        yield return "networkautodetect:i:1";
        yield return "connection type:i:7";
        yield return "bitmapcachepersistenable:i:1";
    }

    private static string SanitizeFileName(string host)
    {
        var sb = new StringBuilder(host.Length);
        foreach (var c in host)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
                sb.Append(c);
            else
                sb.Append('_');
        }

        return sb.Length == 0 ? "remote" : sb.ToString();
    }
}

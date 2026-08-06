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
    private static readonly Regex SafeRdpTarget = new(
        @"^(?:[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*|\d{1,3}(?:\.\d{1,3}){3})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Returns a FileContentResult with <c>application/x-rdp</c>, or null if the hostname is invalid.
    /// </summary>
    public static FileContentResult? TryCreate(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return null;

        var host = hostname.Trim();
        if (host.Length > 253 || !SafeRdpTarget.IsMatch(host))
            return null;

        var content =
            "full address:s:" + host + "\r\n" +
            "prompt for credentials:i:1\r\n" +
            "authentication level:i:2\r\n";

        var fileName = SanitizeFileName(host) + ".rdp";
        var bytes = Encoding.ASCII.GetBytes(content);
        return new FileContentResult(bytes, "application/x-rdp")
        {
            FileDownloadName = fileName
        };
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

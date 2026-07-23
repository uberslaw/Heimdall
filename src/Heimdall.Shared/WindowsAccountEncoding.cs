using System.Text;

namespace Heimdall.Shared;

/// <summary>
/// Helpers for Windows account strings corrupted by ANSI WTS buffers read as UTF-16
/// (PtrToStringUni on WTSQuerySessionInformationA) — classic CJK mojibake for ASCII names.
/// </summary>
public static class WindowsAccountEncoding
{
    /// <summary>
    /// If <paramref name="value"/> looks like ANSI-as-UTF16 mojibake, return the recovered ASCII token; otherwise null.
    /// </summary>
    public static string? TryRecoverAnsiMisreadAsUtf16(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        if (LooksLikeWindowsAccountToken(value))
            return null;

        var hasCjk = false;
        var bytes = new List<byte>(value.Length * 2);
        foreach (var ch in value)
        {
            var code = (int)ch;
            if (code is >= 0x80 and <= 0xFFFF)
            {
                if (code >= 0x2E80)
                    hasCjk = true;
                bytes.Add((byte)(code & 0xFF));
                bytes.Add((byte)((code >> 8) & 0xFF));
            }
            else if (code is >= 0x20 and < 0x7F or '\\')
            {
                bytes.Add((byte)code);
            }
            else if (code is 0)
            {
                break;
            }
            else
            {
                return null;
            }
        }

        if (!hasCjk || bytes.Count < 2)
            return null;

        var recovered = Encoding.Latin1.GetString(bytes.ToArray()).TrimEnd('\0').Trim();
        return LooksLikeWindowsAccountToken(recovered) ? recovered : null;
    }

    public static string? RepairAccountField(string? value) =>
        value is null ? null : (TryRecoverAnsiMisreadAsUtf16(value) ?? value);

    public static bool LooksLikeWindowsAccountToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 1 or > 256)
            return false;

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '$' or '\\' or '@'))
                return false;
        }

        return true;
    }

    public static bool LooksLikeMojibakeAccount(string? value) =>
        !string.IsNullOrEmpty(value) && TryRecoverAnsiMisreadAsUtf16(value) is not null;
}

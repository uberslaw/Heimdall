using System.Runtime.Versioning;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Win32;

namespace Heimdall.Api.Services;

/// <summary>
/// <c>heimdall-rdp:</c> URL protocol so the dashboard can launch mstsc
/// (browsers cannot invoke mstsc themselves; a downloaded .rdp never auto-opens).
/// Staff PCs register via the Configure download (HKCU + %LOCALAPPDATA%).
/// The handler is VBScript via <c>wscript.exe</c> — no PowerShell (avoids console flash
/// and ExecutionPolicy / GPO RemoteSigned-Restricted blocks).
/// API-side <see cref="EnsureRegistered"/> is only a convenience when browsing on the API host.
/// </summary>
public static class RdpProtocolHandler
{
    public const string Scheme = "heimdall-rdp";
    public const string ConfigureFileName = "Heimdall-Configure-RDP.cmd";

    private const string HandlerFileName = "Heimdall-LaunchRdp.vbs";
    private const string CertBegin = "-----BEGIN CERTIFICATE-----";
    private const string CertEnd = "-----END CERTIFICATE-----";

    public static string LaunchUri(string host, bool edit = false)
    {
        var uri = $"{Scheme}://{host}";
        return edit ? uri + "/?edit=1" : uri;
    }

    public static string? TryLaunchUri(string? host, bool edit = false)
    {
        if (!RdpConnectFile.TryNormalizeTarget(host, out var normalized))
            return null;
        return LaunchUri(normalized, edit);
    }

    public static string ProtocolCommand(string handlerPath) =>
        "wscript.exe //nologo \"" + handlerPath + "\" \"%1\"";

    /// <summary>
    /// Self-contained Configure launcher for a staff PC that does not have Heimdall installed.
    /// Writes the VBS handler under %LOCALAPPDATA% and registers HKCU <c>heimdall-rdp</c>
    /// using cmd + certutil + reg add — never PowerShell.
    /// </summary>
    public static FileContentResult? TryCreateUserConfigureLauncher()
    {
        var script = TryLoadHandlerScript() ?? EmbeddedHandler;
        if (string.IsNullOrWhiteSpace(script))
            return null;

        var handlerBytes = Encoding.UTF8.GetBytes(NormalizeNewlines(script));
        var handlerB64 = WrapBase64(Convert.ToBase64String(handlerBytes));

        var cmd = new StringBuilder();
        cmd.AppendLine("@echo off");
        cmd.AppendLine("setlocal EnableExtensions");
        cmd.AppendLine("title Heimdall - Configure one-click Connect");
        cmd.AppendLine("set \"TOOLS=%LOCALAPPDATA%\\Heimdall\\tools\"");
        cmd.AppendLine("set \"DEST=%TOOLS%\\" + HandlerFileName + "\"");
        cmd.AppendLine("set \"LOG=%TOOLS%\\configure-rdp.log\"");
        cmd.AppendLine("set \"TMPVBS=%TEMP%\\heimdall-rdp-%RANDOM%%RANDOM%.vbs\"");
        cmd.AppendLine("echo.");
        cmd.AppendLine("echo Heimdall: Configure one-click Connect");
        cmd.AppendLine("echo This registers the heimdall-rdp link for your Windows user.");
        cmd.AppendLine("echo It does not install Heimdall.");
        cmd.AppendLine("echo.");
        cmd.AppendLine("if not exist \"%TOOLS%\" mkdir \"%TOOLS%\"");
        cmd.AppendLine(">>\"%LOG%\" echo %DATE% %TIME% Starting from %~f0");
        cmd.AppendLine("if not exist \"%SystemRoot%\\System32\\wscript.exe\" (");
        cmd.AppendLine("  echo wscript.exe was not found. One-click Connect needs Windows Script Host.");
        cmd.AppendLine("  >>\"%LOG%\" echo %DATE% %TIME% FAILED wscript.exe not found");
        cmd.AppendLine("  pause");
        cmd.AppendLine("  exit /b 1");
        cmd.AppendLine(")");
        cmd.AppendLine("if not exist \"%SystemRoot%\\System32\\certutil.exe\" (");
        cmd.AppendLine("  echo certutil.exe was not found. Cannot extract the RDP launcher.");
        cmd.AppendLine("  >>\"%LOG%\" echo %DATE% %TIME% FAILED certutil.exe not found");
        cmd.AppendLine("  pause");
        cmd.AppendLine("  exit /b 1");
        cmd.AppendLine(")");
        cmd.AppendLine("del /q \"%TMPVBS%\" >nul 2>&1");
        cmd.AppendLine("certutil -decode \"%~f0\" \"%TMPVBS%\" >>\"%LOG%\" 2>&1");
        cmd.AppendLine("if errorlevel 1 (");
        cmd.AppendLine("  echo Failed to extract the RDP launcher.");
        cmd.AppendLine("  >>\"%LOG%\" echo %DATE% %TIME% FAILED certutil -decode");
        cmd.AppendLine("  pause");
        cmd.AppendLine("  exit /b 1");
        cmd.AppendLine(")");
        cmd.AppendLine("if not exist \"%TMPVBS%\" (");
        cmd.AppendLine("  echo Extracted launcher is missing.");
        cmd.AppendLine("  >>\"%LOG%\" echo %DATE% %TIME% FAILED extracted file missing");
        cmd.AppendLine("  pause");
        cmd.AppendLine("  exit /b 1");
        cmd.AppendLine(")");
        cmd.AppendLine("copy /Y \"%TMPVBS%\" \"%DEST%\" >nul");
        cmd.AppendLine("del /q \"%TMPVBS%\" >nul 2>&1");
        cmd.AppendLine("if not exist \"%DEST%\" (");
        cmd.AppendLine("  echo Handler was not written to %DEST%");
        cmd.AppendLine("  >>\"%LOG%\" echo %DATE% %TIME% FAILED write %DEST%");
        cmd.AppendLine("  pause");
        cmd.AppendLine("  exit /b 1");
        cmd.AppendLine(")");
        cmd.AppendLine("for %%A in (\"%DEST%\") do if %%~zA LSS 80 (");
        cmd.AppendLine("  echo Handler is too small — extract may have failed.");
        cmd.AppendLine("  >>\"%LOG%\" echo %DATE% %TIME% FAILED size %%~zA");
        cmd.AppendLine("  pause");
        cmd.AppendLine("  exit /b 1");
        cmd.AppendLine(")");
        cmd.AppendLine("reg add \"HKCU\\Software\\Classes\\" + Scheme + "\" /ve /t REG_SZ /d \"URL:Heimdall Remote Desktop\" /f >nul");
        cmd.AppendLine("reg add \"HKCU\\Software\\Classes\\" + Scheme + "\" /v \"URL Protocol\" /t REG_SZ /d \"\" /f >nul");
        cmd.AppendLine("reg add \"HKCU\\Software\\Classes\\" + Scheme + "\\DefaultIcon\" /ve /t REG_SZ /d \"%SystemRoot%\\System32\\mstsc.exe,0\" /f >nul");
        cmd.AppendLine("reg add \"HKCU\\Software\\Classes\\" + Scheme + "\\shell\\open\\command\" /ve /t REG_SZ /d \"wscript.exe //nologo \\\"%DEST%\\\" \\\"%%1\\\"\" /f");
        cmd.AppendLine("if errorlevel 1 (");
        cmd.AppendLine("  echo Registry write failed.");
        cmd.AppendLine("  >>\"%LOG%\" echo %DATE% %TIME% FAILED reg add");
        cmd.AppendLine("  pause");
        cmd.AppendLine("  exit /b 1");
        cmd.AppendLine(")");
        cmd.AppendLine("reg query \"HKCU\\Software\\Classes\\" + Scheme + "\\shell\\open\\command\" /ve | find /I \"" + HandlerFileName + "\" >nul");
        cmd.AppendLine("if errorlevel 1 (");
        cmd.AppendLine("  echo Registry read-back failed.");
        cmd.AppendLine("  >>\"%LOG%\" echo %DATE% %TIME% FAILED registry read-back");
        cmd.AppendLine("  pause");
        cmd.AppendLine("  exit /b 1");
        cmd.AppendLine(")");
        cmd.AppendLine("reg query \"HKCU\\Software\\Classes\\" + Scheme + "\\shell\\open\\command\" /ve | find /I \"wscript.exe\" >nul");
        cmd.AppendLine("if errorlevel 1 (");
        cmd.AppendLine("  echo Registry read-back is not wscript.");
        cmd.AppendLine("  >>\"%LOG%\" echo %DATE% %TIME% FAILED registry not wscript");
        cmd.AppendLine("  pause");
        cmd.AppendLine("  exit /b 1");
        cmd.AppendLine(")");
        cmd.AppendLine("reg query \"HKCU\\Software\\Classes\\" + Scheme + "\\shell\\open\\command\" /ve | find /I \"powershell\" >nul");
        cmd.AppendLine("if not errorlevel 1 (");
        cmd.AppendLine("  echo Registry still points at PowerShell.");
        cmd.AppendLine("  >>\"%LOG%\" echo %DATE% %TIME% FAILED registry still powershell");
        cmd.AppendLine("  pause");
        cmd.AppendLine("  exit /b 1");
        cmd.AppendLine(")");
        cmd.AppendLine(">>\"%LOG%\" echo %DATE% %TIME% OK %DEST%");
        cmd.AppendLine("echo.");
        cmd.AppendLine("echo Done. Return to the browser, click Connect, then Always allow.");
        cmd.AppendLine("echo Handler: %DEST%");
        cmd.AppendLine("echo Log: %LOG%");
        cmd.AppendLine("pause");
        cmd.AppendLine("exit /b 0");
        cmd.AppendLine();
        cmd.AppendLine(CertBegin);
        cmd.AppendLine(handlerB64);
        cmd.AppendLine(CertEnd);

        return new FileContentResult(Encoding.ASCII.GetBytes(cmd.ToString()), "application/octet-stream")
        {
            FileDownloadName = ConfigureFileName
        };
    }

    public static void EnsureRegistered(ILogger? log = null)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            EnsureRegisteredWindows(log);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Could not register {Scheme} protocol (Connect will keep downloading .rdp)", Scheme);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureRegisteredWindows(ILogger? log)
    {
        var handlerPath = TryWriteHandlerFile(log);
        if (handlerPath is null)
            return;

        var command = ProtocolCommand(handlerPath);

        var wroteHive = false;
        try
        {
            WriteProtocolKey(Registry.LocalMachine, command);
            wroteHive = true;
            log?.LogInformation("Registered {Scheme} protocol in HKLM (wscript)", Scheme);
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "HKLM {Scheme} registration skipped", Scheme);
        }

        try
        {
            WriteProtocolKey(Registry.CurrentUser, command);
            wroteHive = true;
            log?.LogInformation("Registered {Scheme} protocol in HKCU (wscript)", Scheme);
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "HKCU {Scheme} registration skipped", Scheme);
        }

        if (!wroteHive)
            log?.LogWarning("Could not register {Scheme} in HKLM or HKCU — Connect will download .rdp", Scheme);
    }

    [SupportedOSPlatform("windows")]
    private static string? TryWriteHandlerFile(ILogger? log)
    {
        var script = TryLoadHandlerScript() ?? EmbeddedHandler;
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Heimdall", "tools"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Heimdall", "tools")
        };

        foreach (var tools in candidates)
        {
            try
            {
                Directory.CreateDirectory(tools);
                var handlerPath = Path.Combine(tools, HandlerFileName);
                File.WriteAllText(handlerPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                var written = new FileInfo(handlerPath);
                if (!written.Exists || written.Length < 80)
                    continue;
                log?.LogDebug("Wrote {Scheme} handler to {Path}", Scheme, handlerPath);
                return handlerPath;
            }
            catch (Exception ex)
            {
                log?.LogDebug(ex, "Could not write {Scheme} handler under {Dir}", Scheme, tools);
            }
        }

        log?.LogWarning("Could not write {Scheme} handler under ProgramData or LocalAppData", Scheme);
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteProtocolKey(RegistryKey hive, string command)
    {
        using var key = hive.CreateSubKey(@"SOFTWARE\Classes\" + Scheme, writable: true)
            ?? throw new InvalidOperationException("Could not open " + Scheme + " classes key.");
        key.SetValue("", "URL:Heimdall Remote Desktop");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon"))
            icon?.SetValue("", @"%SystemRoot%\System32\mstsc.exe,0");
        using var cmd = key.CreateSubKey(@"shell\open\command");
        cmd?.SetValue("", command);
    }

    private static string? TryLoadHandlerScript()
    {
        var source = Path.Combine(AppContext.BaseDirectory, HandlerFileName);
        return File.Exists(source) ? File.ReadAllText(source) : null;
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    private static string WrapBase64(string b64, int width = 76)
    {
        var sb = new StringBuilder(b64.Length + b64.Length / width + 8);
        for (var i = 0; i < b64.Length; i += width)
        {
            var len = Math.Min(width, b64.Length - i);
            sb.AppendLine(b64.Substring(i, len));
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Launch-only fallback if scripts/Heimdall-LaunchRdp.vbs was not copied next to the API binaries.
    /// Keep host validation in lockstep with RdpConnectFile.TryNormalizeTarget.
    /// </summary>
    private const string EmbeddedHandler = """
Option Explicit
Dim uri, host, editMode, sh, mstsc, cmdLine, rdpPath, fso, f, re, q, h, i, c, out
If WScript.Arguments.Count < 1 Then WScript.Quit 1
uri = CStr(WScript.Arguments(0))
editMode = (InStr(1, uri, "edit=1", vbTextCompare) > 0)
h = Trim(uri)
If Len(h) >= 2 Then
  If Left(h, 1) = Chr(34) And Right(h, 1) = Chr(34) Then h = Mid(h, 2, Len(h) - 2)
End If
Set re = New RegExp
re.IgnoreCase = True
re.Global = False
re.Pattern = "^heimdall-rdp:/{0,2}"
h = re.Replace(Trim(h), "")
q = InStr(h, "?")
If q > 0 Then h = Left(h, q - 1)
Do While Len(h) > 0 And Right(h, 1) = "/"
  h = Left(h, Len(h) - 1)
Loop
h = Trim(h)
If Len(h) < 1 Or Len(h) > 253 Then WScript.Quit 1
For i = 1 To Len(h)
  c = Mid(h, i, 1)
  If c = "&" Or c = "|" Or c = ";" Or c = "$" Or c = "`" Then WScript.Quit 1
Next
Set re = New RegExp
re.IgnoreCase = False
re.Pattern = "^(?:[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*|\d{1,3}(?:\.\d{1,3}){3})$"
If Not re.Test(h) Then WScript.Quit 1
host = h
Set sh = CreateObject("WScript.Shell")
mstsc = sh.ExpandEnvironmentStrings("%SystemRoot%") & "\System32\mstsc.exe"
If editMode Then
  out = ""
  For i = 1 To Len(host)
    c = Mid(host, i, 1)
    If (c >= "A" And c <= "Z") Or (c >= "a" And c <= "z") Or (c >= "0" And c <= "9") Or c = "." Or c = "-" Then
      out = out & c
    Else
      out = out & "_"
    End If
  Next
  If Len(out) = 0 Then out = "remote"
  rdpPath = sh.ExpandEnvironmentStrings("%TEMP%") & "\Heimdall-" & out & ".rdp"
  Set fso = CreateObject("Scripting.FileSystemObject")
  Set f = fso.CreateTextFile(rdpPath, True)
  f.Write "full address:s:" & host & vbCrLf
  f.Write "prompt for credentials:i:1" & vbCrLf
  f.Write "authentication level:i:2" & vbCrLf
  f.Close
  cmdLine = Chr(34) & mstsc & Chr(34) & " /edit " & Chr(34) & rdpPath & Chr(34)
Else
  cmdLine = Chr(34) & mstsc & Chr(34) & " /v:" & host
End If
On Error Resume Next
sh.Run cmdLine, 1, False
If Err.Number <> 0 Then WScript.Quit 1
""";
}

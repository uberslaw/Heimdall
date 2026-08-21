' Heimdall heimdall-rdp protocol handler — silent mstsc launch (no PowerShell).
' Keep host validation in lockstep with RdpConnectFile.TryNormalizeTarget.
'
'   heimdall-rdp://10.1.2.3          → mstsc /v:10.1.2.3
'   heimdall-rdp://10.1.2.3/?edit=1  → mstsc /edit (temp .rdp)
'   heimdall-rdp:HOST                → same as //HOST
'
' Register (installers / operators; use cscript so output is not a popup):
'   cscript //nologo Heimdall-LaunchRdp.vbs /register
'   cscript //nologo Heimdall-LaunchRdp.vbs /register /user
'
' Runtime (URL protocol; wscript has no console):
'   wscript.exe //nologo "C:\path\Heimdall-LaunchRdp.vbs" "%1"

Option Explicit

Dim doRegister, userOnly, uri, i, a, host, editMode, sh, mstsc, cmdLine, rdpPath

doRegister = False
userOnly = False
uri = ""

For i = 0 To WScript.Arguments.Count - 1
  a = LCase(Trim(CStr(WScript.Arguments(i))))
  If a = "/register" Or a = "-register" Then
    doRegister = True
  ElseIf a = "/user" Or a = "-user" Then
    userOnly = True
  ElseIf Left(a, 1) <> "/" And Left(a, 1) <> "-" Then
    uri = CStr(WScript.Arguments(i))
  End If
Next

If doRegister Then
  RegisterHandler userOnly
  WScript.Quit 0
End If

If Len(Trim(uri)) = 0 Then WScript.Quit 1

editMode = (InStr(1, uri, "edit=1", vbTextCompare) > 0)
host = NormalizeHost(uri)
If Len(host) = 0 Then WScript.Quit 1

Set sh = CreateObject("WScript.Shell")
mstsc = sh.ExpandEnvironmentStrings("%SystemRoot%") & "\System32\mstsc.exe"

If editMode Then
  rdpPath = sh.ExpandEnvironmentStrings("%TEMP%") & "\Heimdall-" & SanitizeFileName(host) & ".rdp"
  WriteSmallRdp rdpPath, host
  cmdLine = Quote(mstsc) & " /edit " & Quote(rdpPath)
Else
  cmdLine = Quote(mstsc) & " /v:" & host
End If

On Error Resume Next
sh.Run cmdLine, 1, False
If Err.Number <> 0 Then WScript.Quit 1
On Error GoTo 0
WScript.Quit 0

Function Quote(s)
  Quote = Chr(34) & s & Chr(34)
End Function

Function NormalizeHost(raw)
  Dim h, re, q
  h = Trim(CStr(raw))
  If Len(h) >= 2 Then
    If Left(h, 1) = Chr(34) And Right(h, 1) = Chr(34) Then
      h = Mid(h, 2, Len(h) - 2)
    End If
  End If
  h = Trim(h)

  Set re = New RegExp
  re.IgnoreCase = True
  re.Global = False
  re.Pattern = "^heimdall-rdp:/{0,2}"
  h = re.Replace(h, "")

  q = InStr(h, "?")
  If q > 0 Then h = Left(h, q - 1)

  Do While Len(h) > 0 And Right(h, 1) = "/"
    h = Left(h, Len(h) - 1)
  Loop
  h = Trim(h)

  If Len(h) < 1 Or Len(h) > 253 Then
    NormalizeHost = ""
    Exit Function
  End If
  If HasDangerousChars(h) Then
    NormalizeHost = ""
    Exit Function
  End If
  If Not IsSafeRdpHost(h) Then
    NormalizeHost = ""
    Exit Function
  End If
  NormalizeHost = h
End Function

Function HasDangerousChars(h)
  Dim i, c
  For i = 1 To Len(h)
    c = Mid(h, i, 1)
    If c = "&" Or c = "|" Or c = ";" Or c = "$" Or c = "`" Then
      HasDangerousChars = True
      Exit Function
    End If
  Next
  HasDangerousChars = False
End Function

Function IsSafeRdpHost(h)
  Dim re
  Set re = New RegExp
  re.IgnoreCase = False
  re.Global = False
  ' Lockstep with RdpConnectFile.SafeRdpTarget (hostname or IPv4).
  re.Pattern = "^(?:[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*|\d{1,3}(?:\.\d{1,3}){3})$"
  IsSafeRdpHost = re.Test(h)
End Function

Function SanitizeFileName(host)
  Dim i, c, out
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
  SanitizeFileName = out
End Function

Sub WriteSmallRdp(path, host)
  Dim fso, f
  Set fso = CreateObject("Scripting.FileSystemObject")
  Set f = fso.CreateTextFile(path, True)
  f.Write "full address:s:" & host & vbCrLf
  f.Write "prompt for credentials:i:1" & vbCrLf
  f.Write "authentication level:i:2" & vbCrLf
  f.Close
End Sub

Sub RegisterHandler(userScope)
  Dim sh, fso, src, destDir, dest, command, logPath, wrote
  Set sh = CreateObject("WScript.Shell")
  Set fso = CreateObject("Scripting.FileSystemObject")

  src = WScript.ScriptFullName
  If userScope Then
    destDir = sh.ExpandEnvironmentStrings("%LOCALAPPDATA%") & "\Heimdall\tools"
  Else
    destDir = sh.ExpandEnvironmentStrings("%ProgramData%") & "\Heimdall\tools"
  End If

  On Error Resume Next
  EnsureFolder destDir
  If Err.Number <> 0 And Not userScope Then
    Err.Clear
    destDir = sh.ExpandEnvironmentStrings("%LOCALAPPDATA%") & "\Heimdall\tools"
    EnsureFolder destDir
  End If
  If Err.Number <> 0 Then
    FailRegister "Could not create tools folder: " & destDir & " (" & Err.Description & ")"
  End If
  On Error GoTo 0

  dest = destDir & "\Heimdall-LaunchRdp.vbs"
  logPath = destDir & "\configure-rdp.log"

  If StrComp(fso.GetAbsolutePathName(src), fso.GetAbsolutePathName(dest), vbTextCompare) <> 0 Then
    On Error Resume Next
    fso.CopyFile src, dest, True
    If Err.Number <> 0 Then FailRegister "Could not copy handler to " & dest & " (" & Err.Description & ")"
    On Error GoTo 0
  End If

  If Not fso.FileExists(dest) Then FailRegister "Handler was not written to " & dest
  If fso.GetFile(dest).Size < 1 Then FailRegister "Handler was empty at " & dest

  command = "wscript.exe //nologo " & Quote(dest) & " " & Quote("%1")
  wrote = False

  If Not userScope And IsAdmin() Then
    On Error Resume Next
    WriteProtocolKey "HKLM\SOFTWARE\Classes\heimdall-rdp", command
    If Err.Number = 0 Then
      wrote = True
      AppendLog logPath, "Registered HKLM -> " & command
    End If
    Err.Clear
    On Error GoTo 0
  End If

  On Error Resume Next
  WriteProtocolKey "HKCU\Software\Classes\heimdall-rdp", command
  If Err.Number <> 0 Then
    FailRegister "HKCU registration failed: " & Err.Description
  End If
  wrote = True
  On Error GoTo 0
  AppendLog logPath, "Registered HKCU -> " & command

  Dim readCmd
  On Error Resume Next
  readCmd = sh.RegRead("HKCU\Software\Classes\heimdall-rdp\shell\open\command\")
  If Err.Number <> 0 Then FailRegister "Registry read-back failed (HKCU command missing)."
  On Error GoTo 0
  If InStr(1, readCmd, "Heimdall-LaunchRdp.vbs", vbTextCompare) = 0 Then
    FailRegister "Registry read-back mismatch: " & readCmd
  End If
  If InStr(1, readCmd, "wscript.exe", vbTextCompare) = 0 Then
    FailRegister "Registry read-back is not wscript: " & readCmd
  End If
  If InStr(1, readCmd, "powershell", vbTextCompare) > 0 Then
    FailRegister "Registry still points at PowerShell: " & readCmd
  End If

  WScript.Echo "Registered heimdall-rdp for this Windows user."
  WScript.Echo "Handler: " & dest
  WScript.Echo "Log: " & logPath
  If Not wrote Then FailRegister "No registry hive was written."
End Sub

Sub WriteProtocolKey(root, command)
  Dim sh
  Set sh = CreateObject("WScript.Shell")
  sh.RegWrite root & "\", "URL:Heimdall Remote Desktop", "REG_SZ"
  sh.RegWrite root & "\URL Protocol", "", "REG_SZ"
  sh.RegWrite root & "\DefaultIcon\", "%SystemRoot%\System32\mstsc.exe,0", "REG_SZ"
  sh.RegWrite root & "\shell\open\command\", command, "REG_SZ"
End Sub

Function IsAdmin()
  On Error Resume Next
  Dim sh
  Set sh = CreateObject("WScript.Shell")
  sh.RegRead "HKEY_USERS\S-1-5-19\Environment\TEMP"
  IsAdmin = (Err.Number = 0)
  Err.Clear
  On Error GoTo 0
End Function

Sub EnsureFolder(path)
  Dim fso, parent
  Set fso = CreateObject("Scripting.FileSystemObject")
  If fso.FolderExists(path) Then Exit Sub
  parent = fso.GetParentFolderName(path)
  If Len(parent) > 0 Then
    If Not fso.FolderExists(parent) Then EnsureFolder parent
  End If
  fso.CreateFolder path
End Sub

Sub AppendLog(logPath, msg)
  On Error Resume Next
  Dim fso, f
  Set fso = CreateObject("Scripting.FileSystemObject")
  Set f = fso.OpenTextFile(logPath, 8, True)
  f.WriteLine Year(Now) & "-" & Right("0" & Month(Now), 2) & "-" & Right("0" & Day(Now), 2) & " " & Time & " " & msg
  f.Close
  On Error GoTo 0
End Sub

Sub FailRegister(msg)
  WScript.Echo "FAILED: " & msg
  WScript.Quit 1
End Sub

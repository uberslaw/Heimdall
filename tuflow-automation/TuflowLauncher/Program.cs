using System.Runtime.InteropServices;
using System.Text.Json;
using TuflowLauncher;

// Entry point. Usage: TuflowLauncher.exe <path-to-run-spec.json>
// Spawned as a detached child process by Heimdall.Agent's TuflowRunHelper.TryStartIfRequested — see
// Agent-patches/Collectors/TuflowRunHelper.cs. This process outlives a single Agent heartbeat cycle by
// design: it owns the TUFLOW child process and status.json for the whole run, independent of whether
// the Agent service itself restarts in between.

if (args.Length < 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: TuflowLauncher.exe <run-spec.json>");
    return 2;
}

var specJson = File.ReadAllText(args[0]);
var spec = JsonSerializer.Deserialize(specJson, LauncherJsonContext.Default.RunSpec)
    ?? throw new InvalidOperationException("run-spec.json did not deserialize to a RunSpec");

var statusPath = Path.Combine(spec.RunDir, "status.json");
var stopRequestPath = Path.Combine(spec.RunDir, "stop.request");
var stdOutPath = Path.Combine(spec.RunDir, "tuflow.stdout.log");
var stdErrPath = Path.Combine(spec.RunDir, "tuflow.stderr.log");

// Mutable "what we know so far" — every WriteStatus call below reads from these instead of threading
// every field through every call site. Updated as the poll loop discovers checkpoints/.tsf progress.
DateTimeOffset? lastCheckpointUtc = null;
string? lastCheckpointFile = null;
double? percentComplete = null;
double? simulationTimeHours = null;
double? simulationEndTimeHours = null;
double? clockTimeRemainingHours = null;
int? warningCount = null;
double? massErrorPercent = null;
DateTimeOffset? stopRequestedUtc = null;
var stopSignalSent = false;

var isCmdMode = string.Equals(spec.LaunchMode, "Cmd", StringComparison.OrdinalIgnoreCase)
    || !string.IsNullOrWhiteSpace(spec.CmdPath);

// Effective display paths — Cmd mode may learn a .tcf from the script body during preflight.
var effectiveTcfPath = spec.TcfPath;
var effectiveCmdPath = spec.CmdPath;

WriteStatus(RunState.Starting, message: isCmdMode ? "Validating CMD/BAT launch script" : "Launching TUFLOW process");

// Preflight — fail with a clear ErrorSummary before CreateProcess so the Machine page Detail column
// (which prefers ErrorSummary over Message) and operators aren't left with an empty Detail.
// Common real-world miss: .tcf path typed as a folder (e.g. "...\runs") or a mapped drive letter that
// exists for the interactive user but not for the Heimdall.Agent Windows service session.
string? launchMessage = null;
if (isCmdMode)
{
    var inspection = CmdScriptInspector.Inspect(spec.CmdPath ?? "");
    if (!inspection.Ok)
    {
        WriteStatus(RunState.Failed, message: inspection.ErrorSummary, errorSummary: inspection.ErrorSummary);
        return 1;
    }

    if (inspection.TcfPaths.Count > 0)
        effectiveTcfPath = inspection.TcfPaths[0];
    effectiveCmdPath = spec.CmdPath;
    launchMessage = $"Running CMD: {inspection.Summary}";
    WriteStatus(RunState.Starting, message: launchMessage);
}
else
{
    if (string.IsNullOrWhiteSpace(spec.ExePath) || !File.Exists(spec.ExePath))
    {
        var msg = $"TUFLOW exe not found: {spec.ExePath}";
        WriteStatus(RunState.Failed, message: msg, errorSummary: msg);
        return 1;
    }
    if (string.IsNullOrWhiteSpace(spec.TcfPath) || !File.Exists(spec.TcfPath))
    {
        var msg = Directory.Exists(spec.TcfPath)
            ? $"TcfPath is a folder, not a .tcf file: {spec.TcfPath}"
            : $"TcfPath not found (service account may not see this path/drive): {spec.TcfPath}";
        WriteStatus(RunState.Failed, message: msg, errorSummary: msg);
        return 1;
    }
}

if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory) && !Directory.Exists(spec.WorkingDirectory))
{
    var msg = $"Working directory not found (mapped drives are often invisible to the agent service): {spec.WorkingDirectory}";
    WriteStatus(RunState.Failed, message: msg, errorSummary: msg);
    return 1;
}

var commandLine = isCmdMode ? BuildCmdCommandLine(spec.CmdPath!) : BuildCommandLine(spec);

var stdOutHandle = OpenInheritableLogHandle(stdOutPath);
var stdErrHandle = OpenInheritableLogHandle(stdErrPath);

var startupInfo = new NativeMethods.STARTUPINFO
{
    cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>(),
    dwFlags = NativeMethods.STARTF_USESTDHANDLES,
    hStdOutput = stdOutHandle,
    hStdError = stdErrHandle,
    // No stdin redirection — TUFLOW with -nq should not need it; leaving this as the launcher's
    // own (likely null/invalid in a service context) handle rather than wiring one up.
    hStdInput = IntPtr.Zero
};

var created = NativeMethods.CreateProcess(
    lpApplicationName: null,
    lpCommandLine: commandLine,
    lpProcessAttributes: IntPtr.Zero,
    lpThreadAttributes: IntPtr.Zero,
    bInheritHandles: true,
    // CREATE_NEW_PROCESS_GROUP so this launcher can target TUFLOW's PID specifically with
    // GenerateConsoleCtrlEvent later without also signalling itself. CREATE_NO_WINDOW because this
    // runs headless under a Windows Service (Heimdall.Agent) with no interactive desktop.
    dwCreationFlags: NativeMethods.CREATE_NEW_PROCESS_GROUP | NativeMethods.CREATE_NO_WINDOW,
    lpEnvironment: IntPtr.Zero,
    lpCurrentDirectory: spec.WorkingDirectory,
    lpStartupInfo: ref startupInfo,
    lpProcessInformation: out var processInfo);

// Capture immediately — CloseHandle below would overwrite Marshal.GetLastWin32Error().
var createProcessError = created ? 0 : Marshal.GetLastWin32Error();

NativeMethods.CloseHandle(stdOutHandle);
NativeMethods.CloseHandle(stdErrHandle);

if (!created)
{
    var msg = $"CreateProcess failed, Win32 error {createProcessError}";
    WriteStatus(RunState.Failed, message: msg, errorSummary: msg);
    return 1;
}

NativeMethods.CloseHandle(processInfo.hThread);
var processHandle = processInfo.hProcess;
var processId = processInfo.dwProcessId;
var startedUtc = DateTimeOffset.UtcNow;

WriteStatus(RunState.Running, processId, startedUtc,
    message: launchMessage ?? (isCmdMode ? "CMD/BAT running" : "TUFLOW running"));

var checkpointFolder = spec.ResultsFolder;
var logFolder = spec.LogFolder;
DateTimeOffset? lastTsfWriteUtc = null;

// Poll loop: watch for (a) an external stop.request file (written by Heimdall.Agent's
// TuflowRunHelper.TryExecuteCommand on TuflowStopGraceful), (b) new/updated restart-checkpoint files in
// the trf/erf folder, (c) progress updates in TUFLOW's own .tsf summary file, (d) the process exiting.
while (true)
{
    var waitResult = NativeMethods.WaitForSingleObject(processHandle, 2000);
    if (waitResult == NativeMethods.WAIT_OBJECT_0)
        break; // process exited

    if (!stopSignalSent && File.Exists(stopRequestPath))
    {
        stopSignalSent = true;
        stopRequestedUtc = DateTimeOffset.UtcNow;
        // Target this specific process group (== TUFLOW's own PID, see CREATE_NEW_PROCESS_GROUP above)
        // rather than broadcasting, so only TUFLOW receives it.
        NativeMethods.GenerateConsoleCtrlEvent(NativeMethods.CTRL_BREAK_EVENT, (uint)processId);
        WriteStatus(RunState.StopRequested, processId, startedUtc,
            message: "CTRL_BREAK_EVENT sent; waiting for TUFLOW to finish writing output and exit");
    }

    checkpointFolder ??= FindCheckpointFolder(spec.WorkingDirectory);
    if (checkpointFolder is not null)
    {
        var newest = FindNewestCheckpointFile(checkpointFolder);
        if (newest is not null && newest.Value.WriteUtc != lastCheckpointUtc)
        {
            lastCheckpointUtc = newest.Value.WriteUtc;
            lastCheckpointFile = newest.Value.FileName;
            WriteStatus(stopSignalSent ? RunState.StopRequested : RunState.Running, processId, startedUtc,
                message: $"Checkpoint written: {lastCheckpointFile}");
        }
    }

    logFolder ??= FindLogFolder(spec.WorkingDirectory);
    if (logFolder is not null)
    {
        var tsf = FindNewestTsf(logFolder);
        if (tsf is not null && tsf.Value.WriteUtc != lastTsfWriteUtc)
        {
            lastTsfWriteUtc = tsf.Value.WriteUtc;
            var progress = TryParseTsf(tsf.Value.Path);
            if (progress is not null)
            {
                percentComplete = progress.Value.PercentComplete ?? percentComplete;
                simulationTimeHours = progress.Value.SimulationTimeHours ?? simulationTimeHours;
                simulationEndTimeHours = progress.Value.SimulationEndTimeHours ?? simulationEndTimeHours;
                clockTimeRemainingHours = progress.Value.ClockTimeRemainingHours ?? clockTimeRemainingHours;
                warningCount = progress.Value.WarningCount ?? warningCount;
                massErrorPercent = progress.Value.MassErrorPercent ?? massErrorPercent;
                WriteStatus(stopSignalSent ? RunState.StopRequested : RunState.Running, processId, startedUtc,
                    message: percentComplete is double pct ? $"{pct:0.#}% complete" : "Progress updated");
            }
        }
    }
}

NativeMethods.GetExitCodeProcess(processHandle, out var exitCodeRaw);
NativeMethods.CloseHandle(processHandle);
var exitCode = unchecked((int)exitCodeRaw);

// Per the manual (Section 14.1.7): 0 == normal exit, 1 == premature exit (error or instability).
// A stop we requested ourselves is reported as Stopped even if TUFLOW's own exit code looks like an
// error exit, since from Heimdall's perspective this was an intentional, expected stop.
var finalState = stopSignalSent
    ? RunState.Stopped
    : exitCode == 0 ? RunState.Completed : RunState.Failed;

// Only try to explain a crash — a clean Completed/Stopped exit doesn't need an ErrorSummary.
string? errorSummary = null;
if (finalState == RunState.Failed)
{
    logFolder ??= FindLogFolder(spec.WorkingDirectory);
    errorSummary = TryExtractTlfErrors(logFolder)
        ?? TryExtractStderrTail(stdErrPath)
        ?? "TUFLOW exited with an error but no ERROR lines were found in its log — check tuflow.stdout.log / tuflow.stderr.log manually.";
}

WriteStatus(
    finalState, processId, startedUtc,
    exitCode: exitCode,
    errorSummary: errorSummary,
    message: stopSignalSent
        ? "Stopped gracefully on request"
        : exitCode == 0 ? "Completed normally" : $"TUFLOW exited with code {exitCode}");

try { File.Delete(stopRequestPath); } catch { /* best effort cleanup */ }
return exitCode;

// ---- local functions ----

static string BuildCommandLine(RunSpec spec)
{
    // -nc: no console window; implies -nmb/-b (batch/no message boxes on completion).
    // -nq: no queries — required so a programmatic CTRL_BREAK_EVENT can complete the stop unattended;
    // without it TUFLOW would show an interactive "stop simulation?" prompt that nothing can answer.
    // See tuflow-reference/wiki (batch-run pages) and ConsoleDisplay-2.md (Section 14.1.5) for both switches.
    var parts = new List<string> { Quote(spec.ExePath), "-nc", "-nq" };

    // TUFLOW scenario/event switches are numbered per-group (-s1/-s2.../-e1/-e2...) rather than repeated
    // bare -s/-e for multiple values. Verify this against the exact TUFLOW build/version in use — the
    // syntax has had minor variations across releases (see manual "Model Runtime Options" appendix).
    for (var i = 0; i < spec.Scenarios.Count; i++)
        parts.Add($"-s{i + 1} {Quote(spec.Scenarios[i])}");
    for (var i = 0; i < spec.Events.Count; i++)
        parts.Add($"-e{i + 1} {Quote(spec.Events[i])}");

    parts.Add(Quote(spec.TcfPath));
    return string.Join(' ', parts);
}

/// <summary>
/// Runs the operator's ready-made script via cmd.exe /c rather than reassembling TUFLOW.exe args.
/// Scripts that use "start …" to spawn TUFLOW asynchronously will make this process exit early —
/// operators should prefer a synchronous call inside the .cmd for Heimdall tracking/stop to work.
/// </summary>
static string BuildCmdCommandLine(string cmdPath)
{
    var comSpec = Environment.GetEnvironmentVariable("ComSpec");
    if (string.IsNullOrWhiteSpace(comSpec) || !File.Exists(comSpec))
        comSpec = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    // Always quote the script path — UNC and spaced paths both need it for cmd.exe /c.
    return $"{Quote(comSpec)} /c \"{cmdPath}\"";
}

static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

static IntPtr OpenInheritableLogHandle(string path)
{
    var sa = new NativeMethods.SECURITY_ATTRIBUTES
    {
        nLength = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>(),
        bInheritHandle = true,
        lpSecurityDescriptor = IntPtr.Zero
    };
    return NativeMethods.CreateFile(
        path,
        NativeMethods.GENERIC_WRITE,
        NativeMethods.FILE_SHARE_READ,
        ref sa,
        NativeMethods.OPEN_ALWAYS,
        NativeMethods.FILE_ATTRIBUTE_NORMAL,
        IntPtr.Zero);
}

// Best-effort discovery of the results folder when RunSpec.ResultsFolder wasn't supplied: TUFLOW writes
// 2D restart files to a "trf" subfolder and 1D to "erf", both under the model's results output folder
// (Section 8.8.3). This is not guaranteed to find the right folder for every Output Folder convention —
// pass ResultsFolder explicitly from Heimdall when it's known instead of relying on this.
static string? FindCheckpointFolder(string workingDirectory)
{
    try
    {
        foreach (var name in new[] { "trf", "erf" })
        {
            var hit = Directory.EnumerateDirectories(workingDirectory, name, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (hit is not null)
                return hit;
        }
    }
    catch
    {
        // Folder may not exist yet (created when TUFLOW first writes a checkpoint) — retry next poll.
    }
    return null;
}

static (DateTimeOffset WriteUtc, string FileName)? FindNewestCheckpointFile(string folder)
{
    try
    {
        var newest = new DirectoryInfo(folder)
            .EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
            .Where(f => f.Extension.Equals(".trf", StringComparison.OrdinalIgnoreCase)
                     || f.Extension.Equals(".erf", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        return newest is null ? null : (new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero), newest.Name);
    }
    catch
    {
        return null;
    }
}

// Best-effort discovery of the "log" folder (.tlf/.tsf/etc. — manual Section 14.4). Per the manual,
// TUFLOW's default (no Log Folder command in the .tcf) is to write these next to wherever the .tcf
// itself is run from, i.e. WorkingDirectory — so checking there directly first should cover the common
// case; the recursive search and folder-named-"log" fallback exist for less typical layouts.
static string? FindLogFolder(string workingDirectory)
{
    try
    {
        if (Directory.EnumerateFiles(workingDirectory, "*.tsf", SearchOption.TopDirectoryOnly).Any())
            return workingDirectory;

        var namedLog = Directory.EnumerateDirectories(workingDirectory, "log", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (namedLog is not null)
            return namedLog;

        var anyTsf = Directory.EnumerateFiles(workingDirectory, "*.tsf", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (anyTsf is not null)
            return Path.GetDirectoryName(anyTsf);
    }
    catch
    {
        // Folder/files may not exist yet this early in the run — retry next poll.
    }
    return null;
}

static (DateTimeOffset WriteUtc, string Path)? FindNewestTsf(string logFolder)
{
    try
    {
        var newest = new DirectoryInfo(logFolder)
            .EnumerateFiles("*.tsf", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        return newest is null ? null : (new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero), newest.FullName);
    }
    catch
    {
        return null;
    }
}

/// <summary>
/// Parses TUFLOW's own .tsf (TUFLOW Summary File) — a plain "Key == Value" text format, same style as
/// a .tcf — for the progress fields it already computes itself (manual Section 14.4.2, Table 14.1).
/// Deliberately tolerant: unknown/missing keys are just left null rather than failing the whole parse,
/// since the exact set of rows can vary by TUFLOW version/build.
/// </summary>
static TsfProgress? TryParseTsf(string path)
{
    try
    {
        // Share read/write so this doesn't collide with TUFLOW itself still writing the file.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        double? percentComplete = null, simTime = null, simEnd = null, clockRemaining = null, massError = null;
        int warningsPrior = 0, warningsDuring = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var eq = line.IndexOf("==", StringComparison.Ordinal);
            if (eq < 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 2)..].Trim();

            // Values can carry a trailing "." (TUFLOW's fixed-point style, e.g. "3.") — TryParse handles that fine.
            if (key.StartsWith("Percentage Complete", StringComparison.OrdinalIgnoreCase))
                percentComplete = ParseLeadingDouble(value);
            else if (key.StartsWith("Simulation Time (h)", StringComparison.OrdinalIgnoreCase))
                simTime = ParseLeadingDouble(value);
            else if (key.StartsWith("Simulation End Time (h)", StringComparison.OrdinalIgnoreCase))
                simEnd = ParseLeadingDouble(value);
            else if (key.StartsWith("Approximate Clock Time Remaining", StringComparison.OrdinalIgnoreCase))
                clockRemaining = ParseLeadingDouble(value);
            else if (key.StartsWith("Cumulative Mass Error", StringComparison.OrdinalIgnoreCase))
                massError = ParseLeadingDouble(value);
            else if (key.StartsWith("WARNINGs Prior to Simulation", StringComparison.OrdinalIgnoreCase))
                warningsPrior = (int)(ParseLeadingDouble(value) ?? 0);
            else if (key.StartsWith("WARNINGs During Simulation", StringComparison.OrdinalIgnoreCase))
                warningsDuring = (int)(ParseLeadingDouble(value) ?? 0);
        }

        return new TsfProgress(percentComplete, simTime, simEnd, clockRemaining, warningsPrior + warningsDuring, massError);
    }
    catch
    {
        return null; // mid-write read collision or transient IO error — retry next poll
    }

    static double? ParseLeadingDouble(string value)
    {
        // Strip anything after "!" (comment) or the first non-numeric run, so "100" / "3." / "0.05 %" all parse.
        var bang = value.IndexOf('!');
        if (bang >= 0)
            value = value[..bang];
        value = value.Trim();

        var end = 0;
        while (end < value.Length && (char.IsDigit(value[end]) || value[end] is '.' or '-'))
            end++;

        return end > 0 && double.TryParse(value[..end], out var d) ? d : null;
    }
}

/// <summary>
/// Reads the newest .tlf in logFolder and returns the first few "ERROR"-prefixed lines (manual Section
/// 14.4.1/14.4.5: "ERROR" indicates an unrecoverable error that stopped the simulation). Returns null if
/// no .tlf was found or it contains no ERROR lines (e.g. the crash happened before TUFLOW opened one at
/// all — a bad licence/dongle failure can do this — in which case the caller falls back to stderr).
/// </summary>
static string? TryExtractTlfErrors(string? logFolder)
{
    if (logFolder is null)
        return null;

    try
    {
        var tlf = new DirectoryInfo(logFolder)
            .EnumerateFiles("*.tlf", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (tlf is null)
            return null;

        using var stream = new FileStream(tlf.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        var errors = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null && errors.Count < 5)
        {
            if (line.TrimStart().StartsWith("ERROR", StringComparison.Ordinal))
                errors.Add(line.Trim());
        }

        if (errors.Count == 0)
            return null;

        var joined = string.Join(" | ", errors);
        return joined.Length > 800 ? joined[..800] + "…" : joined;
    }
    catch
    {
        return null;
    }
}

/// <summary>Fallback when no .tlf ERROR lines were found — last ~40 lines of redirected stderr, in case
/// TUFLOW wrote something there before crashing (e.g. a startup/licence failure before .tlf even opened).
/// Unverified: whether -nc still redirects meaningful console text to these files at all — see README.</summary>
static string? TryExtractStderrTail(string stdErrPath)
{
    try
    {
        if (!File.Exists(stdErrPath))
            return null;

        var lines = File.ReadAllLines(stdErrPath);
        if (lines.Length == 0)
            return null;

        var tail = lines[Math.Max(0, lines.Length - 40)..];
        var joined = string.Join(" | ", tail.Where(l => !string.IsNullOrWhiteSpace(l)));
        if (joined.Length == 0)
            return null;

        return joined.Length > 800 ? joined[..800] + "…" : joined;
    }
    catch
    {
        return null;
    }
}

void WriteStatus(
    RunState state,
    int? processId = null,
    DateTimeOffset? startedUtc = null,
    int? exitCode = null,
    string? errorSummary = null,
    string? message = null)
{
    var status = new RunStatus
    {
        RunId = spec.RunId,
        RunName = spec.RunName,
        State = state.ToWireState(),
        ProcessId = processId,
        TcfPath = string.IsNullOrWhiteSpace(effectiveTcfPath) ? null : effectiveTcfPath,
        CmdPath = string.IsNullOrWhiteSpace(effectiveCmdPath) ? null : effectiveCmdPath,
        StartedUtc = startedUtc,
        StopRequestedUtc = stopRequestedUtc,
        LastCheckpointUtc = lastCheckpointUtc,
        LastCheckpointFile = lastCheckpointFile,
        ExitCode = exitCode,
        Message = message,
        UpdatedUtc = DateTimeOffset.UtcNow,
        PercentComplete = percentComplete,
        SimulationTimeHours = simulationTimeHours,
        SimulationEndTimeHours = simulationEndTimeHours,
        ClockTimeRemainingHours = clockTimeRemainingHours,
        WarningCount = warningCount,
        MassErrorPercent = massErrorPercent,
        ErrorSummary = errorSummary
    };

    var json = JsonSerializer.Serialize(status, LauncherJsonContext.Default.RunStatus);
    // Write-to-temp-then-move avoids the Agent ever reading a half-written status.json mid-write.
    var tmp = statusPath + ".tmp";
    File.WriteAllText(tmp, json);
    File.Copy(tmp, statusPath, overwrite: true);
    File.Delete(tmp);
}

internal readonly record struct TsfProgress(
    double? PercentComplete,
    double? SimulationTimeHours,
    double? SimulationEndTimeHours,
    double? ClockTimeRemainingHours,
    int? WarningCount,
    double? MassErrorPercent);

using System.Runtime.InteropServices;

namespace TuflowLauncher;

/// <summary>
/// Win32 P/Invoke surface for spawning TUFLOW in its own process group and delivering a clean
/// CTRL_BREAK_EVENT to it later, from a separate process (this launcher), without affecting itself.
///
/// Why CTRL_BREAK_EVENT and not CTRL_C_EVENT: Windows only allows GenerateConsoleCtrlEvent to target
/// a *specific* non-zero process group ID reliably for CTRL_BREAK_EVENT. CTRL_C_EVENT can only be
/// broadcast to every process attached to the caller's own console (group ID 0 or the caller's own
/// group), which would also hit this launcher. CREATE_NEW_PROCESS_GROUP makes TUFLOW.exe's PID double
/// as its own process group ID, so CTRL_BREAK_EVENT can be aimed at it alone.
///
/// TUFLOW's manual (Section 14.1.5) documents Ctrl+C as the supported graceful-stop signal from an
/// interactive console; it does not separately document Ctrl+Break. Unverified assumption: TUFLOW's
/// runtime treats CTRL_BREAK_EVENT the same as CTRL_C_EVENT (both raise the same default console
/// control handler in most Windows console apps unless a program installs a handler that treats them
/// differently). Recommend a supervised test run before relying on this for the disconnection window.
/// </summary>
internal static class NativeMethods
{
    public const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const int CTRL_BREAK_EVENT = 1;

    public const int STARTF_USESTDHANDLES = 0x00000100;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_ALWAYS = 4;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint STILL_ACTIVE = 259;
    public const uint WAIT_TIMEOUT = 0x00000102;
    public const uint WAIT_OBJECT_0 = 0x00000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
}

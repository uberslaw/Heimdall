using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Heimdall.DiskUsage.Services;

[SupportedOSPlatform("windows")]
public static class RecycleBinService
{
    const int FO_DELETE = 0x0003;
    const int FOF_ALLOWUNDO = 0x0040;
    const int FOF_NOCONFIRMATION = 0x0010;
    const int FOF_SILENT = 0x0004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public int wFunc;
        public string pFrom;
        public string? pTo;
        public short fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    public static void SendDirectoryToRecycleBin(string path)
    {
        var fsPath = DriveScanner.ToFileSystemPath(path);
        if (string.IsNullOrWhiteSpace(fsPath) || !Directory.Exists(fsPath))
            throw new DirectoryNotFoundException($"Folder not found: {path}");

        // Double null-terminated path list required by SHFileOperation
        var from = fsPath.TrimEnd('\\') + "\0\0";
        var op = new SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = from,
            pTo = null,
            fFlags = (short)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT),
            fAnyOperationsAborted = false,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null
        };

        var rc = SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted)
            throw new IOException($"Could not send folder to Recycle Bin (code {rc}).");
    }

    public static bool IsDriveRoot(string path)
    {
        try
        {
            var key = DriveScanner.ToKey(path);
            return key.Length == 2 && key[1] == ':';
        }
        catch
        {
            return false;
        }
    }
}

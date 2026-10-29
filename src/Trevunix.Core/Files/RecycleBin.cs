using System.Runtime.InteropServices;

namespace Trevunix.Core.Files;

/// <summary>Recycle Bin operations through the Windows shell.</summary>
public static class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    private const uint SherbNoConfirmation = 0x1;
    private const uint SherbNoProgressUi = 0x2;
    private const uint SherbNoSound = 0x4;

    /// <summary>Moves a file or folder to the Recycle Bin so the user can restore it.</summary>
    public static bool Send(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;

        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            // The API takes a list terminated by two NULs.
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi),
        };

        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>Total size and item count across the Recycle Bins of all drives.</summary>
    public static (long Size, long Count) Query()
    {
        var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    /// <summary>Empties the Recycle Bins of all drives without asking.</summary>
    public static bool Empty()
    {
        var hr = SHEmptyRecycleBin(
            IntPtr.Zero,
            null,
            SherbNoConfirmation | SherbNoProgressUi | SherbNoSound
        );
        // E_UNEXPECTED is returned when the bin is already empty.
        return hr == 0 || hr == unchecked((int)0x8000FFFF);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;

        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string? rootPath, ref ShQueryRbInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}

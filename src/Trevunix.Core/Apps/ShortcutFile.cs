using System.Text;

namespace Trevunix.Core.Apps;

/// <summary>
///     Reads the target of a .lnk file from its documented binary layout ([MS-SHLLINK]), so no
///     shell COM object is needed. Covers the usual cases: a local path in the link info, or the
///     environment-variable path that installers write into many Start menu shortcuts.
/// </summary>
internal static class ShortcutFile
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 0x01;
    private const uint HasLinkInfo = 0x02;
    private const uint HasName = 0x04;
    private const uint HasRelativePath = 0x08;
    private const uint HasWorkingDir = 0x10;
    private const uint HasArguments = 0x20;
    private const uint HasIconLocation = 0x40;
    private const uint IsUnicode = 0x80;
    private const uint VolumeIdAndLocalBasePath = 0x01;
    private const uint EnvironmentBlockSignature = 0xA0000001;

    public static string? Target(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            return Parse(data, Path.GetDirectoryName(path));
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentOutOfRangeException
                        or IndexOutOfRangeException
            )
        {
            return null;
        }
    }

    /// <param name="linkFolder">Where the .lnk lives, for a target stored only as a relative path.</param>
    internal static string? Parse(byte[] data, string? linkFolder = null)
    {
        if (data.Length < HeaderSize || BitConverter.ToUInt32(data, 0) != HeaderSize)
            return null;

        var flags = BitConverter.ToUInt32(data, 0x14);
        var pos = HeaderSize;
        if ((flags & HasLinkTargetIdList) != 0)
            pos += 2 + BitConverter.ToUInt16(data, pos);

        string? local = null;
        if ((flags & HasLinkInfo) != 0)
        {
            var infoStart = pos;
            var infoSize = (int)BitConverter.ToUInt32(data, pos);
            local = LocalPath(data, infoStart);
            pos += infoSize;
        }
        if (local is not null)
            return Environment.ExpandEnvironmentVariables(local);

        // Walk the string data (keeping the relative path) to reach the extra blocks.
        var unicode = (flags & IsUnicode) != 0;
        string? relative = null;
        foreach (
            var flag in new[]
            {
                HasName,
                HasRelativePath,
                HasWorkingDir,
                HasArguments,
                HasIconLocation,
            }
        )
        {
            if ((flags & flag) == 0)
                continue;
            var chars = BitConverter.ToUInt16(data, pos);
            var bytes = chars * (unicode ? 2 : 1);
            if (flag == HasRelativePath)
                relative = unicode
                    ? Encoding.Unicode.GetString(data, pos + 2, bytes)
                    : Encoding.Default.GetString(data, pos + 2, bytes);
            pos += 2 + bytes;
        }

        while (pos + 8 <= data.Length)
        {
            var size = (int)BitConverter.ToUInt32(data, pos);
            if (size < 8 || pos + size > data.Length)
                break;
            if (BitConverter.ToUInt32(data, pos + 4) == EnvironmentBlockSignature && size >= 0x314)
            {
                // ANSI target (260 bytes) followed by the Unicode one (520 bytes).
                var wide = Encoding.Unicode.GetString(data, pos + 8 + 260, 520).TrimEnd('\0');
                var target =
                    wide.Length > 0
                        ? wide
                        : Encoding.Default.GetString(data, pos + 8, 260).TrimEnd('\0');
                return target.Length > 0 ? Environment.ExpandEnvironmentVariables(target) : null;
            }
            pos += size;
        }

        // Last resort: the path relative to the shortcut's own folder.
        if (relative is not null && linkFolder is not null)
        {
            try
            {
                // The relative path is from where the link was made; trust it only if it lands on a file.
                var full = Path.GetFullPath(Path.Combine(linkFolder, relative));
                return File.Exists(full) ? full : null;
            }
            catch (Exception ex)
                when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
        return null;
    }

    private static string? LocalPath(byte[] data, int start)
    {
        var headerSize = BitConverter.ToUInt32(data, start + 4);
        var infoFlags = BitConverter.ToUInt32(data, start + 8);
        if ((infoFlags & VolumeIdAndLocalBasePath) == 0)
            return null;

        string basePath;
        string suffix;
        if (headerSize >= 0x24)
        {
            // Unicode offsets follow the ANSI ones in newer links.
            basePath = WideString(data, start + (int)BitConverter.ToUInt32(data, start + 0x1C));
            suffix = WideString(data, start + (int)BitConverter.ToUInt32(data, start + 0x20));
        }
        else
        {
            basePath = AnsiString(data, start + (int)BitConverter.ToUInt32(data, start + 0x10));
            suffix = AnsiString(data, start + (int)BitConverter.ToUInt32(data, start + 0x18));
        }
        var path = basePath + suffix;
        return path.Length > 0 ? path : null;
    }

    private static string AnsiString(byte[] data, int at)
    {
        var end = Array.IndexOf(data, (byte)0, at);
        return end > at ? Encoding.Default.GetString(data, at, end - at) : "";
    }

    private static string WideString(byte[] data, int at)
    {
        var end = at;
        while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0))
            end += 2;
        return end > at ? Encoding.Unicode.GetString(data, at, end - at) : "";
    }
}

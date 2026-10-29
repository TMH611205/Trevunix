namespace Trevunix.Core.DiskUsage;

public sealed record FileTypeUsage(string Extension, long Size, long Count);

public sealed record LargeFile(string FullPath, long Size, DateTime LastWriteUtc)
{
    public string Name => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
}

public sealed record ScanProgress(long Files, long Folders, long Bytes, string CurrentPath);

public sealed class ScanResult
{
    public required UsageNode Root { get; init; }
    public required IReadOnlyList<LargeFile> LargestFiles { get; init; }
    public required IReadOnlyList<FileTypeUsage> FileTypes { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required bool Cancelled { get; init; }
}

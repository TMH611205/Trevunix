namespace Trevunix.Core.DiskUsage;

public sealed record DriveSummary(
    string RootPath,
    string Label,
    string Format,
    long TotalBytes,
    long FreeBytes
)
{
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedShare => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;

    public static IReadOnlyList<DriveSummary> GetFixedDrives()
    {
        var result = new List<DriveSummary>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;
                if (!drive.IsReady)
                    continue;

                result.Add(
                    new DriveSummary(
                        drive.RootDirectory.FullName,
                        drive.VolumeLabel,
                        drive.DriveFormat,
                        drive.TotalSize,
                        drive.TotalFreeSpace
                    )
                );
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return result;
    }
}

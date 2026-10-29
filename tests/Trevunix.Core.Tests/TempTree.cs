namespace Trevunix.Core.Tests;

/// <summary>A throwaway folder under %TEMP%, removed on dispose.</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree() => Directory.CreateDirectory(Root);

    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "TrevunixTests_" + Guid.NewGuid().ToString("N"));

    public string File(string relative, int bytes)
    {
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public string Folder(string relative) =>
        Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

using System.Text.Json;

namespace Trevunix.Core.Tweaks;

/// <summary>
///     A list of tweak ids saved to a file, so the same set can be applied on another PC.
///     Unknown ids (from a newer or older version) are dropped when loading.
/// </summary>
public static class TweakProfile
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record FileModel(string App, int Version, List<string> Tweaks);

    public static void Save(string path, IEnumerable<string> tweakIds) =>
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new FileModel("Trevunix", 1, [.. tweakIds]), Json)
        );

    /// <summary>The known tweaks named in the file; throws <see cref="InvalidDataException"/> for a file that is not a profile.</summary>
    public static IReadOnlyList<Tweak> Load(string path)
    {
        FileModel? model;
        try
        {
            model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
        if (model?.Tweaks is null || model.App != "Trevunix")
            throw new InvalidDataException("Not a Trevunix tweak profile.");

        var wanted = model.Tweaks.ToHashSet(StringComparer.Ordinal);
        return TweakCatalog.All.Where(t => wanted.Contains(t.Id)).ToList();
    }
}

using System.Xml.Linq;
using Trevunix.Core.Cleanup;
using Trevunix.Core.Maintenance;
using Trevunix.Core.Tools;
using Trevunix.Core.Tweaks;

namespace Trevunix.Core.Tests;

/// <summary>Every catalog entry needs a name and description in both languages, or the UI shows the raw key.</summary>
public class CatalogStringsTests
{
    private static HashSet<string> Keys(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Trevunix.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir.FullName, "src", "Trevunix", "Resources", file);
        return XDocument
            .Load(path)
            .Root!.Elements("data")
            .Select(d => (string)d.Attribute("name")!)
            .ToHashSet();
    }

    public static TheoryData<string> Files => new("Strings.resx", "Strings.vi.resx");

    [Theory]
    [MemberData(nameof(Files))]
    public void TweaksAndCleanupTargets_HaveStrings(string file)
    {
        var keys = Keys(file);
        var needed = TweakCatalog
            .All.SelectMany(t => new[] { $"Tweak.{t.Id}", $"Tweak.{t.Id}.Description" })
            .Concat(
                CleanupCatalog.All.SelectMany(t =>
                    new[] { $"Cleanup.Target.{t.Id}", $"Cleanup.Target.{t.Id}.Description" }
                )
            )
            .Concat(
                RepairCatalog
                    .All.Concat(RepairCatalog.Drivers)
                    .SelectMany(c => new[] { $"Repair.{c.Id}", $"Repair.{c.Id}.Description" })
            )
            .Concat(Enum.GetNames<HistoryKind>().Select(k => $"History.{k}"));

        Assert.Equal([], needed.Where(k => !keys.Contains(k)).ToArray());
    }

    [Fact]
    public void TweakProfile_RoundTripsKnownIdsOnly()
    {
        using var tree = new TempTree();
        var path = Path.Combine(tree.Root, "p.json");

        TweakProfile.Save(path, ["Telemetry", "NoSuchTweak", "GameMode"]);

        Assert.Equal(
            ["Telemetry", "GameMode"],
            TweakProfile.Load(path).Select(t => t.Id).Order().Reverse()
        );
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("""{ "App": "Other", "Version": 1, "Tweaks": [] }""")]
    public void TweakProfile_RejectsOtherFiles(string content)
    {
        using var tree = new TempTree();
        var path = Path.Combine(tree.Root, "p.json");
        File.WriteAllText(path, content);

        Assert.Throws<InvalidDataException>(() => TweakProfile.Load(path));
    }
}

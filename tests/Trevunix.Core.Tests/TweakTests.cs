using Microsoft.Win32;
using Trevunix.Core.Tweaks;

namespace Trevunix.Core.Tests;

/// <summary>Uses a throwaway key under HKCU\Software, removed afterwards.</summary>
public sealed class TweakTests : IDisposable
{
    private readonly string _sub = @"Software\TrevunixTests_" + Guid.NewGuid().ToString("N");
    private readonly TempTree _journal = new();

    private string Key(string relative = "") =>
        relative.Length == 0 ? $@"HKCU\{_sub}" : $@"HKCU\{_sub}\{relative}";

    private RegistryKey Create(string relative = "") =>
        Registry.CurrentUser.CreateSubKey(relative.Length == 0 ? _sub : $@"{_sub}\{relative}");

    private object? Read(string relative, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            relative.Length == 0 ? _sub : $@"{_sub}\{relative}"
        );
        return key?.GetValue(name);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_sub, throwOnMissingSubKey: false);
        _journal.Dispose();
    }

    private Tweak TweakOf(params ITweakAction[] actions) =>
        new(
            "Test_" + Guid.NewGuid().ToString("N"),
            TweakCategory.DataCollection,
            TweakRisk.Recommended,
            actions
        );

    [Fact]
    public void RegistryValue_ApplyThenRestore_PutsBackPreviousValue()
    {
        using (var key = Create())
            key.SetValue("Speed", 5, RegistryValueKind.DWord);
        var action = new RegistryValueAction(Key(), "Speed", ValueKind.DWord, 0);

        var before = action.Capture();
        action.Apply();
        Assert.True(action.IsDesired());
        Assert.Equal(0, Read("", "Speed"));

        action.Restore(before);
        Assert.Equal(5, Read("", "Speed"));
        Assert.False(action.IsDesired());
    }

    [Fact]
    public void RegistryValue_MissingBefore_RestoreDeletesValueAndCreatedKeys()
    {
        using (Create()) { }
        var action = new RegistryValueAction(Key(@"New\Deeper"), "Flag", ValueKind.DWord, 1);

        var before = action.Capture();
        action.Apply();
        Assert.Equal(1, Read(@"New\Deeper", "Flag"));

        action.Restore(before);
        using var created = Registry.CurrentUser.OpenSubKey($@"{_sub}\New");
        Assert.Null(created);
    }

    [Fact]
    public void RegistryValue_CreatedKeyGainedOtherValues_KeyIsKept()
    {
        using (Create()) { }
        var action = new RegistryValueAction(Key("Shared"), "Mine", ValueKind.DWord, 1);
        var before = action.Capture();
        action.Apply();
        using (var key = Create("Shared"))
            key.SetValue("Theirs", "x");

        action.Restore(before);

        Assert.Null(Read("Shared", "Mine"));
        Assert.Equal("x", Read("Shared", "Theirs"));
    }

    [Fact]
    public void RegistryValue_StringAndDefaultValue_RoundTrip()
    {
        using (var key = Create())
            key.SetValue("Delay", "400");
        var delay = new RegistryValueAction(Key(), "Delay", ValueKind.String, "100");
        var dflt = new RegistryValueAction(Key("Inproc"), "", ValueKind.String, "");

        var delayBefore = delay.Capture();
        var dfltBefore = dflt.Capture();
        delay.Apply();
        dflt.Apply();
        Assert.True(delay.IsDesired());
        Assert.True(dflt.IsDesired());

        delay.Restore(delayBefore);
        dflt.Restore(dfltBefore);
        Assert.Equal("400", Read("", "Delay"));
        using var inproc = Registry.CurrentUser.OpenSubKey($@"{_sub}\Inproc");
        Assert.Null(inproc);
    }

    [Fact]
    public void RegistryValue_UnsignedDword_IsStoredAndMatched()
    {
        var action = new RegistryValueAction(Key(), "Index", ValueKind.DWord, 0xFFFFFFFFu);

        action.Apply();

        Assert.Equal(-1, Read("", "Index"));
        Assert.True(action.IsDesired());
    }

    [Fact]
    public void ServiceStart_UsesServiceKeyAndRestores()
    {
        using (var svc = Create(@"Services\FakeSvc"))
            svc.SetValue("Start", 2, RegistryValueKind.DWord);
        var action = new ServiceStartAction(
            "FakeSvc",
            ServiceStartAction.Disabled,
            Key("Services")
        );

        var before = action.Capture();
        action.Apply();
        Assert.Equal(4, Read(@"Services\FakeSvc", "Start"));

        action.Restore(before);
        Assert.Equal(2, Read(@"Services\FakeSvc", "Start"));
    }

    [Fact]
    public void ServiceStart_MissingService_IsUnavailableAndCountsAsDone()
    {
        var action = new ServiceStartAction(
            "NoSuchSvc",
            ServiceStartAction.Disabled,
            Key("Services")
        );

        Assert.False(action.IsAvailable);
        Assert.True(action.IsDesired());
        action.Apply();
        Assert.Null(Read(@"Services\NoSuchSvc", "Start"));
    }

    [Fact]
    public void Engine_ApplyThenUndo_RestoresAndRemovesJournal()
    {
        using (var key = Create())
            key.SetValue("A", 7, RegistryValueKind.DWord);
        var engine = new TweakEngine(_journal.Root);
        var tweak = TweakOf(
            new RegistryValueAction(Key(), "A", ValueKind.DWord, 0),
            new RegistryValueAction(Key(), "B", ValueKind.DWord, 1)
        );

        Assert.Equal(TweakStatus.NotApplied, engine.GetStatus(tweak));
        Assert.True(engine.Apply(tweak).Success);
        Assert.Equal(TweakStatus.Applied, engine.GetStatus(tweak));
        Assert.True(engine.HasUndo(tweak));

        Assert.True(engine.Undo(tweak).Success);
        Assert.Equal(7, Read("", "A"));
        Assert.Null(Read("", "B"));
        Assert.False(engine.HasUndo(tweak));
        Assert.Equal(TweakStatus.NotApplied, engine.GetStatus(tweak));
    }

    [Fact]
    public void Engine_ApplyTwice_KeepsTheOriginalSnapshot()
    {
        using (var key = Create())
            key.SetValue("A", 7, RegistryValueKind.DWord);
        var engine = new TweakEngine(_journal.Root);
        var tweak = TweakOf(new RegistryValueAction(Key(), "A", ValueKind.DWord, 0));

        engine.Apply(tweak);
        engine.Apply(tweak);
        engine.Undo(tweak);

        Assert.Equal(7, Read("", "A"));
    }

    [Fact]
    public void Engine_OnlySomeActionsMatch_IsPartial()
    {
        using (var key = Create())
            key.SetValue("A", 0, RegistryValueKind.DWord);
        var engine = new TweakEngine(_journal.Root);
        var tweak = TweakOf(
            new RegistryValueAction(Key(), "A", ValueKind.DWord, 0),
            new RegistryValueAction(Key(), "B", ValueKind.DWord, 1)
        );

        Assert.Equal(TweakStatus.Partial, engine.GetStatus(tweak));
    }

    [Fact]
    public void Engine_UndoWithoutJournal_Fails()
    {
        var engine = new TweakEngine(_journal.Root);
        var tweak = TweakOf(new RegistryValueAction(Key(), "A", ValueKind.DWord, 0));

        Assert.False(engine.Undo(tweak).Success);
    }

    [Theory]
    [InlineData("<Task><Settings><Enabled>false</Enabled></Settings></Task>", false)]
    [InlineData("<Task><Settings><Enabled>true</Enabled></Settings></Task>", true)]
    [InlineData("<Task><Settings></Settings></Task>", true)]
    [InlineData(
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Settings><Enabled>false</Enabled></Settings></Task>",
        false
    )]
    public void ScheduledTask_ParseEnabled_ReadsTaskXml(string xml, bool expected)
    {
        Assert.Equal(expected, ScheduledTaskAction.ParseEnabled(xml));
    }

    [Fact]
    public void ScheduledTask_MissingTask_IsUnavailable()
    {
        var action = new ScheduledTaskAction(
            @"\Trevunix\NoSuchTask_" + Guid.NewGuid().ToString("N")
        );

        Assert.False(action.IsAvailable);
        Assert.True(action.IsDesired());
    }

    [Fact]
    public void Catalog_IdsUniqueAndKeysValid()
    {
        var all = TweakCatalog.All;

        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct().Count());
        Assert.All(all, t => Assert.NotEmpty(t.Actions));
        foreach (var action in all.SelectMany(t => t.Actions).OfType<RegistryValueAction>())
            Assert.Matches(@"^(HKCU|HKLM)\\", action.KeyPath);
    }
}

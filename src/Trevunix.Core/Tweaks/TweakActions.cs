using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Trevunix.Core.Tweaks;

/// <summary>A saved "before" state, as plain strings so it can live in the undo journal.</summary>
public sealed class Snapshot : Dictionary<string, string?>
{
    public Snapshot() { }

    public Snapshot(IDictionary<string, string?> values)
        : base(values) { }
}

/// <summary>One change to Windows that knows how to read, apply and undo itself.</summary>
public interface ITweakAction
{
    /// <summary>False when the thing to change does not exist here (a missing service or task).</summary>
    bool IsAvailable { get; }

    bool IsDesired();

    Snapshot Capture();

    void Apply();

    void Restore(Snapshot before);
}

public enum ValueKind
{
    DWord,
    QWord,
    String,
}

/// <summary>Sets one registry value. <see cref="KeyPath"/> starts with HKCU\ or HKLM\.</summary>
public sealed class RegistryValueAction(string keyPath, string name, ValueKind kind, object value)
    : ITweakAction
{
    public string KeyPath { get; } = keyPath;
    public string Name { get; } = name;
    public ValueKind Kind { get; } = kind;
    public object Value { get; } = value;

    public bool IsAvailable => true;

    public bool IsDesired()
    {
        using var key = Open(writable: false);
        return key is not null && Same(key.GetValue(Name), Value);
    }

    public Snapshot Capture()
    {
        var (root, sub) = Split(KeyPath);
        using var key = root.OpenSubKey(sub);
        var snapshot = new Snapshot { ["createdKey"] = FirstMissingKey(root, sub) };
        var current = key?.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (current is null)
            return snapshot;

        snapshot["kind"] = key!.GetValueKind(Name).ToString();
        snapshot["value"] = current switch
        {
            string[] multi => string.Join("\0", multi),
            byte[] bytes => System.Convert.ToBase64String(bytes),
            _ => System.Convert.ToString(
                current,
                System.Globalization.CultureInfo.InvariantCulture
            ),
        };
        return snapshot;
    }

    public void Apply()
    {
        var (root, sub) = Split(KeyPath);
        using var key =
            root.CreateSubKey(sub, writable: true)
            ?? throw new UnauthorizedAccessException(KeyPath);
        key.SetValue(Name, ToRegistryValue(Value, Kind), ToRegistryKind(Kind));
    }

    public void Restore(Snapshot before)
    {
        var (root, sub) = Split(KeyPath);

        if (before.TryGetValue("kind", out var kindText) && kindText is not null)
        {
            using var key =
                root.CreateSubKey(sub, writable: true)
                ?? throw new UnauthorizedAccessException(KeyPath);
            var kind = Enum.Parse<RegistryValueKind>(kindText);
            key.SetValue(Name, Decode(before["value"], kind), kind);
            return;
        }

        using (var key = root.OpenSubKey(sub, writable: true))
            key?.DeleteValue(Name, throwOnMissingValue: false);

        // The tweak created this key, so take it away again if nothing else lives there.
        if (before.TryGetValue("createdKey", out var created) && created is not null)
        {
            using var existing = root.OpenSubKey(created);
            if (existing is not null && IsEmptyTree(existing))
                root.DeleteSubKeyTree(created, throwOnMissingSubKey: false);
        }
    }

    private RegistryKey? Open(bool writable)
    {
        var (root, sub) = Split(KeyPath);
        return root.OpenSubKey(sub, writable);
    }

    internal static (RegistryKey Root, string Sub) Split(string path)
    {
        var slash = path.IndexOf('\\');
        var hive = slash < 0 ? path : path[..slash];
        var sub = slash < 0 ? "" : path[(slash + 1)..];
        RegistryKey root = hive.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
            _ => throw new ArgumentException($"Unknown registry hive in '{path}'."),
        };
        return (root, sub);
    }

    /// <summary>The shallowest key on the path that does not exist yet, or null.</summary>
    private static string? FirstMissingKey(RegistryKey root, string sub)
    {
        var parts = sub.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i <= parts.Length; i++)
        {
            var path = string.Join('\\', parts.Take(i));
            using var key = root.OpenSubKey(path);
            if (key is null)
                return path;
        }

        return null;
    }

    private static bool IsEmptyTree(RegistryKey key)
    {
        if (key.ValueCount > 0)
            return false;

        foreach (var name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name);
            if (child is null || !IsEmptyTree(child))
                return false;
        }

        return true;
    }

    private static bool Same(object? current, object desired) =>
        current switch
        {
            null => false,
            int i when desired is int d => i == d,
            int i when desired is uint u => unchecked((uint)i) == u,
            long l when desired is long d => l == d,
            string s => string.Equals(s, desired.ToString(), StringComparison.Ordinal),
            _ => false,
        };

    private static object ToRegistryValue(object value, ValueKind kind) =>
        kind switch
        {
            ValueKind.DWord => value is uint u ? unchecked((int)u) : System.Convert.ToInt32(value),
            ValueKind.QWord => System.Convert.ToInt64(value),
            _ => value.ToString() ?? "",
        };

    private static RegistryValueKind ToRegistryKind(ValueKind kind) =>
        kind switch
        {
            ValueKind.DWord => RegistryValueKind.DWord,
            ValueKind.QWord => RegistryValueKind.QWord,
            _ => RegistryValueKind.String,
        };

    private static object Decode(string? text, RegistryValueKind kind)
    {
        text ??= "";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return kind switch
        {
            RegistryValueKind.DWord => unchecked((int)uint.Parse(text, inv)),
            RegistryValueKind.QWord => long.Parse(text, inv),
            RegistryValueKind.MultiString => text.Split('\0'),
            RegistryValueKind.Binary => System.Convert.FromBase64String(text),
            _ => text,
        };
    }
}

/// <summary>
///     Sets how a service starts (2 automatic, 3 manual, 4 disabled). Written to the
///     service's registry key, so it takes effect at the next restart.
/// </summary>
public sealed class ServiceStartAction(string serviceName, int start, string? servicesKey = null)
    : ITweakAction
{
    public const int Automatic = 2;
    public const int Manual = 3;
    public const int Disabled = 4;

    private readonly string _keyPath =
        $@"{servicesKey ?? @"HKLM\SYSTEM\CurrentControlSet\Services"}\{serviceName}";

    public string ServiceName { get; } = serviceName;
    public int Start { get; } = start;

    public bool IsAvailable
    {
        get
        {
            var (root, sub) = RegistryValueAction.Split(_keyPath);
            using var key = root.OpenSubKey(sub);
            return key?.GetValue("Start") is int;
        }
    }

    public bool IsDesired() => !IsAvailable || ReadStart() == Start;

    public Snapshot Capture() => new() { ["start"] = ReadStart()?.ToString() };

    public void Apply()
    {
        if (IsAvailable)
            Write(Start);
    }

    public void Restore(Snapshot before)
    {
        if (IsAvailable && int.TryParse(before.GetValueOrDefault("start"), out var start))
            Write(start);
    }

    private int? ReadStart()
    {
        var (root, sub) = RegistryValueAction.Split(_keyPath);
        using var key = root.OpenSubKey(sub);
        return key?.GetValue("Start") as int?;
    }

    private void Write(int start)
    {
        var (root, sub) = RegistryValueAction.Split(_keyPath);
        using var key =
            root.OpenSubKey(sub, writable: true) ?? throw new UnauthorizedAccessException(_keyPath);
        key.SetValue("Start", start, RegistryValueKind.DWord);
    }
}

/// <summary>Turns a scheduled task off (or on) through schtasks.exe.</summary>
public sealed class ScheduledTaskAction(string taskPath, bool enabled = false) : ITweakAction
{
    public string TaskPath { get; } = taskPath;
    public bool Enabled { get; } = enabled;

    public bool IsAvailable => QueryEnabled() is not null;

    public bool IsDesired() => QueryEnabled() is not { } current || current == Enabled;

    public Snapshot Capture() => new() { ["enabled"] = QueryEnabled()?.ToString() };

    public void Apply()
    {
        if (IsAvailable)
            Change(Enabled);
    }

    public void Restore(Snapshot before)
    {
        if (IsAvailable && bool.TryParse(before.GetValueOrDefault("enabled"), out var wasEnabled))
            Change(wasEnabled);
    }

    /// <summary>Reads &lt;Enabled&gt; from the task XML; the XML is not localized.</summary>
    internal static bool? ParseEnabled(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var settings = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Settings");
            var enabled = settings?.Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled");
            return enabled is null || !bool.TryParse(enabled.Value, out var value) || value;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private bool? QueryEnabled()
    {
        var (code, output) = Run($"/Query /TN \"{TaskPath}\" /XML");
        return code == 0 ? ParseEnabled(output) : null;
    }

    private void Change(bool enable)
    {
        var (code, output) = Run($"/Change /TN \"{TaskPath}\" {(enable ? "/Enable" : "/Disable")}");
        if (code != 0)
            throw new InvalidOperationException(output.Trim());
    }

    private static (int Code, string Output) Run(string arguments)
    {
        var info = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        return (process.ExitCode, stdout.Length > 0 ? stdout : stderr);
    }
}

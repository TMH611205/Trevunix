using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Trevunix.Core.Tweaks;

public enum ContextMenuPlace
{
    Files,
    Folders,
    FolderBackground,
    Drives,
}

/// <param name="KeyPath">The verb or handler key under HKCR.</param>
/// <param name="Clsid">For shell extension handlers; null for plain verbs.</param>
public sealed record ContextMenuEntry(
    string Name,
    ContextMenuPlace Place,
    string KeyPath,
    string? Clsid,
    bool Enabled
);

/// <summary>
///     Right-click menu entries that apps add for files, folders and drives (the classic menu,
///     which Windows 11 shows under "Show more options"). Plain verbs are hidden with the
///     LegacyDisable value; shell extensions through the Blocked list. Both are reversible.
/// </summary>
public static class ContextMenu
{
    private const string BlockedKey =
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    private static readonly (string Key, ContextMenuPlace Place)[] Roots =
    [
        ("*", ContextMenuPlace.Files),
        ("AllFilesystemObjects", ContextMenuPlace.Files),
        ("Directory", ContextMenuPlace.Folders),
        ("Directory\\Background", ContextMenuPlace.FolderBackground),
        ("Drive", ContextMenuPlace.Drives),
    ];

    public static IReadOnlyList<ContextMenuEntry> List()
    {
        var blocked = BlockedClsids();
        var entries = new List<ContextMenuEntry>();
        foreach (var (rootKey, place) in Roots)
        {
            using (var shell = Registry.ClassesRoot.OpenSubKey($@"{rootKey}\shell"))
            {
                foreach (var verb in shell?.GetSubKeyNames() ?? [])
                {
                    using var key = shell!.OpenSubKey(verb);
                    if (key is null)
                        continue;
                    var name = CleanName(
                        Resolve(key.GetValue("MUIVerb") as string)
                            ?? Resolve(key.GetValue("") as string)
                            ?? verb
                    );
                    entries.Add(
                        new ContextMenuEntry(
                            name,
                            place,
                            $@"{rootKey}\shell\{verb}",
                            null,
                            key.GetValue("LegacyDisable") is null
                        )
                    );
                }
            }

            using var handlers = Registry.ClassesRoot.OpenSubKey(
                $@"{rootKey}\shellex\ContextMenuHandlers"
            );
            foreach (var handler in handlers?.GetSubKeyNames() ?? [])
            {
                using var key = handlers!.OpenSubKey(handler);
                var clsid = ParseClsid(key?.GetValue("") as string) ?? ParseClsid(handler);
                if (clsid is null)
                    continue;
                using var clsidKey = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}");
                var name = clsidKey?.GetValue("") as string;
                entries.Add(
                    new ContextMenuEntry(
                        CleanName(string.IsNullOrWhiteSpace(name) ? handler : name),
                        place,
                        $@"{rootKey}\shellex\ContextMenuHandlers\{handler}",
                        clsid,
                        !blocked.Contains(clsid)
                    )
                );
            }
        }
        return
        [
            .. entries
                .OrderBy(e => e.Place)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    public static void SetEnabled(ContextMenuEntry entry, bool enabled)
    {
        if (entry.Clsid is { } clsid)
        {
            var (root, sub) = RegistryValueAction.Split(BlockedKey);
            using var key = root.CreateSubKey(sub, writable: true);
            if (enabled)
                key.DeleteValue(clsid, throwOnMissingValue: false);
            else
                key.SetValue(clsid, "Blocked by Trevunix", RegistryValueKind.String);
            return;
        }

        using var verb =
            Registry.ClassesRoot.OpenSubKey(entry.KeyPath, writable: true)
            ?? throw new UnauthorizedAccessException(entry.KeyPath);
        if (enabled)
            verb.DeleteValue("LegacyDisable", throwOnMissingValue: false);
        else
            verb.SetValue("LegacyDisable", "", RegistryValueKind.String);
    }

    /// <summary>"Open with &amp;Code" becomes "Open with Code".</summary>
    internal static string CleanName(string name) => name.Replace("&", "").Trim();

    internal static string? ParseClsid(string? text) =>
        Guid.TryParse(text?.Trim(), out var guid) ? guid.ToString("B").ToUpperInvariant() : null;

    private static HashSet<string> BlockedClsids()
    {
        var (root, sub) = RegistryValueAction.Split(BlockedKey);
        using var key = root.OpenSubKey(sub);
        return new HashSet<string>(
            (key?.GetValueNames() ?? []).Select(ParseClsid).OfType<string>(),
            StringComparer.OrdinalIgnoreCase
        );
    }

    /// <summary>Turns "@shell32.dll,-8506" style names into text; plain text passes through.</summary>
    private static string? Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!value.StartsWith('@'))
            return value;
        var buffer = new StringBuilder(512);
        return SHLoadIndirectString(value, buffer, buffer.Capacity, IntPtr.Zero) == 0
            ? buffer.ToString()
            : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(
        string pszSource,
        StringBuilder pszOutBuf,
        int cchOutBuf,
        IntPtr ppvReserved
    );
}

using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace Trevunix.Localization;

/// <summary>UI text lookup. The language is fixed at startup; changing it needs a restart.</summary>
public static class Tr
{
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
    [
        ("vi", "Tiếng Việt"),
        ("en", "English"),
    ];

    private static readonly ResourceManager Resources = new(
        "Trevunix.Resources.Strings",
        typeof(Tr).Assembly
    );

    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    public static void SetLanguage(string code)
    {
        if (!Languages.Any(l => l.Code == code))
            code = "vi";

        Culture = CultureInfo.GetCultureInfo(code);
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Thread.CurrentThread.CurrentCulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;
    }

    public static string Get(string key) => Resources.GetString(key, Culture) ?? key;

    public static string Format(string key, params object?[] args) =>
        string.Format(Culture, Get(key), args);
}

/// <summary><c>{l:T Nav.Home}</c> in XAML.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Tr.Get(Key);
}

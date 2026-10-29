using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Trevunix.Core.Common;

namespace Trevunix.Converters;

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long bytes ? ByteSize.Format(bytes, Localization.Tr.Culture) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>True (or non-null, non-zero) shows; pass "invert" as the parameter to flip it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            bool b => b,
            int i => i != 0,
            string s => s.Length > 0,
            null => false,
            _ => true,
        };
        if (parameter as string == "invert")
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo c) =>
        throw new NotSupportedException();
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double share ? share.ToString("P1", Localization.Tr.Culture) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo c) =>
        throw new NotSupportedException();
}

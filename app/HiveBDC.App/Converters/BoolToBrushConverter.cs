using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Balsoft.Hive.BDC.App.Converters;

/// <summary>true → the theme's running dot, false → its stopped dot. Used for the service state.</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ThemeBrush.Get(value is true ? "DotOn" : "DotOff");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>A brush of the theme (Theme/Theme.xaml) by its key, so colours live in one place.</summary>
internal static class ThemeBrush
{
    public static Brush Get(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
}
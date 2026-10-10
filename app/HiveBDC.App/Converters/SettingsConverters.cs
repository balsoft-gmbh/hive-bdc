using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Balsoft.Hive.BDC.App.Converters;

/// <summary>Inverts a bool. Used to disable buttons while a save/test is in flight.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>Status message brush: true (error) → red, false → green.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly Brush Error = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Error : Ok;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>>0 → Visible, else Collapsed. Hides the validation-errors panel when there are none.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

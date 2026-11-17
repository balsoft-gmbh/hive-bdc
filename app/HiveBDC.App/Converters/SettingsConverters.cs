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
        => (value is int n && n > 0) != (parameter as string == "Empty") ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Visible when the value's text is one of the "|"-separated options in the parameter; a leading "!" turns it round.
/// "!" alone: visible when the value is not empty.
/// </summary>
public sealed class MatchToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string p = parameter as string ?? "";
        bool negate = p.StartsWith('!');
        bool match = p.TrimStart('!').Split('|').Contains(value?.ToString() ?? "");
        return match != negate ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True when all bound values have the same text (a navigation item's Tag against the current page).</summary>
public sealed class AllEqualConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length > 1 && values.All(v => Equals(v?.ToString(), values[0]?.ToString()));

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
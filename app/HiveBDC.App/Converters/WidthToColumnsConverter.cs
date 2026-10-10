using System.Globalization;
using System.Windows.Data;

namespace Balsoft.Hive.BDC.App.Converters;

/// <summary>
/// Window width → number of columns for the top card row, so it reflows instead of just
/// getting cramped: 1 column when narrow, 2 at medium, 3 when wide. A light, desktop-style
/// breakpoint (not continuous mobile reflow).
/// </summary>
public sealed class WidthToColumnsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double w = value is double d ? d : 0;
        if (w < 720) return 1;
        if (w < 1040) return 2;
        return 3;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

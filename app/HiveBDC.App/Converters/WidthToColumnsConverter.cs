using System.Globalization;
using System.Windows.Data;

namespace Balsoft.Hive.BDC.App.Converters;

/// <summary>
/// Width of the content area → number of columns for a row of cards or tiles, so it reflows instead of getting
/// cramped: 1 column below 640, 2 below 960, else 3. The parameter caps the count ("2" for two cards).
/// </summary>
public sealed class WidthToColumnsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double w = value is double d ? d : 0;
        int columns = w < 640 ? 1 : w < 960 ? 2 : 3;
        return int.TryParse(parameter as string, out int max) && max > 0 ? Math.Min(columns, max) : columns;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Balsoft.Hive.BDC.App.Converters;

/// <summary>true → green (running), false → grey (stopped/unreachable). Used for the status dot + pill.</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    private static readonly Brush Running = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush Stopped = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Running : Stopped;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

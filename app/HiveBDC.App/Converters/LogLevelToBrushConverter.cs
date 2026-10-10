using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Balsoft.Hive.BDC.App.Converters;

/// <summary>
/// Maps a <see cref="Balsoft.Hive.BDC.App.Services.LogLine"/> level string to the console
/// foreground colour: INF light, DBG dim, WRN amber, ERR red, UI blue (dashboard actions).
/// Tuned for the dark log surface.
/// </summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static readonly Brush Info = Freeze(0xC9, 0xD1, 0xD9);
    private static readonly Brush Debug = Freeze(0x7D, 0x85, 0x90);
    private static readonly Brush Warn = Freeze(0xE3, 0xB3, 0x41);
    private static readonly Brush Error = Freeze(0xFF, 0x7B, 0x72);
    private static readonly Brush Ui = Freeze(0x79, 0xC0, 0xFF);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "WRN" => Warn,
            "ERR" => Error,
            "DBG" => Debug,
            "UI" => Ui,
            _ => Info,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

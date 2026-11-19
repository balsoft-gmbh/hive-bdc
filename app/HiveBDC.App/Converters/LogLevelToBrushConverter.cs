using System.Globalization;
using System.Windows.Data;

namespace Balsoft.Hive.BDC.App.Converters;

/// <summary>
/// Maps a <see cref="Balsoft.Hive.BDC.App.Services.LogLine"/> level string to the log colour of the theme:
/// INF text, DBG grey, WRN amber, ERR red, UI blue (dashboard actions), on the light grey log surface.
/// </summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ThemeBrush.Get((value as string) switch
        {
            "WRN" => "LogWarn",
            "ERR" => "LogError",
            "DBG" => "LogDebug",
            "UI" => "LogUi",
            _ => "LogInfo",
        });

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
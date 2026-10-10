using System.IO;
using System.Text.Json;
using System.Windows;

namespace Balsoft.Hive.BDC.App.Services;

/// <summary>The main window's saved size / position / maximized state.</summary>
public sealed class WindowGeometry
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Remembers the dashboard's window size, position and maximized state between runs — a
/// polished-desktop touch. Stored per-user at %LOCALAPPDATA%\Balsoft\HiveBDC\window.json.
/// Best-effort: any failure just falls back to the default placement.
/// </summary>
public static class WindowPlacement
{
    private static string PathFor() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Balsoft", "HiveBDC", "window.json");

    /// <summary>Apply the saved geometry to a window before it is shown. Ignores off-screen positions.</summary>
    public static void Restore(Window w)
    {
        try
        {
            string p = PathFor();
            if (!File.Exists(p)) return;
            var g = JsonSerializer.Deserialize<WindowGeometry>(File.ReadAllText(p));
            if (g is null || g.Width < 300 || g.Height < 200) return;

            w.Width = g.Width;
            w.Height = g.Height;

            // Only honor the saved position if the window would land on a visible screen.
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var rect = new Rect(g.Left, g.Top, g.Width, g.Height);
            if (!double.IsNaN(g.Left) && !double.IsNaN(g.Top) && screen.IntersectsWith(rect))
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = g.Left;
                w.Top = g.Top;
            }

            if (g.Maximized) w.WindowState = WindowState.Maximized;
        }
        catch (Exception ex) { AppLog.Warn("Window position could not be restored, using defaults: " + ex.Message); }
    }

    /// <summary>Persist the window's current geometry. When maximized, stores the restore bounds so it reopens sensibly.</summary>
    public static void Save(Window w)
    {
        try
        {
            bool max = w.WindowState == WindowState.Maximized;
            Rect b = max ? w.RestoreBounds : new Rect(w.Left, w.Top, w.Width, w.Height);
            if (b.Width < 300 || b.Height < 200) return;   // minimized/odd state — skip

            var g = new WindowGeometry
            {
                Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height, Maximized = max,
            };
            string p = PathFor();
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(g));
        }
        catch (Exception ex) { AppLog.Warn("Window position could not be saved: " + ex.Message); }
    }
}

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App.Services;

/// <summary>Opens a dispatched document's output file (.pdf / .xml) from a
/// grid double-click, using the OS default application. The dashboard runs on the same
/// machine as the service, so the recorded output path is local.</summary>
internal static class ShellHelper
{
    /// <summary>Walk up from the double-clicked element to its row's item (null if a header/blank was clicked).</summary>
    public static RecentExportItem? RowItem(object originalSource)
    {
        DependencyObject? dep = originalSource as DependencyObject;
        while (dep is not null and not DataGridRow)
            dep = VisualTreeHelper.GetParent(dep);
        return (dep as DataGridRow)?.Item as RecentExportItem;
    }

    /// <summary>Open the export's output file with its default app. Shows a message if it's gone.</summary>
    public static void OpenOutput(RecentExportItem? item, Window owner)
    {
        if (item is null) return;
        string? path = item.OutputFile;
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(owner, "No output file was recorded for this export.",
                "Open file", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!File.Exists(path))
        {
            MessageBox.Show(owner,
                $"The file no longer exists:\n{path}\n\nIt may have been moved, renamed, or pruned.",
                "Open file", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Open file {path} failed: {ex.Message}");
            MessageBox.Show(owner, "Could not open the file:\n" + ex.Message,
                "Open file", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

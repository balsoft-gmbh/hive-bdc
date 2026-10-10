using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Balsoft.Hive.BDC.App.Services;

/// <summary>Tiny visual-tree helpers for the dashboard windows.</summary>
public static class VisualTreeUtil
{
    /// <summary>First <see cref="ScrollViewer"/> in the visual subtree (e.g. the one inside a ListBox), or null if not realized yet.</summary>
    public static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is ScrollViewer sv) return sv;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }
}

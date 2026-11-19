using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Balsoft.Hive.BDC.App.Pages;

/// <summary>Read-only lists as the web console's grid behaves: columns that give way, and one Tab stop.</summary>
internal static class ListColumns
{
    /// <summary>Below a width the list drops its least important columns instead of squeezing every column.</summary>
    public static void HideBelow(DataGrid grid, double width, params DataGridColumn[] columns)
    {
        void Apply()
        {
            var visibility = grid.ActualWidth > 0 && grid.ActualWidth < width ? Visibility.Collapsed : Visibility.Visible;
            foreach (var column in columns) column.Visibility = visibility;
        }
        grid.SizeChanged += (_, _) => Apply();
        Apply();
    }

    /// <summary>
    /// The list is one Tab stop (its cells are none, Theme/Controls.xaml): Tab onto it focuses the selected or first row,
    /// the arrow keys move within, the next Tab leaves it, and Shift+Tab from a row goes on to the control before it.
    /// </summary>
    public static void TabOnce(DataGrid grid)
    {
        grid.GotKeyboardFocus += (_, e) =>
        {
            if (!ReferenceEquals(e.NewFocus, grid)) return;
            if (e.OldFocus is DependencyObject old && Inside(grid, old))
            {
                grid.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
                return;
            }
            if (grid.Items.Count == 0) return;
            var item = grid.SelectedItem ?? grid.Items[0];
            grid.ScrollIntoView(item);
            grid.UpdateLayout();
            if (grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row && First<DataGridCell>(row) is { } cell)
            {
                grid.SelectedItem = item;
                cell.Focus();
            }
        };
    }

    private static bool Inside(DependencyObject root, DependencyObject element)
    {
        for (var e = element; e is not null; e = e is Visual ? VisualTreeHelper.GetParent(e) : LogicalTreeHelper.GetParent(e))
            if (ReferenceEquals(e, root)) return true;
        return false;
    }

    private static T? First<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (First<T>(child) is { } found) return found;
        }
        return null;
    }
}
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App.Pages;

/// <summary>
/// Document history: the steps (what needs attention, every step of one document, Retry) and every exported document.
/// Each tab reads the service again when it opens, as the former History and Documents windows did.
/// </summary>
public partial class HistoryPage : UserControl
{
    private ShellViewModel? _shell;
    private HistoryViewModel? _history;
    private DocumentsViewModel? _documents;

    public HistoryPage()
    {
        InitializeComponent();
        ListColumns.HideBelow(StepsGrid, 880, StepsTypeColumn, StepsWhereColumn);
        ListColumns.HideBelow(DocumentsGrid, 860, DocumentsSourceColumn, DocumentsSizeColumn, DocumentsSharePointColumn);
        ListColumns.TabOnce(StepsGrid);
        ListColumns.TabOnce(DocumentsGrid);
    }

    public void Attach(ShellViewModel shell, HistoryViewModel history, DocumentsViewModel documents)
    {
        _shell = shell;
        _history = history;
        _documents = documents;
        TabsBar.DataContext = shell;
        StepsView.DataContext = history;
        DocumentsView.DataContext = documents;
        IsVisibleChanged += async (_, _) => { if (IsVisible) await RefreshTab(); };
        shell.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.HistoryTab) && IsVisible) await RefreshTab();
        };
    }

    private async Task RefreshTab()
    {
        try
        {
            if (_shell?.HistoryTab == "Documents") await _documents!.RefreshAsync();
            else await _history!.RefreshAsync();
        }
        catch (Exception ex) { AppLog.Warn("Document history: loading failed: " + ex.Message); }
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _history is not null) await _history.RefreshAsync();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_history?.ShowDocumentCommand.CanExecute(null) == true) _history.ShowDocumentCommand.Execute(null);
    }

    private void OnStepsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _history?.Selected is null) return;
        _history.ShowDocumentCommand.Execute(null);
        e.Handled = true;
    }

    private void OnDocumentDoubleClick(object sender, MouseButtonEventArgs e)
        => ShellHelper.OpenOutput(ShellHelper.RowItem(e.OriginalSource), Window.GetWindow(this)!);

    private void OnDocumentsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DocumentsGrid.SelectedItem is not RecentExportItem item) return;
        ShellHelper.OpenOutput(item, Window.GetWindow(this)!);
        e.Handled = true;
    }
}
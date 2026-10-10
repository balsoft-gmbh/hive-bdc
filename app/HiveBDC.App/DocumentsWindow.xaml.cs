using System.Windows;
using System.Windows.Input;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App;

public partial class DocumentsWindow : Window
{
    private readonly DocumentsViewModel _vm;

    public DocumentsWindow(DocumentsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            try { await _vm.RefreshAsync(); }
            catch (Exception ex) { AppLog.Warn("Documents window: loading the export list failed: " + ex.Message); }
        };
    }

    private void Document_DoubleClick(object sender, MouseButtonEventArgs e)
        => ShellHelper.OpenOutput(ShellHelper.RowItem(e.OriginalSource), this);
}

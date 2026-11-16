using System.Windows;
using System.Windows.Input;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App;

public partial class HistoryWindow : Window
{
    private readonly HistoryViewModel _vm;

    public HistoryWindow(HistoryViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) => await _vm.RefreshAsync();
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await _vm.RefreshAsync();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.ShowDocumentCommand.CanExecute(null)) _vm.ShowDocumentCommand.Execute(null);
    }
}
using System.Windows;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App;

public partial class ImportFlowWindow : Window
{
    private readonly ImportFlowViewModel _vm;

    public ImportFlowWindow(ImportFlowViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) => await _vm.LoadAsync();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var empty = _vm.Columns.Where(c => string.IsNullOrWhiteSpace(c.Template)).Select(c => c.Column).ToList();
        if (empty.Count > 0
            && MessageBox.Show(this, $"These columns are not filled and stay empty in the CSV: {string.Join(", ", empty)}. Save anyway?",
                "Hive BDC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        DialogResult = true;
    }
}
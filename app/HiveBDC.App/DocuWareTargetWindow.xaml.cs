using System.Windows;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App;

public partial class DocuWareTargetWindow : Window
{
    private readonly DocuWareTargetViewModel _vm;

    public DocuWareTargetWindow(DocuWareTargetViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) => await _vm.LoadAsync();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var missing = _vm.Fields.Where(f => f.Required && string.IsNullOrWhiteSpace(f.Template)).Select(f => f.Field).ToList();
        if (missing.Count > 0
            && MessageBox.Show(this, $"These required fields are not filled: {string.Join(", ", missing)}. DocuWare will refuse the documents. Save anyway?",
                "Hive BDC", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DialogResult = true;
    }
}
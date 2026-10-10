using System.Windows;
using Balsoft.Hive.BDC.App.Services;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App;

public partial class ChangelogWindow : Window
{
    private readonly ChangelogViewModel _vm;

    public ChangelogWindow(ChangelogViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            try { await _vm.LoadAsync(); }
            catch (Exception ex) { AppLog.Warn("Changelog window: loading failed (the built-in copy stays shown): " + ex.Message); }
        };
    }
}

using System.Windows;
using Balsoft.Hive.BDC.App.ViewModels;

namespace Balsoft.Hive.BDC.App.Pages;

/// <summary>Remove asks inside the record first (Keep or Remove); the item is taken from the button's data context.</summary>
internal static class RecordActions
{
    public static void SetConfirm(object sender, bool ask)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case DocumentTypeEditViewModel t: t.ConfirmRemove = ask; break;
            case RouteRow r: r.ConfirmRemove = ask; break;
            case TargetRow d: d.ConfirmRemove = ask; break;
            case ImportFlowRow i: i.ConfirmRemove = ask; break;
            case InboundTypeRow g: g.ConfirmRemove = ask; break;
        }
    }
}
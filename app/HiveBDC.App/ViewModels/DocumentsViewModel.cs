using System.Collections.ObjectModel;
using Balsoft.Hive.BDC.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>
/// Backs the all-documents window: the full export ledger (up to 2000 rows, ~the 60-day
/// retention window) plus lifetime totals and this month's split per document type, over
/// the loopback API. Metadata only.
/// </summary>
public sealed partial class DocumentsViewModel : ObservableObject
{
    private readonly LoopbackClient _client;

    public DocumentsViewModel(LoopbackClient client) => _client = client;

    public ObservableCollection<RecentExportItem> Documents { get; } = new();

    [ObservableProperty] private string _summary = "Loading…";
    [ObservableProperty] private string _byType = "";
    [ObservableProperty] private bool _isBusy;

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var res = await _client.GetMetricsAsync(2000);
            Documents.Clear();
            if (res is { Reachable: true, Data: { } m })
            {
                if (m.Recent is not null)
                    foreach (var e in m.Recent)
                        Documents.Add(RecentExportItem.From(e));
                Summary = $"{Documents.Count} shown · today {m.Today} · month {m.Month?.ToString() ?? "—"} · total {m.Total}";
                ByType = m.MonthByType is { Count: > 0 }
                    ? "This month: " + string.Join(" · ", m.MonthByType.Select(t => $"{t.DocumentType} {t.Count}"))
                    : "";
            }
            else
            {
                Summary = res.Error ?? "Service not reachable.";
                ByType = "";
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand] private Task Refresh() => RefreshAsync();
}

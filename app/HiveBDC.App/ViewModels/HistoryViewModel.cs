using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Balsoft.Hive.BDC.App.Services;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>One line of a document's history as the list shows it.</summary>
public sealed record HistoryRow(string At, string DocumentId, string Document, string Type, string Step, string Target, string State, string Message)
{
    public bool IsFailed => State == "failed";
    public bool IsWaiting => State == "waiting";

    public static HistoryRow From(HistoryEventDto e) => new(
        e.At ?? "",
        e.DocumentId ?? "",
        string.IsNullOrWhiteSpace(e.DocumentNumber) ? e.DocumentId ?? "" : e.DocumentNumber,
        e.DocumentType ?? "",
        e.Step switch
        {
            "received" => "Received",
            "read" => "Read",
            "produced" => "Produced",
            "delivered" => "Delivered",
            "waiting" => "Waiting",
            "failed" => "Stopped",
            "done" => "Finished",
            _ => e.Step ?? "",
        },
        e.Target switch
        {
            null or "" => "",
            "sharepoint" => "SharePoint",
            "mail" => "Mail",
            "docuware" => "DocuWare",
            var other => other,
        },
        e.State ?? "",
        e.Message ?? "");
}

/// <summary>
/// "Document history": what needs attention (stopped or waiting documents), every step of one document by its number,
/// and Retry for a document that stopped.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly LoopbackClient _client;

    public HistoryViewModel(LoopbackClient client) => _client = client;

    public ObservableCollection<HistoryRow> Rows { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _attentionOnly = true;
    [ObservableProperty] private HistoryRow? _selected;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;

    public bool CanRetry => Selected is { IsFailed: true };

    partial void OnSelectedChanged(HistoryRow? value) => OnPropertyChanged(nameof(CanRetry));

    partial void OnAttentionOnlyChanged(bool value) => _ = RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        string search = Search.Trim();
        var res = search.Length > 0
            ? await _client.GetHistoryAsync(search)
            : AttentionOnly ? await _client.GetAttentionAsync() : await _client.GetHistoryAsync(null, 300);
        if (res is not { Reachable: true, Data: { } data })
        {
            StatusMessage = res.Error ?? "Service not reachable.";
            StatusIsError = true;
            return;
        }
        var rows = (data.Events ?? new()).Select(HistoryRow.From).ToList();
        if (search.Length == 0 && !AttentionOnly) rows.Reverse();
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        StatusIsError = false;
        StatusMessage = search.Length > 0
            ? rows.Count == 0 ? $"Nothing found for \"{search}\". Search by document number or by the file name's first part." : $"{rows.Count} step(s) of \"{search}\", oldest first."
            : AttentionOnly
                ? rows.Count == 0 ? "Nothing needs attention: no document is stopped or waiting." : $"{rows.Count} document(s) need attention, newest first."
                : $"The newest {rows.Count} step(s) of all documents.";
    }

    [RelayCommand]
    private async Task ShowDocumentAsync()
    {
        if (Selected is null) return;
        Search = Selected.Document;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        Search = "";
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (Selected is not { IsFailed: true } row) return;
        var res = await _client.RetryAsync(row.DocumentId);
        StatusIsError = res.Data?.Ok != true;
        StatusMessage = res.Data?.Message ?? res.Error ?? "Retry failed.";
        AppLog.Info($"Retry {row.Document}: {StatusMessage}");
    }
}
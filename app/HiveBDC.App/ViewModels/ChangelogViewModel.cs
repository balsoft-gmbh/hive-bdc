using System.Collections.ObjectModel;
using Balsoft.Hive.BDC.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Balsoft.Hive.BDC.App.ViewModels;

/// <summary>
/// Backs the Release-notes window. Shows the changelog bundled with this build immediately
/// (offline), then refreshes from the update feed so newer versions also appear.
/// </summary>
public sealed partial class ChangelogViewModel : ObservableObject
{
    private readonly UpdateService _updates;

    public ChangelogViewModel(UpdateService updates) => _updates = updates;

    public ObservableCollection<ChangelogItem> Releases { get; } = new();
    [ObservableProperty] private string _source = "";

    public async Task LoadAsync()
    {
        Populate(Changelog.LoadEmbedded());
        Source = "Bundled with this version";

        var remote = await _updates.GetChangelogAsync();
        if (remote is { Count: > 0 })
        {
            Populate(remote);
            Source = "Latest from the update server";
        }
    }

    private void Populate(List<ChangelogEntry> entries)
    {
        Releases.Clear();
        foreach (var e in entries)
            Releases.Add(new ChangelogItem
            {
                Header = $"v{e.Version}" + (string.IsNullOrWhiteSpace(e.Date) ? "" : $"   ·   {e.Date}"),
                Changes = new ObservableCollection<string>(e.Changes ?? Array.Empty<string>()),
            });
    }

    [RelayCommand] private Task Refresh() => LoadAsync();
}

/// <summary>One release block in the Release-notes list.</summary>
public sealed class ChangelogItem
{
    public required string Header { get; init; }
    public required ObservableCollection<string> Changes { get; init; }
}

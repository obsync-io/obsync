using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Obsync.Data.Repositories;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>Recent run history across all jobs.</summary>
public sealed partial class HistoryViewModel : ObservableObject, IAsyncViewModel
{
    private readonly IRunRepository _runs;

    public ObservableCollection<SyncRun> Runs { get; } = [];

    public HistoryViewModel(IRunRepository runs) => _runs = runs;

    public async Task LoadAsync()
    {
        var runs = await _runs.GetRecentAsync(100);
        Runs.Clear();
        foreach (var run in runs)
        {
            Runs.Add(run);
        }
    }
}

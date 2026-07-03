using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.ViewModels;

/// <summary>
/// Resolves the human-readable Source (SQL Server) and Destination (repository · branch) shown in
/// the Dashboard/Jobs tables by joining each job to its connection and repository profiles. The
/// values are written to the jobs' transient display fields before they are bound.
/// </summary>
internal static class JobDisplay
{
    public static async Task PopulateAsync(
        IReadOnlyList<SyncJob> jobs,
        IConnectionProfileRepository connections,
        IRepositoryProfileRepository repositories)
    {
        if (jobs.Count == 0)
        {
            return;
        }

        var connectionsById = (await connections.GetAllAsync()).ToDictionary(c => c.Id);
        var repositoriesById = (await repositories.GetAllAsync()).ToDictionary(r => r.Id);

        foreach (var job in jobs)
        {
            job.SourceDisplay = connectionsById.TryGetValue(job.ConnectionProfileId, out var connection)
                ? connection.ServerName
                : job.DatabasesDisplay;

            // Export Only jobs have no repository — show the export destination instead.
            if (job.CommitMode == CommitMode.ExportOnly)
            {
                job.DestinationDisplay = string.IsNullOrWhiteSpace(job.ExportPath) ? "Export" : $"Export → {job.ExportPath}";
            }
            else
            {
                job.DestinationDisplay = job.RepositoryProfileId is { } repoId && repositoriesById.TryGetValue(repoId, out var repository)
                    ? $"{repository.FullName} · {(string.IsNullOrWhiteSpace(job.Branch) ? repository.DefaultBranch : job.Branch)}"
                    : "—";
            }
        }
    }
}

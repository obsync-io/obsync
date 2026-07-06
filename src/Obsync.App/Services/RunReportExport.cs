using System.IO;
using Obsync.Data.Repositories;
using Obsync.Shared.Models;

namespace Obsync.App.Services;

/// <summary>
/// Shared "Export report…" flow for the Job Workspace and History screens: prompts for a location,
/// picks the format from the chosen file type, and streams the report to the file. Kept in one place
/// so both callers behave identically.
/// </summary>
internal static class RunReportExport
{
    private const string Filter =
        "HTML report (*.html)|*.html|CSV changes (*.csv)|*.csv|JSON report (*.json)|*.json";

    /// <summary>
    /// Shows the save dialog and writes the report. Returns a user-facing status message, or
    /// <c>null</c> if the user cancelled.
    /// </summary>
    public static async Task<string?> PromptAndWriteAsync(IRunReportWriter writer, IRunRepository runs, SyncRun run)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"obsync-report-{Slug(run.JobName)}-{run.RunKey}.html",
            Filter = Filter,
            DefaultExt = ".html",
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        try
        {
            var format = FormatFor(dialog.FileName, dialog.FilterIndex);
            var generatedAt = DateTimeOffset.UtcNow;

            // The report always contains the run's complete change and log sets — a VLDB run can
            // carry hundreds of thousands of changes, so the fetch and the streamed write both stay
            // off the UI thread.
            await Task.Run(async () =>
            {
                var changes = await runs.GetChangesAsync(run.Id).ConfigureAwait(false);
                var logs = await runs.GetLogsAsync(run.Id).ConfigureAwait(false);
                var stream = File.Create(dialog.FileName);
                await using (stream.ConfigureAwait(false))
                {
                    await writer.WriteAsync(format, stream, run, changes, logs, generatedAt).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            return $"Report saved to {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            return $"Could not save the report — {ex.Message}";
        }
    }

    // Prefer the chosen extension (robust when the user types one); fall back to the filter index.
    private static ReportFormat FormatFor(string path, int filterIndex)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return ReportFormat.Csv;
        }

        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return ReportFormat.Json;
        }

        if (ext.Equals(".html", StringComparison.OrdinalIgnoreCase) || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            return ReportFormat.Html;
        }

        return filterIndex switch
        {
            2 => ReportFormat.Csv,
            3 => ReportFormat.Json,
            _ => ReportFormat.Html,
        };
    }

    // A filesystem-friendly slug from the job name for the default file name.
    private static string Slug(string name)
    {
        var normalized = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? "job" : string.Join('-', tokens);
    }
}

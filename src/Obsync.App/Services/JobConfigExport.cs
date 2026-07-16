using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.Services;

/// <summary>
/// Shared "Export configuration…" flow for the Jobs page and the Job Workspace: prompts for a
/// destination, writes the porter's secret-free JSON, and audits the export. Kept in one place so
/// both callers behave identically.
/// </summary>
internal static class JobConfigExport
{
    /// <summary>
    /// Shows the save dialog and writes the export. Returns a user-facing status message, or
    /// <c>null</c> if the user cancelled.
    /// </summary>
    public static async Task<string?> PromptAndWriteAsync(IJobConfigPorter porter, IAuditWriter audit, SyncJob job)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var stem = new string([.. job.Name.Select(ch => invalid.Contains(ch) ? '_' : ch)]);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{stem}.obsync-job.json",
            Filter = "Obsync job export (*.obsync-job.json)|*.obsync-job.json|All files (*.*)|*.*",
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        try
        {
            var json = await porter.ExportAsync(job);
            await System.IO.File.WriteAllTextAsync(dialog.FileName, json);
            await audit.WriteAsync(AuditAction.JobExported, "Job", job.Id.ToString(), job.Name);
            return $"Configuration exported to {dialog.FileName}. Passwords and tokens are never included.";
        }
        catch (Exception ex)
        {
            return $"Export failed — {ex.Message}";
        }
    }
}

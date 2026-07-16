using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.Services;

/// <summary>
/// "Export audit log…" flow for the Settings screen: prompts for a location, picks CSV or JSON from
/// the chosen file type, and writes the COMPLETE audit trail — not just the recent entries shown on
/// screen. Draws only from the persisted audit_log table, which never contains secrets.
/// </summary>
internal static class AuditLogExport
{
    private const string Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Shows the save dialog and writes the export. Returns a user-facing status message, or
    /// <c>null</c> if the user cancelled.
    /// </summary>
    public static async Task<string?> PromptAndWriteAsync(IAuditWriter audit)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "obsync-audit-log.csv",
            Filter = Filter,
            DefaultExt = ".csv",
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        try
        {
            var asJson = IsJson(dialog.FileName, dialog.FilterIndex);

            // The full trail can hold years of events — fetch and write off the UI thread.
            await Task.Run(async () =>
            {
                var events = await audit.GetAllAsync().ConfigureAwait(false);
                var stream = File.Create(dialog.FileName);
                await using (stream.ConfigureAwait(false))
                {
                    if (asJson)
                    {
                        await WriteJsonAsync(stream, events).ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteCsvAsync(stream, events).ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);

            // The export itself is an audited action (compliance data leaving the app).
            await audit.WriteAsync(
                AuditAction.AuditLogExported, "AuditLog", null, null, asJson ? "JSON" : "CSV").ConfigureAwait(false);

            return $"Audit log saved to {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            return $"Could not export the audit log — {ex.Message}";
        }
    }

    internal static Task WriteJsonAsync(Stream destination, IReadOnlyList<AuditEvent> events) =>
        JsonSerializer.SerializeAsync(destination, events, Json);

    internal static async Task WriteCsvAsync(Stream destination, IReadOnlyList<AuditEvent> events)
    {
        // UTF-8 without BOM (StreamWriter default), CRLF rows per RFC 4180.
        var writer = new StreamWriter(destination) { NewLine = "\r\n" };
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteLineAsync("Id,OccurredAt,Actor,Action,EntityType,EntityId,EntityName,Detail").ConfigureAwait(false);
            foreach (var e in events)
            {
                await writer.WriteLineAsync(
                    $"{e.Id},{e.OccurredAt:O},{Csv(e.Actor)},{e.Action},{Csv(e.EntityType)}," +
                    $"{Csv(e.EntityId ?? string.Empty)},{Csv(e.EntityName ?? string.Empty)},{Csv(e.Detail ?? string.Empty)}")
                    .ConfigureAwait(false);
            }
        }
    }

    // RFC 4180: quote a field only when it contains a comma, quote, or newline; escape quotes by doubling.
    private static string Csv(string value) =>
        value.IndexOfAny(['"', ',', '\n', '\r']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    // Prefer the chosen extension (robust when the user types one); fall back to the filter index.
    private static bool IsJson(string path, int filterIndex)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return filterIndex == 2;
    }
}

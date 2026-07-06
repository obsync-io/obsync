using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.Services;

/// <summary>The file format a run report is rendered in.</summary>
public enum ReportFormat
{
    /// <summary>A self-contained, formatted HTML document (summary + changes + logs).</summary>
    Html,

    /// <summary>A comma-separated table with one row per changed object.</summary>
    Csv,

    /// <summary>A structured JSON document (summary + changes + logs).</summary>
    Json,
}

/// <summary>
/// Renders a single run — its summary, per-object changes, and log timeline — as a shareable
/// report, written incrementally to the destination stream so a VLDB run's hundreds of thousands
/// of changes never materialize as one document string. Pure (no secrets): it reads only the
/// already-persisted, user-facing run data the caller supplies.
/// </summary>
public interface IRunReportWriter
{
    Task WriteAsync(
        ReportFormat format,
        Stream destination,
        SyncRun run,
        IReadOnlyList<ObjectChange> changes,
        IReadOnlyList<SyncRunLog> logs,
        DateTimeOffset generatedAt);
}

/// <inheritdoc cref="IRunReportWriter" />
public sealed class RunReportWriter : IRunReportWriter
{
    private const string Crlf = "\r\n";

    // Matches File.WriteAllTextAsync's default (UTF-8, no byte-order mark).
    private static readonly Encoding Utf8NoBom = new UTF8Encoding();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task WriteAsync(
        ReportFormat format,
        Stream destination,
        SyncRun run,
        IReadOnlyList<ObjectChange> changes,
        IReadOnlyList<SyncRunLog> logs,
        DateTimeOffset generatedAt)
    {
        switch (format)
        {
            case ReportFormat.Json:
                await WriteJsonAsync(destination, run, changes, logs, generatedAt).ConfigureAwait(false);
                break;
            case ReportFormat.Csv:
            case ReportFormat.Html:
                var writer = new StreamWriter(destination, Utf8NoBom, bufferSize: -1, leaveOpen: true);
                await using (writer.ConfigureAwait(false))
                {
                    if (format == ReportFormat.Csv)
                    {
                        WriteCsv(writer, changes);
                    }
                    else
                    {
                        WriteHtml(writer, run, changes, logs, generatedAt);
                    }
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown report format.");
        }
    }

    // ------------------------------------------------------------------ JSON

    private static async Task WriteJsonAsync(
        Stream destination,
        SyncRun run,
        IReadOnlyList<ObjectChange> changes,
        IReadOnlyList<SyncRunLog> logs,
        DateTimeOffset generatedAt)
    {
        // The Changes/Logs projections stay lazy; the serializer streams them to the destination.
        var document = new RunReportDocument(
            generatedAt,
            VersionInfo.Of(typeof(RunReportWriter).Assembly),
            new RunReportSummary(
                run.RunKey, run.JobName, run.Trigger, run.TriggeredBy, run.Status,
                run.ServerName, run.Databases,
                run.StartedAt, run.CompletedAt, run.DurationMs,
                run.ObjectsScanned, run.ObjectsAdded, run.ObjectsModified, run.ObjectsDeleted, run.ObjectsFailed,
                run.CommitSha, run.CommitUrl, run.PullRequestNumber, run.PullRequestUrl, run.ErrorMessage),
            changes.Select(c => new RunReportChange(
                c.ChangeType, c.ObjectType.ToString(), c.QualifiedName, c.RelativePath, c.PreviousHash, c.NewHash)),
            logs.Select(l => new RunReportLogEntry(l.Timestamp, l.Level, l.Message, l.Detail)));

        await JsonSerializer.SerializeAsync(destination, document, Json).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ CSV

    private static void WriteCsv(TextWriter writer, IReadOnlyList<ObjectChange> changes)
    {
        writer.Write("ChangeType,ObjectType,Schema,Name,QualifiedName,RelativePath,PreviousHash,NewHash");
        writer.Write(Crlf);
        foreach (var c in changes)
        {
            writer.Write(Csv(c.ChangeType.ToString()));
            writer.Write(',');
            writer.Write(Csv(c.ObjectType.ToString()));
            writer.Write(',');
            writer.Write(Csv(c.Schema));
            writer.Write(',');
            writer.Write(Csv(c.Name));
            writer.Write(',');
            writer.Write(Csv(c.QualifiedName));
            writer.Write(',');
            writer.Write(Csv(c.RelativePath));
            writer.Write(',');
            writer.Write(Csv(c.PreviousHash ?? string.Empty));
            writer.Write(',');
            writer.Write(Csv(c.NewHash ?? string.Empty));
            writer.Write(Crlf);
        }
    }

    // RFC 4180: quote a field only when it contains a comma, quote, or newline; escape quotes by doubling.
    private static string Csv(string value) =>
        value.IndexOfAny(['"', ',', '\n', '\r']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    // ------------------------------------------------------------------ HTML

    private static void WriteHtml(
        TextWriter writer,
        SyncRun run,
        IReadOnlyList<ObjectChange> changes,
        IReadOnlyList<SyncRunLog> logs,
        DateTimeOffset generatedAt)
    {
        writer.Write("<!doctype html>");
        writer.Write(Crlf);
        writer.Write("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        writer.Write(Crlf);
        writer.Write("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        writer.Write(Crlf);
        writer.Write("<title>Obsync run report — ");
        writer.Write(Enc(run.JobName));
        writer.Write("</title>");
        writer.Write(Crlf);
        writer.Write("<style>");
        writer.Write(Css);
        writer.Write("</style></head><body>");
        writer.Write(Crlf);

        // Header
        writer.Write("<h1>");
        writer.Write(Enc(run.JobName));
        writer.Write("</h1>");
        writer.Write(Crlf);
        writer.Write("<p class=\"sub\">Run <code>");
        writer.Write(Enc(run.RunKey));
        writer.Write("</code> ");
        writer.Write(StatusBadge(run.Status));
        writer.Write("</p>");
        writer.Write(Crlf);

        // Summary
        writer.Write("<table class=\"kv\">");
        writer.Write(Crlf);
        Row(writer, "Status", run.Status.ToString());
        Row(writer, "Source server", run.ServerName);
        Row(writer, "Databases", run.Databases);
        Row(writer, "Trigger", run.Trigger.ToString());
        Row(writer, "Started by", run.TriggeredBy ?? "—");
        Row(writer, "Started", run.StartedAt.LocalDateTime.ToString("F", CultureInfo.CurrentCulture));
        Row(writer, "Completed", run.CompletedAt is { } done ? done.LocalDateTime.ToString("F", CultureInfo.CurrentCulture) : "—");
        Row(writer, "Duration", FormatDuration(run.DurationMs));
        Row(writer, "Objects scanned", run.ObjectsScanned.ToString(CultureInfo.CurrentCulture));
        Row(writer, "Added / Modified / Deleted",
            $"{run.ObjectsAdded.ToString(CultureInfo.CurrentCulture)} / {run.ObjectsModified.ToString(CultureInfo.CurrentCulture)} / {run.ObjectsDeleted.ToString(CultureInfo.CurrentCulture)}");
        Row(writer, "Skipped", run.ObjectsFailed.ToString(CultureInfo.CurrentCulture));
        RowRaw(writer, "Commit", CommitCell(run));
        if (run.PullRequestUrl is { Length: > 0 } prUrl)
        {
            RowRaw(writer, "Pull request", Link(prUrl, run.PullRequestNumber is { } n ? "#" + n.ToString(CultureInfo.CurrentCulture) : prUrl));
        }

        if (run.ErrorMessage is { Length: > 0 } error)
        {
            RowRaw(writer, "Detail", "<span class=\"err\">" + Enc(error) + "</span>");
        }

        writer.Write("</table>");
        writer.Write(Crlf);

        // Changes
        writer.Write("<h2>Changed objects <span class=\"count\">");
        writer.Write(changes.Count.ToString(CultureInfo.CurrentCulture));
        writer.Write("</span></h2>");
        writer.Write(Crlf);
        if (changes.Count == 0)
        {
            writer.Write("<p class=\"empty\">No object changes were recorded for this run.</p>");
            writer.Write(Crlf);
        }
        else
        {
            writer.Write("<table class=\"grid\"><thead><tr><th>Change</th><th>Object</th><th>Type</th><th>Path</th></tr></thead><tbody>");
            writer.Write(Crlf);
            foreach (var c in changes)
            {
                writer.Write("<tr><td>");
                writer.Write(ChangeBadge(c.ChangeType));
                writer.Write("</td><td>");
                writer.Write(Enc(c.QualifiedName));
                writer.Write("</td><td>");
                writer.Write(Enc(c.ObjectType.ToString()));
                writer.Write("</td><td><code>");
                writer.Write(Enc(c.RelativePath));
                writer.Write("</code></td></tr>");
                writer.Write(Crlf);
            }

            writer.Write("</tbody></table>");
            writer.Write(Crlf);
        }

        // Logs
        writer.Write("<h2>Log <span class=\"count\">");
        writer.Write(logs.Count.ToString(CultureInfo.CurrentCulture));
        writer.Write("</span></h2>");
        writer.Write(Crlf);
        if (logs.Count == 0)
        {
            writer.Write("<p class=\"empty\">No log entries were recorded for this run.</p>");
            writer.Write(Crlf);
        }
        else
        {
            writer.Write("<table class=\"grid\"><thead><tr><th>Time</th><th>Level</th><th>Message</th></tr></thead><tbody>");
            writer.Write(Crlf);
            foreach (var l in logs)
            {
                writer.Write("<tr><td class=\"nowrap\">");
                writer.Write(Enc(l.Timestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture)));
                writer.Write("</td><td>");
                writer.Write(Enc(l.Level.ToString()));
                writer.Write("</td><td>");
                writer.Write(Enc(l.Message));
                if (l.Detail is { Length: > 0 } detail)
                {
                    writer.Write("<div class=\"detail\">");
                    writer.Write(Enc(detail));
                    writer.Write("</div>");
                }

                writer.Write("</td></tr>");
                writer.Write(Crlf);
            }

            writer.Write("</tbody></table>");
            writer.Write(Crlf);
        }

        writer.Write("<p class=\"footer\">Generated by Obsync ");
        writer.Write(Enc(VersionInfo.Of(typeof(RunReportWriter).Assembly)));
        writer.Write(" · ");
        writer.Write(Enc(generatedAt.LocalDateTime.ToString("F", CultureInfo.CurrentCulture)));
        writer.Write("</p>");
        writer.Write(Crlf);
        writer.Write("</body></html>");
        writer.Write(Crlf);
    }

    private static void Row(TextWriter writer, string label, string value) =>
        RowRaw(writer, label, Enc(value));

    private static void RowRaw(TextWriter writer, string label, string valueHtml)
    {
        writer.Write("<tr><th>");
        writer.Write(Enc(label));
        writer.Write("</th><td>");
        writer.Write(valueHtml);
        writer.Write("</td></tr>");
        writer.Write(Crlf);
    }

    private static string CommitCell(SyncRun run)
    {
        if (run.CommitSha is not { Length: > 0 } sha)
        {
            return "—";
        }

        var shortSha = sha[..Math.Min(7, sha.Length)];
        return run.CommitUrl is { Length: > 0 } url ? Link(url, shortSha) : "<code>" + Enc(shortSha) + "</code>";
    }

    private static string Link(string url, string text) =>
        "<a href=\"" + Enc(url) + "\">" + Enc(text) + "</a>";

    private static string StatusBadge(RunStatus status) =>
        "<span class=\"badge s-" + status.ToString().ToLowerInvariant() + "\">" + Enc(status.ToString()) + "</span>";

    private static string ChangeBadge(ChangeType change) =>
        "<span class=\"badge c-" + change.ToString().ToLowerInvariant() + "\">" + Enc(change.ToString()) + "</span>";

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string FormatDuration(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        if (ts.TotalHours >= 1)
        {
            return $"{((int)ts.TotalHours).ToString(CultureInfo.CurrentCulture)}h {ts.Minutes.ToString(CultureInfo.CurrentCulture)}m {ts.Seconds.ToString(CultureInfo.CurrentCulture)}s";
        }

        if (ts.TotalMinutes >= 1)
        {
            return $"{ts.Minutes.ToString(CultureInfo.CurrentCulture)}m {ts.Seconds.ToString(CultureInfo.CurrentCulture)}s";
        }

        return $"{ts.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)}s";
    }

    // Self-contained styling — no external fonts, stylesheets, or scripts (safe on a locked-down desktop).
    private const string Css = """
        :root{color-scheme:light}
        *{box-sizing:border-box}
        body{font-family:Segoe UI,system-ui,sans-serif;color:#1b1f24;background:#f6f7f9;margin:0;padding:32px;line-height:1.5}
        h1{font-size:22px;margin:0 0 4px}
        h2{font-size:15px;margin:32px 0 10px;color:#3a4250}
        .sub{color:#6b7280;margin:0 0 20px}
        code{font-family:Cascadia Mono,Consolas,monospace;font-size:.9em;background:#eceef1;padding:1px 5px;border-radius:4px}
        table{border-collapse:collapse;width:100%;background:#fff;border:1px solid #e3e6ea;border-radius:8px;overflow:hidden}
        table.kv{max-width:720px}
        th,td{text-align:left;padding:8px 12px;font-size:13px;vertical-align:top;border-top:1px solid #eef0f2}
        thead th{background:#f0f2f4;border-top:0;font-weight:600;color:#4a5260}
        table.kv th{width:210px;color:#6b7280;font-weight:600;background:#fafbfc}
        tr:first-child th,tr:first-child td{border-top:0}
        a{color:#2563eb;text-decoration:none}
        a:hover{text-decoration:underline}
        .count{color:#8a93a0;font-weight:400;font-size:12px}
        .empty{color:#8a93a0;font-size:13px}
        .err{color:#b42318}
        .detail{color:#6b7280;font-size:12px;margin-top:4px;white-space:pre-wrap}
        .nowrap{white-space:nowrap;color:#6b7280}
        .badge{display:inline-block;padding:2px 9px;border-radius:999px;font-size:11px;font-weight:600}
        .s-succeeded,.c-added{background:#dcfce7;color:#166534}
        .s-nochanges{background:#e5e7eb;color:#374151}
        .s-warning{background:#fef3c7;color:#92400e}
        .s-failed,.s-cancelled{background:#fee2e2;color:#991b1b}
        .s-running,.s-pending{background:#dbeafe;color:#1e40af}
        .c-modified{background:#fef3c7;color:#92400e}
        .c-deleted{background:#fee2e2;color:#991b1b}
        .c-unchanged{background:#e5e7eb;color:#374151}
        .footer{color:#9aa2ad;font-size:11px;margin-top:28px}
        """;
}

// Curated report DTOs — a stable shape that also makes "no secrets" self-evident (the run data holds
// none; passwords/tokens live in Windows Credential Manager).
// Changes/Logs are IEnumerable so the serializer streams them; a VLDB run's change set never
// materializes as a second in-memory list.
internal sealed record RunReportDocument(
    DateTimeOffset GeneratedAtUtc,
    string AppVersion,
    RunReportSummary Run,
    IEnumerable<RunReportChange> Changes,
    IEnumerable<RunReportLogEntry> Logs);

internal sealed record RunReportSummary(
    string RunKey,
    string JobName,
    RunTrigger Trigger,
    string? TriggeredBy,
    RunStatus Status,
    string ServerName,
    string Databases,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long DurationMs,
    int ObjectsScanned,
    int ObjectsAdded,
    int ObjectsModified,
    int ObjectsDeleted,
    int ObjectsFailed,
    string? CommitSha,
    string? CommitUrl,
    int? PullRequestNumber,
    string? PullRequestUrl,
    string? ErrorMessage);

internal sealed record RunReportChange(
    ChangeType ChangeType,
    string ObjectType,
    string QualifiedName,
    string RelativePath,
    string? PreviousHash,
    string? NewHash);

internal sealed record RunReportLogEntry(
    DateTimeOffset Timestamp,
    SyncLogLevel Level,
    string Message,
    string? Detail);

using System.Globalization;
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
/// report string. Pure (no filesystem, no secrets): it reads only the already-persisted,
/// user-facing run data the caller supplies.
/// </summary>
public interface IRunReportWriter
{
    string Build(
        ReportFormat format,
        SyncRun run,
        IReadOnlyList<ObjectChange> changes,
        IReadOnlyList<SyncRunLog> logs,
        DateTimeOffset generatedAt);
}

/// <inheritdoc cref="IRunReportWriter" />
public sealed class RunReportWriter : IRunReportWriter
{
    private const string Crlf = "\r\n";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Build(
        ReportFormat format,
        SyncRun run,
        IReadOnlyList<ObjectChange> changes,
        IReadOnlyList<SyncRunLog> logs,
        DateTimeOffset generatedAt) => format switch
    {
        ReportFormat.Json => BuildJson(run, changes, logs, generatedAt),
        ReportFormat.Csv => BuildCsv(changes),
        ReportFormat.Html => BuildHtml(run, changes, logs, generatedAt),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown report format."),
    };

    // ------------------------------------------------------------------ JSON

    private static string BuildJson(
        SyncRun run,
        IReadOnlyList<ObjectChange> changes,
        IReadOnlyList<SyncRunLog> logs,
        DateTimeOffset generatedAt)
    {
        var document = new RunReportDocument(
            generatedAt,
            VersionInfo.Of(typeof(RunReportWriter).Assembly),
            new RunReportSummary(
                run.RunKey, run.JobName, run.Trigger, run.TriggeredBy, run.Status,
                run.ServerName, run.Databases,
                run.StartedAt, run.CompletedAt, run.DurationMs,
                run.ObjectsScanned, run.ObjectsAdded, run.ObjectsModified, run.ObjectsDeleted, run.ObjectsFailed,
                run.CommitSha, run.CommitUrl, run.PullRequestNumber, run.PullRequestUrl, run.ErrorMessage),
            [.. changes.Select(c => new RunReportChange(
                c.ChangeType, c.ObjectType.ToString(), c.QualifiedName, c.RelativePath, c.PreviousHash, c.NewHash))],
            [.. logs.Select(l => new RunReportLogEntry(l.Timestamp, l.Level, l.Message, l.Detail))]);

        return JsonSerializer.Serialize(document, Json);
    }

    // ------------------------------------------------------------------ CSV

    private static string BuildCsv(IReadOnlyList<ObjectChange> changes)
    {
        var sb = new StringBuilder();
        sb.Append("ChangeType,ObjectType,Schema,Name,QualifiedName,RelativePath,PreviousHash,NewHash").Append(Crlf);
        foreach (var c in changes)
        {
            sb.Append(Csv(c.ChangeType.ToString())).Append(',')
              .Append(Csv(c.ObjectType.ToString())).Append(',')
              .Append(Csv(c.Schema)).Append(',')
              .Append(Csv(c.Name)).Append(',')
              .Append(Csv(c.QualifiedName)).Append(',')
              .Append(Csv(c.RelativePath)).Append(',')
              .Append(Csv(c.PreviousHash ?? string.Empty)).Append(',')
              .Append(Csv(c.NewHash ?? string.Empty)).Append(Crlf);
        }

        return sb.ToString();
    }

    // RFC 4180: quote a field only when it contains a comma, quote, or newline; escape quotes by doubling.
    private static string Csv(string value) =>
        value.IndexOfAny(['"', ',', '\n', '\r']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    // ------------------------------------------------------------------ HTML

    private static string BuildHtml(
        SyncRun run,
        IReadOnlyList<ObjectChange> changes,
        IReadOnlyList<SyncRunLog> logs,
        DateTimeOffset generatedAt)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html>").Append(Crlf)
          .Append("<html lang=\"en\"><head><meta charset=\"utf-8\">").Append(Crlf)
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">").Append(Crlf)
          .Append("<title>Obsync run report — ").Append(Enc(run.JobName)).Append("</title>").Append(Crlf)
          .Append("<style>").Append(Css).Append("</style></head><body>").Append(Crlf);

        // Header
        sb.Append("<h1>").Append(Enc(run.JobName)).Append("</h1>").Append(Crlf)
          .Append("<p class=\"sub\">Run <code>").Append(Enc(run.RunKey)).Append("</code> ")
          .Append(StatusBadge(run.Status)).Append("</p>").Append(Crlf);

        // Summary
        sb.Append("<table class=\"kv\">").Append(Crlf);
        Row(sb, "Status", run.Status.ToString());
        Row(sb, "Source server", run.ServerName);
        Row(sb, "Databases", run.Databases);
        Row(sb, "Trigger", run.Trigger.ToString());
        Row(sb, "Started by", run.TriggeredBy ?? "—");
        Row(sb, "Started", run.StartedAt.LocalDateTime.ToString("F", CultureInfo.CurrentCulture));
        Row(sb, "Completed", run.CompletedAt is { } done ? done.LocalDateTime.ToString("F", CultureInfo.CurrentCulture) : "—");
        Row(sb, "Duration", FormatDuration(run.DurationMs));
        Row(sb, "Objects scanned", run.ObjectsScanned.ToString(CultureInfo.CurrentCulture));
        Row(sb, "Added / Modified / Deleted",
            $"{run.ObjectsAdded.ToString(CultureInfo.CurrentCulture)} / {run.ObjectsModified.ToString(CultureInfo.CurrentCulture)} / {run.ObjectsDeleted.ToString(CultureInfo.CurrentCulture)}");
        Row(sb, "Skipped", run.ObjectsFailed.ToString(CultureInfo.CurrentCulture));
        RowRaw(sb, "Commit", CommitCell(run));
        if (run.PullRequestUrl is { Length: > 0 } prUrl)
        {
            RowRaw(sb, "Pull request", Link(prUrl, run.PullRequestNumber is { } n ? "#" + n.ToString(CultureInfo.CurrentCulture) : prUrl));
        }

        if (run.ErrorMessage is { Length: > 0 } error)
        {
            RowRaw(sb, "Detail", "<span class=\"err\">" + Enc(error) + "</span>");
        }

        sb.Append("</table>").Append(Crlf);

        // Changes
        sb.Append("<h2>Changed objects <span class=\"count\">")
          .Append(changes.Count.ToString(CultureInfo.CurrentCulture)).Append("</span></h2>").Append(Crlf);
        if (changes.Count == 0)
        {
            sb.Append("<p class=\"empty\">No object changes were recorded for this run.</p>").Append(Crlf);
        }
        else
        {
            sb.Append("<table class=\"grid\"><thead><tr><th>Change</th><th>Object</th><th>Type</th><th>Path</th></tr></thead><tbody>").Append(Crlf);
            foreach (var c in changes)
            {
                sb.Append("<tr><td>").Append(ChangeBadge(c.ChangeType))
                  .Append("</td><td>").Append(Enc(c.QualifiedName))
                  .Append("</td><td>").Append(Enc(c.ObjectType.ToString()))
                  .Append("</td><td><code>").Append(Enc(c.RelativePath)).Append("</code></td></tr>").Append(Crlf);
            }

            sb.Append("</tbody></table>").Append(Crlf);
        }

        // Logs
        sb.Append("<h2>Log <span class=\"count\">").Append(logs.Count.ToString(CultureInfo.CurrentCulture)).Append("</span></h2>").Append(Crlf);
        if (logs.Count == 0)
        {
            sb.Append("<p class=\"empty\">No log entries were recorded for this run.</p>").Append(Crlf);
        }
        else
        {
            sb.Append("<table class=\"grid\"><thead><tr><th>Time</th><th>Level</th><th>Message</th></tr></thead><tbody>").Append(Crlf);
            foreach (var l in logs)
            {
                var message = Enc(l.Message);
                if (l.Detail is { Length: > 0 } detail)
                {
                    message += "<div class=\"detail\">" + Enc(detail) + "</div>";
                }

                sb.Append("<tr><td class=\"nowrap\">").Append(Enc(l.Timestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture)))
                  .Append("</td><td>").Append(Enc(l.Level.ToString()))
                  .Append("</td><td>").Append(message).Append("</td></tr>").Append(Crlf);
            }

            sb.Append("</tbody></table>").Append(Crlf);
        }

        sb.Append("<p class=\"footer\">Generated by Obsync ").Append(Enc(VersionInfo.Of(typeof(RunReportWriter).Assembly)))
          .Append(" · ").Append(Enc(generatedAt.LocalDateTime.ToString("F", CultureInfo.CurrentCulture))).Append("</p>").Append(Crlf)
          .Append("</body></html>").Append(Crlf);

        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string label, string value) =>
        sb.Append("<tr><th>").Append(Enc(label)).Append("</th><td>").Append(Enc(value)).Append("</td></tr>").Append(Crlf);

    private static void RowRaw(StringBuilder sb, string label, string valueHtml) =>
        sb.Append("<tr><th>").Append(Enc(label)).Append("</th><td>").Append(valueHtml).Append("</td></tr>").Append(Crlf);

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
internal sealed record RunReportDocument(
    DateTimeOffset GeneratedAtUtc,
    string AppVersion,
    RunReportSummary Run,
    IReadOnlyList<RunReportChange> Changes,
    IReadOnlyList<RunReportLogEntry> Logs);

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

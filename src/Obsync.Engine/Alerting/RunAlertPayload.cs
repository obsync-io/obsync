using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Engine.Alerting;

/// <summary>
/// Builds the deterministic alert content for a finished run: an email subject + plain-text body,
/// and the webhook JSON document. Pure formatting — no secrets, no I/O.
/// </summary>
public static class RunAlertPayload
{
    // The default encoder escapes '+' (mangling "+00:00" timestamp offsets into a \u escape)
    // and non-ASCII. The payload goes to a JSON consumer, not an HTML page — keep it readable.
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The webhook event discriminator: <c>run.failed</c>, <c>run.warning</c>, or <c>run.changes</c>.</summary>
    public static string Event(SyncRun run) => run.Status switch
    {
        RunStatus.Failed => "run.failed",
        RunStatus.Warning => "run.warning",
        _ => "run.changes",
    };

    /// <summary>The email subject line, e.g. <c>[Obsync] Run failed — SalesDB Sync</c>.</summary>
    public static string BuildEmailSubject(SyncRun run) => run.Status switch
    {
        RunStatus.Failed => $"[Obsync] Run failed — {run.JobName}",
        RunStatus.Warning => $"[Obsync] Run finished with warnings — {run.JobName}",
        _ => $"[Obsync] Changes committed — {run.JobName}",
    };

    /// <summary>The plain-text email body: one aligned label/value line per populated field.</summary>
    public static string BuildEmailBody(SyncRun run)
    {
        var builder = new StringBuilder();
        AppendLine(builder, "Job", run.JobName);
        AppendLine(builder, "Server", run.ServerName);
        AppendLine(builder, "Databases", run.Databases);
        AppendLine(builder, "Status", run.Status.ToString());
        AppendLine(builder, "Objects", string.Create(CultureInfo.InvariantCulture,
            $"{run.ObjectsAdded} added, {run.ObjectsModified} modified, {run.ObjectsDeleted} deleted " +
            $"({run.ObjectsScanned:N0} scanned, {run.ObjectsFailed} skipped)"));
        AppendLine(builder, "Started", run.StartedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        AppendLine(builder, "Duration", FormatDuration(run.DurationMs));
        AppendLine(builder, "Error", FirstLine(run.ErrorMessage));
        AppendLine(builder, "Commit", run.CommitUrl);
        AppendLine(builder, "Pull request", run.PullRequestUrl);
        AppendLine(builder, "Tags", run.Tags.Count == 0 ? null : string.Join(", ", run.Tags));
        return builder.ToString();
    }

    /// <summary>
    /// The webhook JSON (camelCase, stable property order, nulls included so the shape is constant).
    /// </summary>
    public static string BuildWebhookJson(SyncRun run) => JsonSerializer.Serialize(new
    {
        @event = Event(run),
        job = run.JobName,
        jobId = run.JobId,
        status = run.Status.ToString(),
        server = run.ServerName,
        databases = run.Databases,
        started = run.StartedAt,
        completed = run.CompletedAt,
        durationMs = run.DurationMs,
        counts = new
        {
            scanned = run.ObjectsScanned,
            added = run.ObjectsAdded,
            modified = run.ObjectsModified,
            deleted = run.ObjectsDeleted,
            failed = run.ObjectsFailed,
        },
        changeCount = run.ChangeCount,
        commitUrl = run.CommitUrl,
        pullRequestUrl = run.PullRequestUrl,
        error = run.ErrorMessage,
        tags = run.Tags,
    }, JsonOptions);

    private static void AppendLine(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append((label + ':').PadRight(14)).AppendLine(value);
        }
    }

    // "45s", "2m 31s", "1h 05m" — coarse on purpose; alerts need a feel, not milliseconds.
    internal static string FormatDuration(long durationMs)
    {
        var time = TimeSpan.FromMilliseconds(durationMs);
        if (time.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}h {time.Minutes:00}m");
        }

        return time.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}m {time.Seconds:00}s")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Seconds}s");
    }

    private static string? FirstLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}

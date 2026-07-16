using System.IO;
using System.Text.RegularExpressions;

namespace Obsync.App.Services;

/// <summary>Severity bucket for the logs panel's filter (finer Serilog levels are mapped in).</summary>
public enum LogSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>One parsed log line (continuation lines — stack traces — are folded into the message).</summary>
/// <param name="Timestamp">The entry's timestamp, or <see cref="DateTimeOffset.MinValue"/> for an orphan continuation line.</param>
/// <param name="Level">Serilog's three-letter level code as written in the file (INF, WRN, ERR, …).</param>
/// <param name="Source">Which log the entry came from: "app" or "service".</param>
public sealed record LogEntry(DateTimeOffset Timestamp, LogSeverity Severity, string Level, string Message, string Source);

/// <summary>
/// Reads the most recent app and service log files for the Settings → Diagnostics logs panel.
/// A faithful parser: messages are returned exactly as written (Serilog call sites never receive
/// secrets by construction, so no redaction happens here — guarded by regression tests).
/// </summary>
public interface ILogFileReader
{
    /// <summary>Entries from the newest app and service log file, newest first, capped per file.</summary>
    Task<IReadOnlyList<LogEntry>> ReadRecentAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ILogFileReader" />
public sealed class LogFileReader : ILogFileReader
{
    /// <summary>Per-file cap — enough for "what just happened" without loading a whole day.</summary>
    internal const int MaxLinesPerFile = 500;

    // Serilog's default file format: "2026-07-16 09:41:22.123 +02:00 [INF] message".
    private static readonly Regex EntryStart = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(?<level>[A-Z]{3})\] (?<message>.*)$",
        RegexOptions.Compiled);

    private readonly string _logsRoot;

    public LogFileReader(string logsRoot) => _logsRoot = logsRoot;

    public Task<IReadOnlyList<LogEntry>> ReadRecentAsync(CancellationToken cancellationToken = default) =>
        Task.Run(ReadCore, cancellationToken);

    private IReadOnlyList<LogEntry> ReadCore()
    {
        if (!Directory.Exists(_logsRoot))
        {
            return [];
        }

        var entries = new List<LogEntry>();
        foreach (var source in new[] { "app", "service" })
        {
            var newest = new DirectoryInfo(_logsRoot)
                .EnumerateFiles($"{source}-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is not null)
            {
                entries.AddRange(Parse(TailLines(newest.FullName, MaxLinesPerFile), source));
            }
        }

        return [.. entries.OrderByDescending(e => e.Timestamp)];
    }

    /// <summary>
    /// Parses raw log lines into entries. Lines that do not start a new entry (exception stack
    /// traces, wrapped messages) are folded into the previous entry's message; an orphan
    /// continuation at the start of the window becomes its own entry rather than being dropped.
    /// </summary>
    internal static IReadOnlyList<LogEntry> Parse(IReadOnlyList<string> lines, string source)
    {
        var entries = new List<LogEntry>();
        foreach (var line in lines)
        {
            var match = EntryStart.Match(line);
            if (match.Success && DateTimeOffset.TryParse(
                    match.Groups["ts"].Value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var timestamp))
            {
                var level = match.Groups["level"].Value;
                entries.Add(new LogEntry(timestamp, ToSeverity(level), level, match.Groups["message"].Value, source));
            }
            else if (entries.Count > 0)
            {
                var previous = entries[^1];
                entries[^1] = previous with { Message = $"{previous.Message}\n{line}" };
            }
            else if (line.Length > 0)
            {
                entries.Add(new LogEntry(DateTimeOffset.MinValue, LogSeverity.Information, "···", line, source));
            }
        }

        return entries;
    }

    // ERR and FTL both surface as Error; VRB/DBG (not produced at the current minimum level) as Information.
    private static LogSeverity ToSeverity(string level) => level switch
    {
        "ERR" or "FTL" => LogSeverity.Error,
        "WRN" => LogSeverity.Warning,
        _ => LogSeverity.Information,
    };

    // Serilog holds today's file open for writing — the stream must allow ReadWrite sharing.
    private static IReadOnlyList<string> TailLines(string path, int maxLines)
    {
        var lines = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.Count <= maxLines ? lines : lines[^maxLines..];
    }
}

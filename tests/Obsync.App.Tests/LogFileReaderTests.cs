using System.IO;
using Obsync.App.Services;

namespace Obsync.App.Tests;

/// <summary>
/// The logs-panel reader must parse Serilog's default file format faithfully (fold continuation
/// lines, map levels) and read only the newest app/service file, capped. Faithful means verbatim:
/// redaction is guaranteed at the logging call sites (messages never receive secrets by
/// construction), never by the viewer — asserted here so a "sanitizing" reader can't hide a leak.
/// </summary>
public sealed class LogFileReaderTests
{
    [Fact]
    public void Parse_MapsSerilogLevels_ToSeverities()
    {
        string[] lines =
        [
            "2026-07-16 09:00:00.000 +02:00 [INF] started",
            "2026-07-16 09:00:01.000 +02:00 [WRN] slow",
            "2026-07-16 09:00:02.000 +02:00 [ERR] failed",
            "2026-07-16 09:00:03.000 +02:00 [FTL] crashed",
        ];

        var entries = LogFileReader.Parse(lines, "app");

        Assert.Equal(4, entries.Count);
        Assert.Equal(LogSeverity.Information, entries[0].Severity);
        Assert.Equal(LogSeverity.Warning, entries[1].Severity);
        Assert.Equal(LogSeverity.Error, entries[2].Severity);
        Assert.Equal(LogSeverity.Error, entries[3].Severity); // FTL surfaces as Error
        Assert.Equal(new DateTimeOffset(2026, 7, 16, 9, 0, 0, TimeSpan.FromHours(2)), entries[0].Timestamp);
        Assert.All(entries, e => Assert.Equal("app", e.Source));
    }

    [Fact]
    public void Parse_FoldsContinuationLines_IntoThePreviousEntry()
    {
        string[] lines =
        [
            "2026-07-16 09:00:00.000 +02:00 [ERR] sync failed",
            "System.InvalidOperationException: boom",
            "   at Obsync.Engine.SyncEngine.RunAsync()",
            "2026-07-16 09:00:05.000 +02:00 [INF] next run scheduled",
        ];

        var entries = LogFileReader.Parse(lines, "service");

        Assert.Equal(2, entries.Count);
        Assert.Equal(
            "sync failed\nSystem.InvalidOperationException: boom\n   at Obsync.Engine.SyncEngine.RunAsync()",
            entries[0].Message);
        Assert.Equal("next run scheduled", entries[1].Message);
    }

    [Fact]
    public void Parse_AnOrphanContinuationAtTheStart_BecomesItsOwnEntry()
    {
        string[] lines =
        [
            "   at Some.Stack.Frame()", // the 500-line window can start mid-exception
            "2026-07-16 09:00:00.000 +02:00 [INF] ok",
        ];

        var entries = LogFileReader.Parse(lines, "app");

        Assert.Equal(2, entries.Count);
        Assert.Equal("   at Some.Stack.Frame()", entries[0].Message);
        Assert.Equal(DateTimeOffset.MinValue, entries[0].Timestamp);
    }

    [Fact]
    public void Parse_IsFaithful_TokenLookingStringsComeBackVerbatim()
    {
        // If a secret ever DID reach a log file, the viewer must show it verbatim so the leak is
        // visible and fixable at its source — a quietly redacting viewer would mask the defect.
        const string line = "2026-07-16 09:00:00.000 +02:00 [WRN] auth retry with token=ghp_161lHFAKEFAKEFAKE and password=hunter2";

        var entries = LogFileReader.Parse([line], "app");

        var entry = Assert.Single(entries);
        Assert.Equal("auth retry with token=ghp_161lHFAKEFAKEFAKE and password=hunter2", entry.Message);
    }

    [Fact]
    public async Task ReadRecentAsync_ReadsOnlyTheNewestFilePerSource_NewestEntriesFirst_Capped()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"obsync-logs-{Guid.NewGuid():N}")).FullName;
        try
        {
            // An older app file that must be ignored, plus a newer one exceeding the line cap.
            File.WriteAllLines(Path.Combine(root, "app-20260714.log"),
                ["2026-07-14 08:00:00.000 +02:00 [INF] old-file-entry"]);
            var newLines = Enumerable.Range(0, LogFileReader.MaxLinesPerFile + 50)
                .Select(i => $"2026-07-16 09:{i / 60:00}:{i % 60:00}.000 +02:00 [INF] app entry {i}");
            File.WriteAllLines(Path.Combine(root, "app-20260716.log"), newLines);
            File.SetLastWriteTimeUtc(Path.Combine(root, "app-20260714.log"), DateTime.UtcNow.AddDays(-2));
            File.WriteAllLines(Path.Combine(root, "service-20260716.log"),
                ["2026-07-16 23:00:00.000 +02:00 [WRN] service entry"]);

            var entries = await new LogFileReader(root).ReadRecentAsync();

            Assert.Equal(LogFileReader.MaxLinesPerFile + 1, entries.Count); // capped app file + 1 service line
            Assert.DoesNotContain(entries, e => e.Message == "old-file-entry");
            Assert.DoesNotContain(entries, e => e.Message == "app entry 0"); // dropped by the tail cap
            Assert.Equal("service entry", entries[0].Message);               // newest first across sources
            Assert.Contains(entries, e => e.Source == "app");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadRecentAsync_MissingLogsFolder_ReturnsEmpty()
    {
        var reader = new LogFileReader(Path.Combine(Path.GetTempPath(), $"obsync-none-{Guid.NewGuid():N}"));

        Assert.Empty(await reader.ReadRecentAsync());
    }
}

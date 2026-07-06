using System.IO;
using System.Text;
using System.Text.Json;
using Obsync.App.Services;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The run-report writer must render a run faithfully in each format, escape untrusted names, and
/// never expose a secret field. Pure (no database, no filesystem) — it works off supplied run data,
/// streamed to whatever destination the caller provides.
/// </summary>
public sealed class RunReportWriterTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 7, 2, 9, 30, 0, TimeSpan.Zero);
    private readonly RunReportWriter _writer = new();

    /// <summary>Streams the report into memory and decodes it, so the content assertions read the
    /// exact bytes a file export would contain.</summary>
    private async Task<string> WriteAsync(
        ReportFormat format, SyncRun run, IReadOnlyList<ObjectChange> changes, IReadOnlyList<SyncRunLog> logs)
    {
        using var stream = new MemoryStream();
        await _writer.WriteAsync(format, stream, run, changes, logs, GeneratedAt);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static SyncRun SampleRun() => new()
    {
        RunKey = "20260702-093000",
        JobName = "Prod Sync",
        Trigger = RunTrigger.Manual,
        TriggeredBy = @"ACME\svc_reader",
        Status = RunStatus.Succeeded,
        ServerName = "PROD-SQL01",
        Databases = "SalesDB",
        StartedAt = GeneratedAt,
        CompletedAt = GeneratedAt.AddSeconds(42),
        DurationMs = 42_000,
        ObjectsScanned = 120,
        ObjectsAdded = 2,
        ObjectsModified = 1,
        ObjectsDeleted = 0,
        ObjectsFailed = 0,
        CommitSha = "abcdef1234567890",
        CommitUrl = "https://github.com/acme/sql-history/commit/abcdef1234567890",
    };

    private static List<ObjectChange> SampleChanges() =>
    [
        new() { ChangeType = ChangeType.Added, ObjectType = SqlObjectType.StoredProcedure, Schema = "dbo", Name = "usp_GetCustomer", RelativePath = "procedures/dbo.usp_GetCustomer.sql", NewHash = "aaa" },
        new() { ChangeType = ChangeType.Modified, ObjectType = SqlObjectType.View, Schema = "dbo", Name = "vSales", RelativePath = "views/dbo.vSales.sql", PreviousHash = "old", NewHash = "new" },
    ];

    private static List<SyncRunLog> SampleLogs() =>
    [
        new() { Timestamp = GeneratedAt, Level = SyncLogLevel.Info, Message = "Scanned 120 objects" },
        new() { Timestamp = GeneratedAt.AddSeconds(1), Level = SyncLogLevel.Warning, Message = "2 objects skipped", Detail = "dbo.enc_proc: encrypted" },
    ];

    [Fact]
    public async Task Json_IsStructured_WithReadableEnums_AndNoSecrets()
    {
        var json = await WriteAsync(ReportFormat.Json, SampleRun(), SampleChanges(), SampleLogs());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("20260702-093000", root.GetProperty("Run").GetProperty("RunKey").GetString());
        Assert.Equal("Succeeded", root.GetProperty("Run").GetProperty("Status").GetString());   // enum as string
        Assert.Equal("Manual", root.GetProperty("Run").GetProperty("Trigger").GetString());

        var changes = root.GetProperty("Changes");
        Assert.Equal(2, changes.GetArrayLength());
        Assert.Equal("Added", changes[0].GetProperty("ChangeType").GetString());
        Assert.Equal("dbo.usp_GetCustomer", changes[0].GetProperty("QualifiedName").GetString());

        Assert.Equal(2, root.GetProperty("Logs").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("AppVersion").GetString()));

        // The run data holds no secrets; guard against a field literally named Password/Token/Secret.
        Assert.DoesNotContain("\"Password\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Token\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Secret\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Csv_HasHeader_AndOneRowPerChange()
    {
        var csv = await WriteAsync(ReportFormat.Csv, SampleRun(), SampleChanges(), SampleLogs());

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // header + 2 changes
        Assert.StartsWith("ChangeType,ObjectType,Schema,Name,QualifiedName,RelativePath", lines[0]);
        Assert.Contains("Added,StoredProcedure,dbo,usp_GetCustomer,dbo.usp_GetCustomer", lines[1]);
    }

    [Fact]
    public async Task Csv_QuotesAndEscapes_FieldsWithCommasAndQuotes()
    {
        List<ObjectChange> changes =
        [
            new() { ChangeType = ChangeType.Added, ObjectType = SqlObjectType.Table, Schema = "dbo", Name = "Odd,\"Name", RelativePath = "tables/x.sql" },
        ];

        var csv = await WriteAsync(ReportFormat.Csv, SampleRun(), changes, []);

        // Embedded comma forces quoting; embedded quote is doubled.
        Assert.Contains("\"Odd,\"\"Name\"", csv);
    }

    [Fact]
    public async Task Csv_WithNoChanges_IsHeaderOnly()
    {
        var csv = await WriteAsync(ReportFormat.Csv, SampleRun(), [], []);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
    }

    [Fact]
    public async Task Html_IsSelfContained_AndEncodesUntrustedNames()
    {
        List<ObjectChange> changes =
        [
            new() { ChangeType = ChangeType.Added, ObjectType = SqlObjectType.Table, Schema = "dbo", Name = "<script>x", RelativePath = "tables/x.sql" },
        ];

        var html = await WriteAsync(ReportFormat.Html, SampleRun(), changes, SampleLogs());

        // StartsWith also guards against a byte-order mark sneaking into the streamed output.
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("Prod Sync", html);
        Assert.Contains("dbo.&lt;script&gt;x", html);        // object name HTML-encoded
        Assert.DoesNotContain("<script>x", html, StringComparison.Ordinal); // never raw
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);  // no external stylesheet
        Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);   // no external script/image/font
    }

    [Fact]
    public async Task Html_IsDeterministic_ForFixedGeneratedAt()
    {
        var run = SampleRun();
        var a = await WriteAsync(ReportFormat.Html, run, SampleChanges(), SampleLogs());
        var b = await WriteAsync(ReportFormat.Html, run, SampleChanges(), SampleLogs());

        Assert.Equal(a, b);
    }
}

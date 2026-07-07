using System.IO;
using System.Text;
using System.Text.Json;
using Obsync.App.Services;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.App.Tests;

/// <summary>The audit-log export writers: CSV field escaping and the JSON document shape.</summary>
public sealed class AuditLogExportTests
{
    private static readonly AuditEvent[] Events =
    [
        new()
        {
            Id = 2,
            OccurredAt = new DateTimeOffset(2026, 7, 6, 10, 30, 0, TimeSpan.Zero),
            Actor = @"CONTOSO\alice",
            Action = AuditAction.RunFailed,
            EntityType = "Job",
            EntityId = "a2f1c9d0-0000-0000-0000-000000000000",
            EntityName = "Sales, \"nightly\" sync",
            Detail = "Scheduled run 20260706-103000 failed — push rejected\r\nnon-fast-forward",
        },
        new()
        {
            Id = 1,
            OccurredAt = new DateTimeOffset(2026, 7, 5, 9, 0, 0, TimeSpan.Zero),
            Actor = @"CONTOSO\bob",
            Action = AuditAction.JobCreated,
            EntityType = "Job",
            EntityId = null,
            EntityName = null,
            Detail = null,
        },
    ];

    [Fact]
    public async Task Csv_EscapesCommasQuotesAndNewlines_AndKeepsPlainFieldsBare()
    {
        using var stream = new MemoryStream();
        await AuditLogExport.WriteCsvAsync(stream, Events);
        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split("\r\n");

        Assert.Equal("Id,OccurredAt,Actor,Action,EntityType,EntityId,EntityName,Detail", lines[0]);
        // The name's comma + quotes are escaped; the detail's embedded CRLF keeps the field quoted,
        // so the record spans two physical lines.
        Assert.StartsWith(
            "2,2026-07-06T10:30:00.0000000+00:00,CONTOSO\\alice,RunFailed,Job," +
            "a2f1c9d0-0000-0000-0000-000000000000,\"Sales, \"\"nightly\"\" sync\",\"Scheduled run 20260706-103000 failed — push rejected",
            lines[1]);
        Assert.Equal("non-fast-forward\"", lines[2]);
        Assert.Equal("1,2026-07-05T09:00:00.0000000+00:00,CONTOSO\\bob,JobCreated,Job,,,", lines[3]);
    }

    [Fact]
    public async Task Json_WritesEnumNamesAndAllFields()
    {
        using var stream = new MemoryStream();
        await AuditLogExport.WriteJsonAsync(stream, Events);
        stream.Position = 0;

        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        Assert.Equal(2, root.GetArrayLength());
        var first = root[0];
        Assert.Equal("RunFailed", first.GetProperty("Action").GetString());
        Assert.Equal(@"CONTOSO\alice", first.GetProperty("Actor").GetString());
        Assert.Equal("Sales, \"nightly\" sync", first.GetProperty("EntityName").GetString());
        Assert.True(root[1].GetProperty("EntityId").ValueKind == JsonValueKind.Null);
    }
}

using System.Text.Json;
using Obsync.Engine.Alerting;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Engine.Tests;

/// <summary>
/// A re-sent alert must be recognisable as the same alert.
/// </summary>
/// <remarks>
/// Delivery is retried once on any failure, including a timeout — and a timeout cannot distinguish
/// "the endpoint never received it" from "the endpoint received it and the acknowledgement was
/// lost". A duplicate email is untidy; a duplicate POST to PagerDuty, ServiceNow or Jira opens a
/// second incident for one event. Nothing in the payload let a receiver tell a re-send from a new
/// event, so nothing could dedupe.
/// </remarks>
public sealed class AlertIdempotencyTests
{
    private static SyncRun Run() => new()
    {
        JobName = "SalesDB",
        Status = RunStatus.Failed,
        StartedAt = DateTimeOffset.UtcNow,
        RunKey = "20260906-173000",
    };

    [Fact]
    public void TheKey_IsStableForOneAlert()
    {
        // The whole point: two attempts at the same alert must carry the same key.
        var run = Run();
        Assert.Equal(RunAlertPayload.IdempotencyKey(run), RunAlertPayload.IdempotencyKey(run));
    }

    [Fact]
    public void TheKey_DiffersBetweenRuns()
    {
        // ...and a genuinely new event must not be collapsed onto the previous one.
        Assert.NotEqual(RunAlertPayload.IdempotencyKey(Run()), RunAlertPayload.IdempotencyKey(Run()));
    }

    [Fact]
    public void TheKey_IsSafeForAnHttpHeader()
    {
        // It is sent as Idempotency-Key; a value needing quoting or encoding would be rejected or
        // silently mangled by some receivers.
        var key = RunAlertPayload.IdempotencyKey(Run());

        Assert.NotEmpty(key);
        Assert.All(key, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c == '-', $"Unsafe character '{c}'."));
    }

    [Fact]
    public void ThePayload_CarriesTheKeyAndTheRunIdentity()
    {
        // Receivers that cannot read headers (a generic webhook relay, an automation platform) still
        // need something stable to dedupe on.
        var run = Run();
        using var json = JsonDocument.Parse(RunAlertPayload.BuildWebhookJson(run));
        var root = json.RootElement;

        Assert.Equal(RunAlertPayload.IdempotencyKey(run), root.GetProperty("idempotencyKey").GetString());
        Assert.Equal(run.Id, root.GetProperty("runId").GetGuid());
        Assert.Equal(run.RunKey, root.GetProperty("runKey").GetString());
    }

    [Fact]
    public void ThePayload_StillCarriesWhatItAlwaysDid()
    {
        // Adding fields must not have disturbed the existing contract — these are consumed by
        // whatever the customer wired the webhook to.
        using var json = JsonDocument.Parse(RunAlertPayload.BuildWebhookJson(Run()));
        var root = json.RootElement;

        foreach (var field in new[] { "event", "job", "jobId", "status", "started", "counts", "changeCount" })
        {
            Assert.True(root.TryGetProperty(field, out _), $"The webhook payload lost '{field}'.");
        }
    }
}

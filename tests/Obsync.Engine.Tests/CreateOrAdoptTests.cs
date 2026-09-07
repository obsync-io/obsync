using System.Net.Http;
using Obsync.GitHub;

namespace Obsync.Engine.Tests;

/// <summary>
/// The rule that a lost response must not be read as a failed operation.
/// </summary>
/// <remarks>
/// From a real incident: a pull request was created on GitHub, the TLS connection dropped before its
/// response came back, the transport error looked transient, and the run reported that the pull
/// request could not be opened — while it sat open on GitHub. The run's objects were therefore never
/// marked delivered, so the next run would have cut a fresh head branch and opened a second pull
/// request for the same content, once per run, indefinitely.
/// </remarks>
public sealed class CreateOrAdoptTests
{
    private sealed record Thing(string Id);

    private static Task NoDelay(int _) => Task.CompletedTask;
    private static bool Transient(Exception _) => true;
    private static bool Permanent(Exception _) => false;

    [Fact]
    public async Task TheHappyPath_CreatesOnceAndLooksUpNothing()
    {
        var creates = 0;
        var lookups = 0;

        var result = await CreateOrAdopt.ExecuteAsync(
            create: () => { creates++; return Task.FromResult(new Thing("made")); },
            findExisting: _ => { lookups++; return Task.FromResult<Thing?>(null); },
            isTransient: Transient,
            delayBeforeRetry: NoDelay);

        Assert.Equal("made", result.Id);
        Assert.Equal(1, creates);
        Assert.Equal(0, lookups);
    }

    [Fact]
    public async Task ALostResponse_AdoptsWhatTheServerAlreadyHas()
    {
        // The incident, exactly: the create took effect and the reply never arrived.
        var creates = 0;

        var result = await CreateOrAdopt.ExecuteAsync<Thing>(
            create: () =>
            {
                creates++;
                throw new HttpRequestException("The SSL connection could not be established.");
            },
            findExisting: _ => Task.FromResult<Thing?>(new Thing("already-there")),
            isTransient: Transient,
            delayBeforeRetry: NoDelay);

        Assert.Equal("already-there", result.Id);

        // And critically it did NOT try again — a retry after a silently-successful create is what
        // produces the duplicate.
        Assert.Equal(1, creates);
    }

    [Fact]
    public async Task ADuplicateRejection_IsAlsoReconciled()
    {
        // The same situation seen from the other side: the first attempt landed, and the server now
        // refuses a second because the object exists.
        var result = await CreateOrAdopt.ExecuteAsync<Thing>(
            create: () => throw new InvalidOperationException("422: a pull request already exists"),
            findExisting: _ => Task.FromResult<Thing?>(new Thing("existing")),
            isTransient: Permanent,
            delayBeforeRetry: NoDelay);

        Assert.Equal("existing", result.Id);
    }

    [Fact]
    public async Task ATransientFailureThatLeftNothingBehind_IsRetried()
    {
        var creates = 0;

        var result = await CreateOrAdopt.ExecuteAsync(
            create: () =>
            {
                creates++;
                return creates == 1
                    ? throw new HttpRequestException("connection reset")
                    : Task.FromResult(new Thing("second-try"));
            },
            findExisting: _ => Task.FromResult<Thing?>(null),
            isTransient: Transient,
            delayBeforeRetry: NoDelay);

        Assert.Equal("second-try", result.Id);
        Assert.Equal(2, creates);
    }

    [Fact]
    public async Task WhenNothingExistsAndNothingWorks_TheOriginalFailureSurfaces()
    {
        // Rethrown as-is, so the caller classifies the real cause rather than a wrapper.
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateOrAdopt.ExecuteAsync<Thing>(
                create: () => throw new HttpRequestException("the actual cause"),
                findExisting: _ => Task.FromResult<Thing?>(null),
                isTransient: Transient,
                delayBeforeRetry: NoDelay));

        Assert.Equal("the actual cause", error.Message);
    }

    [Fact]
    public async Task APermanentFailure_IsNotRetried()
    {
        var creates = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOrAdopt.ExecuteAsync<Thing>(
                create: () => { creates++; throw new InvalidOperationException("403: forbidden"); },
                findExisting: _ => Task.FromResult<Thing?>(null),
                isTransient: Permanent,
                delayBeforeRetry: NoDelay));

        Assert.Equal(1, creates);
    }

    [Fact]
    public async Task ALookupThatTHROWS_DoesNotReplaceTheOriginalFailure()
    {
        // The lookup usually fails for the same reason the create did — the transport is broken. Its
        // exception must not become the reported cause, or the caller classifies a symptom and the
        // user is sent after the wrong thing.
        //
        // The earlier version of this test passed a lookup that RETURNED NULL, which exercises a
        // different path entirely and made it a duplicate of the test above. It asserted a property
        // the code did not have.
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateOrAdopt.ExecuteAsync<Thing>(
                create: () => throw new HttpRequestException("the actual cause"),
                findExisting: _ => throw new InvalidOperationException("the lookup broke too"),
                isTransient: Permanent,
                delayBeforeRetry: NoDelay));

        Assert.Equal("the actual cause", error.Message);
    }

    [Fact]
    public async Task ALookupThatTHROWS_StillAllowsTheRetry()
    {
        // A broken lookup must not turn a retryable failure into a fatal one.
        var creates = 0;

        var result = await CreateOrAdopt.ExecuteAsync(
            create: () =>
            {
                creates++;
                return creates == 1
                    ? throw new HttpRequestException("first attempt lost")
                    : Task.FromResult(new Thing("second-try"));
            },
            findExisting: _ => throw new InvalidOperationException("the lookup broke too"),
            isTransient: Transient,
            delayBeforeRetry: NoDelay);

        Assert.Equal("second-try", result.Id);
    }

    [Fact]
    public async Task TheFailure_IsHandedToTheLookup_SoHopelessCasesCanBeSkipped()
    {
        // A rejected token creates nothing; asking the server costs a request and answers a question
        // nobody had.
        Exception? seen = null;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CreateOrAdopt.ExecuteAsync<Thing>(
                create: () => throw new UnauthorizedAccessException("bad token"),
                findExisting: failure => { seen = failure; return Task.FromResult<Thing?>(null); },
                isTransient: Permanent,
                delayBeforeRetry: NoDelay));

        Assert.IsType<UnauthorizedAccessException>(seen);
    }

    [Fact]
    public async Task AnImpossibleAttemptLimit_IsRejectedRatherThanSilentlyMeaningOne()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CreateOrAdopt.ExecuteAsync<Thing>(
                create: () => Task.FromResult(new Thing("x")),
                findExisting: _ => Task.FromResult<Thing?>(null),
                isTransient: Transient,
                delayBeforeRetry: NoDelay,
                maxAttempts: 0));
    }

    [Fact]
    public async Task ACancelledRun_IsNeverTreatedAsSomethingToReconcile()
    {
        // A cancelled run is not an ambiguous outcome, and must not be delayed by a lookup against a
        // server the caller has stopped caring about.
        var lookups = 0;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateOrAdopt.ExecuteAsync<Thing>(
                create: () => throw new OperationCanceledException(),
                findExisting: _ => { lookups++; return Task.FromResult<Thing?>(null); },
                isTransient: Transient,
                delayBeforeRetry: NoDelay,
                cancellationToken: cancelled.Token));

        Assert.Equal(0, lookups);
    }

    [Fact]
    public async Task AnHttpTimeout_IsReconciled_EvenThoughItLooksLikeCancellation()
    {
        // The trap this test exists for: HttpClient reports its OWN timeout as
        // TaskCanceledException, which derives from OperationCanceledException. Excluding that TYPE
        // would skip reconciliation for a timeout — and a timeout is the likeliest way to create
        // something on the server and never see the reply. Only the token says "the caller asked to
        // stop"; here nobody did.
        var result = await CreateOrAdopt.ExecuteAsync<Thing>(
            create: () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
            findExisting: _ => Task.FromResult<Thing?>(new Thing("created-anyway")),
            isTransient: Transient,
            delayBeforeRetry: NoDelay,
            cancellationToken: CancellationToken.None);

        Assert.Equal("created-anyway", result.Id);
    }

    [Fact]
    public async Task TheAttemptLimit_IsHonoured()
    {
        var creates = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateOrAdopt.ExecuteAsync<Thing>(
                create: () => { creates++; throw new HttpRequestException("nope"); },
                findExisting: _ => Task.FromResult<Thing?>(null),
                isTransient: Transient,
                delayBeforeRetry: NoDelay,
                maxAttempts: 3));

        Assert.Equal(3, creates);
    }

    [Fact]
    public async Task EveryFailedAttempt_ReconcilesBeforeTheNextOne()
    {
        // Not just the first. An object created on attempt two must be adopted on attempt three
        // rather than duplicated.
        var creates = 0;
        var lookups = 0;

        var result = await CreateOrAdopt.ExecuteAsync<Thing>(
            create: () => { creates++; throw new HttpRequestException("lost"); },
            findExisting: _ =>
            {
                lookups++;
                return Task.FromResult<Thing?>(lookups >= 2 ? new Thing("appeared") : null);
            },
            isTransient: Transient,
            delayBeforeRetry: NoDelay,
            maxAttempts: 4);

        Assert.Equal("appeared", result.Id);
        Assert.Equal(2, creates);
    }
}

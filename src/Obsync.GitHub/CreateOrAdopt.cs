namespace Obsync.GitHub;

/// <summary>
/// Performs a create that is safe to attempt more than once, by asking the server what happened
/// instead of assuming.
/// </summary>
/// <remarks>
/// A POST is not idempotent, and a client cannot tell "the request never arrived" from "the request
/// was applied and the response was lost". Retrying on that ambiguity creates duplicates; reporting
/// failure on it loses work that actually landed. Obsync hit the second: a pull request was created
/// on GitHub, the TLS connection dropped before the response came back, the generic retry helper
/// treated the transport error as transient, and the run finished by reporting that the pull request
/// could not be opened — while it sat open on GitHub.
///
/// <para>
/// The resolution is to make the ambiguity answerable. Before retrying, and before giving up, look
/// for the thing that would exist if the request had succeeded. Finding it is proof, and it converts
/// both failure modes into a correct result.
/// </para>
///
/// <para>
/// Deliberately separated from <c>GitHubService</c>, which constructs its own Octokit client and so
/// cannot be exercised without a live API. The policy is the part that has to be right.
/// </para>
/// </remarks>
public static class CreateOrAdopt
{
    /// <summary>
    /// Attempts <paramref name="create"/>, reconciling against <paramref name="findExisting"/> on
    /// every failure.
    /// </summary>
    /// <param name="create">The non-idempotent operation.</param>
    /// <param name="findExisting">
    /// Given the failure, looks for the object <paramref name="create"/> would have made. Receiving
    /// the exception lets a caller skip the lookup for outcomes that are not ambiguous — a rejected
    /// token creates nothing, so asking costs a request and answers a question nobody had.
    /// <para>
    /// It SHOULD return null rather than throw when it cannot tell; if it throws anyway that is
    /// contained here, because an inconclusive lookup must never replace the original failure with
    /// its own, and enforcing that is this method's job rather than every caller's.
    /// </para>
    /// </param>
    /// <param name="isTransient">Whether a failure is worth another attempt at all.</param>
    /// <param name="delayBeforeRetry">Backoff, given the completed attempt number.</param>
    /// <param name="cancellationToken">
    /// Used ONLY to tell a caller-requested cancellation from an HTTP timeout. A signalled token
    /// means stop; an unsignalled one means the operation's outcome is unknown and must be
    /// reconciled, however the failure was reported.
    /// </param>
    /// <param name="maxAttempts">Total attempts, including the first.</param>
    /// <returns>
    /// The created object, or the existing one when the create had already taken effect.
    /// </returns>
    /// <remarks>
    /// <b>Precondition on <paramref name="findExisting"/>:</b> it must look for something unique to
    /// THIS attempt. Adoption is sound only because what it finds could not have been created by
    /// anything else — for pull requests that holds because the head branch is cut fresh per run. A
    /// lookup keyed on something shared would adopt an unrelated object and report it as this
    /// operation's result.
    /// </remarks>
    /// <exception cref="Exception">
    /// The last failure, when nothing exists on the server to adopt. Rethrown as-is so the caller
    /// classifies the real cause rather than a wrapper.
    /// </exception>
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> create,
        Func<Exception, Task<T?>> findExisting,
        Func<Exception, bool> isTransient,
        Func<int, Task> delayBeforeRetry,
        CancellationToken cancellationToken = default,
        int maxAttempts = 2)
        where T : class
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await create().ConfigureAwait(false);
            }
            // Distinguishing a real cancellation from an HTTP timeout matters more than it looks.
            // HttpClient reports its OWN timeout as TaskCanceledException, which derives from
            // OperationCanceledException — so excluding that type wholesale would skip reconciliation
            // for a timeout, and a timeout is the single most likely way to create something and lose
            // the reply. The token, not the exception type, is what says whether the caller asked to
            // stop. This is the same test the rest of GitHubService already uses.
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Reconcile FIRST. Retrying before asking is what turns one lost response into two
                // objects, and giving up before asking is what loses one that already exists.
                if (await ReconcileAsync(findExisting, ex).ConfigureAwait(false) is { } existing)
                {
                    return existing;
                }

                if (attempt >= maxAttempts || !isTransient(ex))
                {
                    throw;
                }

                await delayBeforeRetry(attempt).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs the lookup, treating any failure of its own as "cannot tell".
    /// </summary>
    /// <remarks>
    /// The lookup usually fails for the same reason the create did — the transport is broken — so
    /// letting its exception escape would replace the real cause with a symptom, and the caller
    /// would then classify and report the wrong thing. Cancellation still propagates: that is the
    /// caller asking to stop, not a failed lookup.
    /// </remarks>
    private static async Task<T?> ReconcileAsync<T>(Func<Exception, Task<T?>> findExisting, Exception failure)
        where T : class
    {
        try
        {
            return await findExisting(failure).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}

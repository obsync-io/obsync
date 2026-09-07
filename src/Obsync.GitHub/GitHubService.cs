using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Results;
using Octokit;
using Octokit.Internal;

namespace Obsync.GitHub;

/// <summary>A GitHub repository visible to the authenticated token.</summary>
public sealed record GitHubRepository(string Owner, string Name, string DefaultBranch, bool Private)
{
    public string FullName => $"{Owner}/{Name}";
}

/// <summary>
/// The outcome of checking a token's effective access to a specific repository. Works for both
/// classic and fine-grained PATs (the repository payload carries the token's <c>pull</c>/<c>push</c>
/// permissions), so it verifies the WRITE access whose absence silently breaks pushes.
/// </summary>
public sealed record TokenPermissionReport(
    bool TokenValid, string? Login, bool RepositoryFound, bool CanRead, bool CanWrite, string? Detail);

/// <summary>An opened pull request. <see cref="ReviewerWarning"/> is set when the PR opened but
/// requesting one or more reviewers failed (a non-fatal condition).</summary>
public sealed record PullRequestInfo(int Number, string HtmlUrl, string? ReviewerWarning);

/// <summary>Checks GitHub tokens, reads repository/branch metadata, and opens pull requests via Octokit.</summary>
public interface IGitHubService
{
    /// <summary>
    /// Verifies the token and its effective access to <paramref name="owner"/>/<paramref name="name"/>:
    /// valid, repository reachable, read, and write. A failed <see cref="Result"/> means the check
    /// itself could not run (e.g. GitHub was unreachable); a successful result carries the checklist.
    /// </summary>
    Task<Result<TokenPermissionReport>> CheckRepositoryAccessAsync(
        string token, string owner, string name, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<GitHubRepository>>> GetRepositoriesAsync(string token, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<string>>> GetBranchesAsync(string token, string owner, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the target branch has protection that could reject a push, independently of whether
    /// the token has write permission.
    /// </summary>
    /// <remarks>
    /// <c>repository.permissions.push</c> is the collaborator ROLE, computed before and entirely
    /// independently of ref-update policy. Branch protection, push rulesets, required reviews,
    /// required status checks, required linear history and required signed commits all leave it
    /// <c>true</c> and still reject the push with GH006. The engine already ships a dedicated
    /// explanation for that rejection — this is the check that stops the product diagnosing a
    /// condition it never looked for.
    /// </remarks>
    Task<Result<bool>> IsBranchProtectedAsync(
        string token, string owner, string name, string branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active repository-ruleset rules that apply to <paramref name="branch"/>, by rule type
    /// (e.g. <c>pull_request</c>, <c>required_status_checks</c>). Empty when none apply.
    /// </summary>
    /// <remarks>
    /// Rulesets are GitHub's current branch-policy mechanism and are a different system from classic
    /// branch protection, with a different error code: a ruleset rejection is <c>GH013</c>, classic
    /// protection is <c>GH006</c>. <see cref="IsBranchProtectedAsync"/> reads the branch object's
    /// <c>protected</c> flag, which was built for the classic system and in any case cannot say
    /// WHICH rule applies — so it can neither reliably detect a ruleset nor explain one.
    /// <para>
    /// Unlike the classic protection endpoint (which requires admin and 403s for the ordinary write
    /// token this product is designed around), <c>GET /repos/{owner}/{repo}/rules/branches/{branch}</c>
    /// returns the rules that apply to a branch for any caller that can read the repository.
    /// </para>
    /// </remarks>
    Task<Result<IReadOnlyList<string>>> GetBranchRulesAsync(
        string token, string owner, string name, string branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a pull request from <paramref name="headBranch"/> into <paramref name="baseBranch"/> and,
    /// if <paramref name="reviewers"/> is non-empty, requests them (best-effort). A failed
    /// <see cref="Result"/> means the PR itself could not be opened (e.g. missing PR permission).
    /// </summary>
    Task<Result<PullRequestInfo>> CreatePullRequestAsync(
        string token, string owner, string name, string title, string headBranch, string baseBranch,
        string body, IReadOnlyList<string> reviewers, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGitHubService" />
public sealed class GitHubService : IGitHubService
{
    private static readonly ProductHeaderValue Product = new("Obsync");
    private readonly ILogger<GitHubService> _logger;
    private readonly IProxyProvider _proxy;

    public GitHubService(ILogger<GitHubService> logger, IProxyProvider proxy)
    {
        _logger = logger;
        _proxy = proxy;
    }

    public async Task<Result<TokenPermissionReport>> CheckRepositoryAccessAsync(
        string token, string owner, string name, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await CreateClientAsync(token, cancellationToken).ConfigureAwait(false);

            // Identity first: confirms the token is valid at all before probing repository access.
            var user = await WithRetryAsync(() => client.User.Current(), cancellationToken).ConfigureAwait(false);

            // The repository payload carries this token's effective permissions (pull/push/admin),
            // which is the reliable way to verify write access for a fine-grained PAT.
            var repo = await WithRetryAsync(() => client.Repository.Get(owner, name), cancellationToken).ConfigureAwait(false);
            var permissions = repo.Permissions;

            return Result.Success(new TokenPermissionReport(
                TokenValid: true,
                Login: user.Login,
                RepositoryFound: true,
                CanRead: permissions?.Pull ?? false,
                CanWrite: permissions?.Push ?? false,
                Detail: null));
        }
        catch (NotFoundException)
        {
            // The token authenticated but cannot see owner/name. GitHub returns 404 rather than 403
            // for a repository a token has no grant on, so "not found" and "not granted" are
            // indistinguishable here — hence naming the causes instead of guessing one. Ordered by
            // how often each is the real answer for an organization-owned repository.
            return Result.Success(new TokenPermissionReport(
                TokenValid: true, Login: null, RepositoryFound: false, CanRead: false, CanWrite: false,
                Detail: $"The token authenticated, but cannot see {owner}/{name}. Usually one of: a fine-grained "
                    + "token still awaiting organization approval (it shows as Pending under Developer settings); "
                    + $"its Resource owner set to your personal account instead of {owner}; a classic token not yet "
                    + "authorized for the organization's SSO; or a typo in the owner or repository name."));
        }
        catch (AuthorizationException)
        {
            return Result.Success(new TokenPermissionReport(
                TokenValid: false, Login: null, RepositoryFound: false, CanRead: false, CanWrite: false,
                Detail: "The token was rejected by GitHub. Check that it is valid and not expired."));
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("GitHub permission check failed: {Message}", ex.Message);
            return Result.Failure<TokenPermissionReport>($"GitHub error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<TokenPermissionReport>($"Could not reach GitHub: {GitHubApiDiagnosis.Explain(ex)}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An HttpClient timeout, not user cancellation (which must propagate).
            return Result.Failure<TokenPermissionReport>("The request to GitHub timed out.");
        }
    }

    public async Task<Result<IReadOnlyList<GitHubRepository>>> GetRepositoriesAsync(
        string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await CreateClientAsync(token, cancellationToken).ConfigureAwait(false);
            var repositories = await WithRetryAsync(
                () => client.Repository.GetAllForCurrent(new RepositoryRequest { Sort = RepositorySort.FullName }),
                cancellationToken).ConfigureAwait(false);

            var mapped = repositories
                .Select(r => new GitHubRepository(r.Owner.Login, r.Name, r.DefaultBranch ?? "main", r.Private))
                .ToList();
            return Result.Success<IReadOnlyList<GitHubRepository>>(mapped);
        }
        catch (ApiException ex)
        {
            return Result.Failure<IReadOnlyList<GitHubRepository>>($"GitHub error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<IReadOnlyList<GitHubRepository>>($"Could not reach GitHub: {GitHubApiDiagnosis.Explain(ex)}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An HttpClient timeout, not user cancellation (which must propagate).
            return Result.Failure<IReadOnlyList<GitHubRepository>>("The request to GitHub timed out.");
        }
    }

    public async Task<Result<bool>> IsBranchProtectedAsync(
        string token, string owner, string name, string branch, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await CreateClientAsync(token, cancellationToken).ConfigureAwait(false);

            // Branch.Get, not the protection endpoint: `Protected` comes back for any collaborator,
            // whereas GET /branches/{b}/protection needs ADMIN on the repository and 403s for the
            // ordinary write token this product is designed around. Knowing that protection exists
            // is enough to warn; enumerating which rules apply is not worth requiring admin for.
            var result = await WithRetryAsync(
                () => client.Repository.Branch.Get(owner, name, branch), cancellationToken).ConfigureAwait(false);
            return Result.Success(result.Protected);
        }
        catch (NotFoundException)
        {
            // No such branch. Not this check's business — the branch-exists check reports it — and
            // reporting a protection failure here would give the same problem two contradictory rows.
            return Result.Success(false);
        }
        catch (ApiException ex)
        {
            return Result.Failure<bool>($"GitHub error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<bool>($"Could not reach GitHub: {GitHubApiDiagnosis.Explain(ex)}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result.Failure<bool>("The request to GitHub timed out.");
        }
    }

    public async Task<Result<IReadOnlyList<string>>> GetBranchRulesAsync(
        string token, string owner, string name, string branch, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await CreateClientAsync(token, cancellationToken).ConfigureAwait(false);

            // Octokit 14 has no ruleset client at all, so this goes through the raw connection. It
            // still travels the configured proxy and credentials, because the Connection is the one
            // CreateClientAsync built.
            var uri = new Uri(
                $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/rules/branches/{Uri.EscapeDataString(branch)}",
                UriKind.Relative);

            var response = await WithRetryAsync(
                () => client.Connection.Get<IReadOnlyList<BranchRule>>(
                    uri, parameters: null!, accepts: "application/vnd.github+json", cancellationToken),
                cancellationToken).ConfigureAwait(false);

            var rules = (response.Body ?? [])
                .Select(r => r.Type)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Result.Success<IReadOnlyList<string>>(rules);
        }
        catch (NotFoundException)
        {
            // Either no such branch, or an account/plan where the endpoint is unavailable. Not
            // evidence of anything — the caller falls back rather than inventing a warning.
            return Result.Failure<IReadOnlyList<string>>("The branch rules endpoint returned not found.");
        }
        catch (ApiException ex)
        {
            return Result.Failure<IReadOnlyList<string>>($"GitHub error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<IReadOnlyList<string>>($"Could not reach GitHub: {GitHubApiDiagnosis.Explain(ex)}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result.Failure<IReadOnlyList<string>>("The request to GitHub timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The response shape is GitHub's, not ours, and this is the one call in this service
            // deserialized into a hand-written type. A new field or an unexpected body must degrade
            // to "unknown" so the caller falls back to the classic protection check — not escape and
            // turn a preflight row into a raw exception message.
            return Result.Failure<IReadOnlyList<string>>($"Could not read branch rules: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<string>>> GetBranchesAsync(
        string token, string owner, string name, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await CreateClientAsync(token, cancellationToken).ConfigureAwait(false);
            var branches = await WithRetryAsync(() => client.Repository.Branch.GetAll(owner, name), cancellationToken).ConfigureAwait(false);
            return Result.Success<IReadOnlyList<string>>([.. branches.Select(b => b.Name)]);
        }
        catch (ApiException ex)
        {
            return Result.Failure<IReadOnlyList<string>>($"GitHub error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<IReadOnlyList<string>>($"Could not reach GitHub: {GitHubApiDiagnosis.Explain(ex)}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An HttpClient timeout, not user cancellation (which must propagate).
            return Result.Failure<IReadOnlyList<string>>("The request to GitHub timed out.");
        }
    }

    public async Task<Result<PullRequestInfo>> CreatePullRequestAsync(
        string token, string owner, string name, string title, string headBranch, string baseBranch,
        string body, IReadOnlyList<string> reviewers, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await CreateClientAsync(token, cancellationToken).ConfigureAwait(false);
            var pr = await CreateOrAdoptPullRequestAsync(
                client, owner, name, title, headBranch, baseBranch, body, cancellationToken).ConfigureAwait(false);

            string? reviewerWarning = null;
            if (reviewers.Count > 0)
            {
                try
                {
                    // Deliberately NOT through WithRetryAsync: this is a POST, and that helper
                    // treats a transport error as transient — the same combination that made a
                    // created pull request look like a failed one. Requesting reviewers is already
                    // best-effort (the catch below downgrades any failure to a warning on an
                    // already-open PR), so a single attempt is the honest thing to do rather than
                    // re-POSTing on an outcome we cannot observe.
                    await client.PullRequest.ReviewRequest
                        .Create(owner, name, pr.Number, new PullRequestReviewRequest(reviewers, []))
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ApiException or HttpRequestException
                    || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    // The PR is already open; a bad/unknown reviewer or a network blip must not fail it.
                    reviewerWarning = $"The pull request opened, but requesting reviewers failed: {ex.Message}";
                    _logger.LogWarning("Requesting reviewers for PR #{Number} failed: {Message}", pr.Number, ex.Message);
                }
            }

            return Result.Success(new PullRequestInfo(pr.Number, pr.HtmlUrl, reviewerWarning));
        }
        catch (AuthorizationException)
        {
            return Result.Failure<PullRequestInfo>("The token was rejected by GitHub. Check that it is valid and not expired.");
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("Opening the pull request failed: {Message}", GitHubApiDiagnosis.Describe(ex));
            return Result.Failure<PullRequestInfo>(ExplainPullRequestFailure(ex));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Opening the pull request failed: {Message}", GitHubApiDiagnosis.Describe(ex));

            // Reaching here means the create failed AND the reconciliation lookup could not confirm
            // one way or the other — most likely because the same transport problem broke it too. The
            // pull request may nonetheless exist: the request can have been applied with its response
            // lost. Saying so is the difference between the user checking and the user opening a
            // duplicate by hand.
            return Result.Failure<PullRequestInfo>(
                $"Could not reach GitHub: {GitHubApiDiagnosis.Explain(ex)} "
                + "Check the repository before retrying — if the request reached GitHub, the pull request "
                + "may have been created even though the reply never arrived.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An HttpClient timeout, not user cancellation (which must propagate).
            return Result.Failure<PullRequestInfo>(
                "The request to GitHub timed out. Check the repository before retrying — the pull request "
                + "may have been created even though the reply never arrived.");
        }
    }

    /// <summary>
    /// Opens the pull request, reconciling with the server before giving up or retrying.
    /// </summary>
    /// <remarks>
    /// Creating a pull request is a POST, and POSTs are not idempotent. This used to go through the
    /// generic retry helper, which treats <see cref="HttpRequestException"/> as transient — so a
    /// request that REACHED GitHub and created the pull request, but whose response was lost to a
    /// dropped TLS connection, was retried and ultimately reported as a failure. The pull request
    /// existed; the product said it did not.
    ///
    /// <para>
    /// That is not merely a wrong message. The engine only marks the run's objects as delivered when
    /// this returns success, so a false failure leaves the state un-advanced and the NEXT run cuts a
    /// fresh head branch and opens a SECOND pull request for the same content — once per run, until
    /// somebody notices.
    /// </para>
    ///
    /// <para>
    /// "No response" and "no effect" are different things and cannot be told apart from the client
    /// side, so the server is asked instead: on any failure, look for an open pull request for this
    /// head and base. Finding one is proof the work landed, whatever the transport did afterwards.
    /// That also covers GitHub's 422 for a duplicate, which is the same situation seen from the
    /// other side.
    /// </para>
    /// </remarks>
    private Task<PullRequest> CreateOrAdoptPullRequestAsync(
        GitHubClient client, string owner, string name, string title, string headBranch, string baseBranch,
        string body, CancellationToken cancellationToken) =>
        CreateOrAdopt.ExecuteAsync(
            create: () => client.PullRequest.Create(
                owner, name, new NewPullRequest(ClampTitle(title), headBranch, baseBranch) { Body = body }),
            findExisting: async failure =>
            {
                // Only an ambiguous outcome is worth a lookup. A rejected token, a missing repository
                // or an exhausted rate limit created nothing, so asking spends a request — against an
                // already-exhausted quota, in the rate-limit case — and logs a warning that reads as a
                // second, unrelated problem.
                if (!CouldHaveTakenEffect(failure))
                {
                    return null;
                }

                var existing = await FindOpenPullRequestAsync(
                    client, owner, name, headBranch, baseBranch, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    _logger.LogInformation(
                        "The pull request for {Head} already exists as #{Number} — adopting it rather than "
                        + "creating a second one.", headBranch, existing.Number);
                }

                return existing;
            },
            isTransient: IsRetryableCreateFailure,
            delayBeforeRetry: attempt => Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken),
            cancellationToken: cancellationToken);

    /// <summary>
    /// The open pull request for this head and base, or null — including when the lookup itself
    /// fails, which must never turn into a second failure on top of the first.
    /// </summary>
    private async Task<PullRequest?> FindOpenPullRequestAsync(
        GitHubClient client, string owner, string name, string headBranch, string baseBranch,
        CancellationToken cancellationToken)
    {
        try
        {
            // GitHub qualifies the head with the owner of the branch, which for this product is
            // always the repository owner: Obsync pushes its head branch to the repository itself,
            // never to a fork.
            var request = new PullRequestRequest
            {
                State = ItemStateFilter.Open,
                Head = $"{owner}:{headBranch}",
                Base = baseBranch,
            };

            // Safe to retry: these are GETs.
            var filtered = await WithRetryAsync(
                () => client.PullRequest.GetAllForRepository(owner, name, request),
                cancellationToken).ConfigureAwait(false);

            if (Match(filtered) is { } found)
            {
                return found;
            }

            // The head filter is documented only as "user:ref-name", and this product's head branches
            // contain slashes (obsync/<job>/<timestamp>) while the owner is whatever the user typed,
            // in whatever case. If either makes the server-side filter miss, the whole reconciliation
            // silently does nothing — which is the one failure mode that would leave this fix looking
            // like it works. So an empty filtered result is not taken as proof: re-ask without the
            // head filter and match on the ref locally, where the semantics are ours.
            var byBase = await WithRetryAsync(
                () => client.PullRequest.GetAllForRepository(
                    owner, name, new PullRequestRequest { State = ItemStateFilter.Open, Base = baseBranch }),
                cancellationToken).ConfigureAwait(false);

            return Match(byBase);

            PullRequest? Match(IReadOnlyList<PullRequest> candidates) =>
                candidates.FirstOrDefault(p =>
                    string.Equals(p.Head?.Ref, headBranch, StringComparison.Ordinal));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The same token test as CreateOrAdopt, for the same reason: HttpClient reports its own
            // timeout as TaskCanceledException. Testing the TYPE here let a timed-out lookup escape
            // and replace the create's real failure with "the request to GitHub timed out".
            _logger.LogWarning(
                "Could not check whether the pull request for {Head} already exists: {Message}",
                headBranch, GitHubApiDiagnosis.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// Whether a failed create might nonetheless have been applied on the server.
    /// </summary>
    /// <remarks>
    /// The distinction is between "GitHub answered, and the answer was no" and "we never learned what
    /// happened". Only the second is worth reconciling. A 422 IS worth it: the usual reason a create
    /// is refused as a duplicate is that an earlier attempt succeeded.
    /// </remarks>
    internal static bool CouldHaveTakenEffect(Exception failure) => failure switch
    {
        AuthorizationException => false,
        RateLimitExceededException => false,
        NotFoundException => false,
        ApiException api => (int)api.StatusCode is not (401 or 403 or 404),
        _ => true,
    };

    /// <summary>
    /// Whether a failed create is worth another attempt, once reconciliation has found nothing.
    /// </summary>
    /// <remarks>
    /// Wider than <see cref="IsTransient"/> by one case: an HttpClient timeout arrives as
    /// <see cref="TaskCanceledException"/>, which IsTransient does not cover — and must not, because
    /// it also guards a retry helper that has no cancellation token, where retrying a cancelled call
    /// would be wrong. By the time this is consulted, CreateOrAdopt has already established that the
    /// caller did not ask to stop, so a cancellation-shaped exception here is a timeout.
    /// </remarks>
    internal static bool IsRetryableCreateFailure(Exception failure) =>
        IsTransient(failure) || failure is TaskCanceledException or TimeoutException;

    /// <summary>Turns a PR-create API failure into a short, actionable message.</summary>
    private static string ExplainPullRequestFailure(ApiException ex) => (int)ex.StatusCode switch
    {
        403 => "GitHub denied opening the pull request — the token needs Pull requests: write permission on this repository.",
        422 => $"GitHub could not open the pull request: {ex.Message}", // e.g. a PR already exists, or no diff between base and head
        _ => $"GitHub error opening the pull request: {ex.Message}",
    };

    // Builds a client whose HTTP handler routes through the configured proxy (if any). Resolved per
    // call because the proxy config lives in the DB/Credential Manager and can change at runtime.
    private async Task<GitHubClient> CreateClientAsync(string token, CancellationToken cancellationToken)
    {
        var resolution = await _proxy.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var adapter = new HttpClientAdapter(() => new HttpClientHandler
        {
            Proxy = resolution?.WebProxy,
            UseProxy = resolution is not null,
        });
        // Trim mirrors BuildAuthorizationHeader: tokens are often pasted with a trailing
        // newline/space, which GitHub rejects as an invalid credential.
        return new GitHubClient(new Connection(Product, adapter)) { Credentials = new Credentials(token.Trim()) };
    }

    /// <summary>
    /// Runs an Octokit call with a short, growing backoff on transient failures (server 5xx,
    /// secondary rate limits, transport blips). Permanent failures — bad credentials, 4xx, the
    /// primary rate limit — are not retried and surface to the caller's catch immediately.
    /// </summary>
    /// <remarks>
    /// ONLY for idempotent calls. It treats <see cref="HttpRequestException"/> as transient, and a
    /// transport error cannot distinguish "the request never arrived" from "the request was applied
    /// and the response was lost" — so retrying a POST here duplicates whatever it creates. Pull
    /// request creation was wrapped in this and produced exactly that class of bug; it now goes
    /// through <see cref="CreateOrAdopt"/>, which reconciles with the server instead of guessing.
    /// Every remaining caller is a GET — asserted by <c>WithRetryAsyncTests</c>, because this remark
    /// is the only thing standing between a future POST and a repeat of that bug, and a remark that
    /// is merely believed protects nothing.
    /// </remarks>
    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken, int maxAttempts = 3)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                // The secondary rate limit says exactly how long to back off; retrying sooner just
                // trips it again. Capped at 120s because Retry-After is server-supplied input and
                // must not be able to stall a run indefinitely.
                var delay = ex is AbuseException { RetryAfterSeconds: { } retryAfterSeconds }
                    ? TimeSpan.FromSeconds(Math.Min(retryAfterSeconds, 120))
                    : TimeSpan.FromSeconds(attempt);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        // Primary rate limit resets minutes away — retrying soon cannot help, so surface it.
        RateLimitExceededException => false,
        // Secondary ("abuse") rate limit clears quickly; a short backoff is worthwhile.
        AbuseException => true,
        ApiException api => (int)api.StatusCode >= 500,
        HttpRequestException => true,
        TimeoutException => true,
        _ => false,
    };

    /// <summary>
    /// Longest pull request title sent to GitHub. GitHub documents no limit, but it rejects an
    /// over-long title with a 422 rather than truncating it, so the request must be sized here.
    /// The reported ceiling is 256; this keeps headroom under it.
    /// </summary>
    public const int MaxPullRequestTitleLength = 250;

    /// <summary>
    /// Last line of defence on the title length. Callers are expected to compose a title that
    /// already fits (<c>CommitMessageBuilder</c> summarizes the database list for exactly this
    /// reason); this guarantees the invariant at the API boundary for every caller, present and
    /// future. Never splits a surrogate pair — a title can carry non-BMP SQL identifiers.
    /// </summary>
    public static string ClampTitle(string title)
    {
        if (title.Length <= MaxPullRequestTitleLength)
        {
            return title;
        }

        var cut = MaxPullRequestTitleLength - 1;
        if (char.IsHighSurrogate(title[cut - 1]))
        {
            cut--;
        }

        return string.Concat(title.AsSpan(0, cut), "…");
    }

    /// <summary>The web URL for a commit, used for the "Open in GitHub" links.</summary>
    public static string BuildCommitUrl(string owner, string name, string sha) =>
        $"https://github.com/{owner}/{name}/commit/{sha}";

    /// <summary>The web URL for a file on a branch, used for the per-change "Open in GitHub" links.</summary>
    public static string BuildBlobUrl(string owner, string name, string branch, string relativePath) =>
        $"https://github.com/{owner}/{name}/blob/{branch}/{relativePath.TrimStart('/')}";

    /// <summary>
    /// Builds the HTTP Authorization header value used to authenticate git over HTTPS with a PAT,
    /// without persisting the token in the repository config.
    /// </summary>
    public static string BuildAuthorizationHeader(string token)
    {
        // Trim: tokens are often pasted with a trailing newline/space, which would corrupt the
        // base64 and cause GitHub to reject every push with an authentication error.
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token.Trim()}"));
        return $"AUTHORIZATION: basic {basic}";
    }
}

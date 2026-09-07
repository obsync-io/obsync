using System.Text.RegularExpressions;
using Obsync.Git;

namespace Obsync.Engine;

/// <summary>
/// Turns raw <c>git push</c> stderr into a short, actionable reason for the user.
/// </summary>
/// <remarks>
/// Lifted out of <c>SyncEngine</c> so it can be tested against real GitHub output, the same way
/// <see cref="GitTransportDiagnosis"/> already is. The strings it returns are what a user sees on
/// the dashboard, in an alert email and in an exported report — the raw stderr stays behind them as
/// the log entry's technical details — so getting one wrong sends somebody to fix the wrong thing.
///
/// <para>
/// <b>Order is the design.</b> GitHub prints a specific <c>GH0nn</c> code for each server-side
/// policy rejection and then, underneath, git's own generic <c>! [remote rejected]</c> line. Every
/// rejection therefore contains the word "rejected", so any test for it has to run LAST or it
/// swallows the specific causes above it.
/// </para>
/// </remarks>
public static partial class PushFailureDiagnosis
{
    /// <summary>A GitHub push-policy error code, e.g. <c>GH013</c>.</summary>
    [GeneratedRegex(@"GH0\d\d", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubErrorCode();

    /// <summary>
    /// The bullet lines GitHub prints under a rule violation, e.g.
    /// <c>remote: - Changes must be made through a pull request.</c>
    /// </summary>
    [GeneratedRegex(@"^\s*remote:\s*-\s*(?<rule>.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex ViolationBullet();

    /// <summary>Explains a failed push, or falls back to its first line.</summary>
    public static string Explain(string? error)
    {
        var raw = error ?? string.Empty;
        var text = raw.ToLowerInvariant();

        // --- Specific server-side policy rejections, most specific first --------------------------

        if (text.Contains("gh006") || text.Contains("protected branch"))
        {
            return "The branch is protected — GitHub blocks direct pushes to it. Switch the job to " +
                   "Pull request mode, or allow this token's account to push to the branch.";
        }

        if (text.Contains("gh013") || text.Contains("repository rule violations"))
        {
            // Rulesets are GitHub's current mechanism; classic branch protection (GH006) is the
            // legacy one. They are separate systems with separate error codes, and only the legacy
            // one was handled — so the modern, more common case fell through to a generic message
            // and told the user nothing they could act on.
            var violations = DescribeViolations(raw);
            return "A repository ruleset on this branch blocked the push"
                   + (violations is null ? ". " : $": {violations} ")
                   + "This is a repository policy, not a permissions problem. Switch the job to "
                   + "Pull request mode, or ask a repository administrator to grant this account a "
                   + "bypass for the ruleset.";
        }

        if (text.Contains("gh001") || text.Contains("exceeds github's file size limit"))
        {
            return "GitHub rejected a file over its 100 MB limit. Remove the oversized file from the " +
                   "workspace branch (or reduce the job's reference-data scope), then re-run.";
        }

        // --- Credentials and permissions ----------------------------------------------------------

        if (text.Contains("permission") || text.Contains("403") || text.Contains("forbidden"))
        {
            return "GitHub denied the push — the access token needs write (Contents) permission on this repository.";
        }

        if (text.Contains("authentication failed") || text.Contains("could not read username")
            || text.Contains("401") || text.Contains("invalid username or password"))
        {
            return "GitHub rejected the credentials — check the repository's access token is valid and not expired.";
        }

        // --- The branch genuinely moved on --------------------------------------------------------

        // Deliberately NOT triggered by the bare word "rejected". git prints
        // "! [remote rejected]" for every server-side refusal, including all three GH codes above,
        // so matching it here diagnosed a repository POLICY as a stale local branch and sent the
        // user off to pull and merge a branch that was perfectly up to date. Only phrases that
        // actually mean "your branch is behind" belong here.
        if (text.Contains("non-fast-forward") || text.Contains("fetch first")
            || text.Contains("behind its remote counterpart"))
        {
            return "The remote branch has commits Obsync does not have — pull/merge the branch, then re-run.";
        }

        // --- Transport ----------------------------------------------------------------------------

        // Shared with the preflight reachability probe so both surfaces name the same cause the same
        // way, and consulted BEFORE any catch-all because git reports TLS trust, blocked revocation,
        // proxy 407, DNS and plain connectivity behind one "fatal: unable to access …" line.
        if (GitTransportDiagnosis.TryExplain(error) is { } transport)
        {
            return transport;
        }

        // --- A rejection we do not have a specific explanation for --------------------------------

        // Reached by a GH code newer than this build, or by an organisation policy that prints
        // something we have never seen. Say what is definitely true and point at the details, rather
        // than guessing — a wrong diagnosis costs more than an honest "look here".
        if (text.Contains("[remote rejected]") || text.Contains("push declined")
            || GitHubErrorCode().IsMatch(raw))
        {
            var violations = DescribeViolations(raw);
            return "GitHub's server refused the push"
                   + (violations is null ? "" : $": {violations}")
                   + ". This is a repository policy rather than a credential problem — see the "
                   + "technical details for the exact rule, and consider Pull request mode.";
        }

        var firstLine = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? "See technical details for the git error." : firstLine.Trim();
    }

    /// <summary>
    /// The rules GitHub said were violated, joined into one sentence, or null when it named none.
    /// </summary>
    /// <remarks>
    /// These bullets are the only part of a rule rejection that says what to actually DO — "Changes
    /// must be made through a pull request", "Required status check … is expected". Reporting the
    /// code without them tells the user a push failed and nothing more.
    /// </remarks>
    private static string? DescribeViolations(string error)
    {
        var rules = ViolationBullet().Matches(error)
            .Select(m => m.Groups["rule"].Value.Trim())
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (rules.Count == 0)
        {
            return null;
        }

        var joined = string.Join(" ", rules.Select(r => r.EndsWith('.') ? r : r + "."));
        return joined;
    }
}

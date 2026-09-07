using System.Text.RegularExpressions;

namespace Obsync.Engine.Tests;

/// <summary>
/// Only idempotent calls may be retried.
/// </summary>
/// <remarks>
/// A production incident came from breaking this: <c>client.PullRequest.Create</c> — a POST — was
/// wrapped in a helper that treats a transport error as transient. The request reached GitHub and
/// created the pull request, the connection dropped before the response, the helper retried, and the
/// run reported that the pull request could not be opened while it sat open on GitHub.
///
/// <para>
/// The rule cannot be expressed in the type system, and the remark on the helper asserting it was
/// already false when written — a second POST was still flowing through it. So it is asserted here
/// instead, against the source, because an invariant nothing checks is an invariant that drifts.
/// </para>
/// </remarks>
public sealed class RetryHelperInvariantTests
{
    private static readonly string Source =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GitHubService.cs"));

    /// <summary>Octokit method names that change server state.</summary>
    private static readonly string[] Mutating =
        [".Create(", ".Update(", ".Delete(", ".Merge(", ".Add(", ".Remove(", ".Post(", ".Put(", ".Patch("];

    /// <summary>
    /// Each <c>WithRetryAsync(...)</c> call with the argument text that follows it, up to the closing
    /// of that call. Deliberately crude: it only has to be good enough to see the delegate.
    /// </summary>
    private static IEnumerable<string> RetriedCallSites()
    {
        foreach (Match match in Regex.Matches(Source, @"WithRetryAsync\s*(<[^>]*>)?\s*\("))
        {
            var start = match.Index + match.Length;
            var depth = 1;
            var end = start;
            while (end < Source.Length && depth > 0)
            {
                if (Source[end] == '(') { depth++; }
                else if (Source[end] == ')') { depth--; }
                end++;
            }

            yield return Source[start..Math.Min(end, Source.Length)];
        }
    }

    [Fact]
    public void NoMutatingCall_IsWrappedInTheRetryHelper()
    {
        var offenders = new List<string>();

        foreach (var site in RetriedCallSites())
        {
            foreach (var mutator in Mutating.Where(site.Contains))
            {
                offenders.Add($"{mutator.Trim('(', '.')} in: {Collapse(site)}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "WithRetryAsync retries on transport errors, which cannot distinguish 'never arrived' from "
            + "'applied, reply lost'. Retrying a state-changing call there duplicates what it creates. "
            + "Use CreateOrAdopt, which reconciles with the server, or call it once.\n"
            + string.Join("\n", offenders));

        static string Collapse(string text) =>
            Regex.Replace(text, @"\s+", " ").Trim() is { Length: > 140 } long_ ? long_[..140] + "…" : Regex.Replace(text, @"\s+", " ").Trim();
    }

    [Fact]
    public void TheTestActuallyFindsTheCallSites()
    {
        // Guards the crude parser: if the regex stopped matching, every assertion above would pass
        // vacuously and the invariant would be unprotected.
        var sites = RetriedCallSites().ToList();

        Assert.True(sites.Count >= 5, $"Only found {sites.Count} retried call sites — the parser has drifted.");
        Assert.Contains(sites, s => s.Contains("User.Current"));
    }
}

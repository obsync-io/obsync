namespace Obsync.GitHub;

/// <summary>
/// One active repository-ruleset rule as returned by
/// <c>GET /repos/{owner}/{repo}/rules/branches/{branch}</c>.
/// </summary>
/// <remarks>
/// Only <see cref="Type"/> is modelled. The endpoint also returns the ruleset id, its source and a
/// <c>parameters</c> object whose shape differs per rule type — none of which this product needs:
/// the question being asked is "will a direct push to this branch be refused, and by what", and the
/// rule type answers it. Deserialized by Octokit's serializer, which maps the JSON <c>type</c>
/// field onto this property.
/// </remarks>
public sealed class BranchRule
{
    /// <summary>
    /// The rule type, e.g. <c>pull_request</c>, <c>required_status_checks</c>,
    /// <c>required_signatures</c>, <c>non_fast_forward</c>, <c>creation</c>, <c>update</c>,
    /// <c>deletion</c>.
    /// </summary>
    public string Type { get; set; } = string.Empty;
}

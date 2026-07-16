namespace Obsync.Shared;

/// <summary>
/// Validates git branch names against the <c>git check-ref-format</c> rules, so the wizard rejects
/// a branch that would only fail at run time inside the git CLI.
/// </summary>
public static class GitRefName
{
    // Characters git forbids anywhere in a ref component (plus control chars, checked separately).
    private static readonly char[] ForbiddenChars = [' ', '~', '^', ':', '?', '*', '[', '\\'];

    /// <summary>
    /// True when <paramref name="name"/> is a valid git branch name: not empty, not <c>@</c>, no
    /// leading/trailing/double <c>/</c>, no <c>..</c>, no <c>@{</c>, no control or forbidden
    /// characters, no trailing <c>.</c>, and no path component that starts with <c>.</c> or ends
    /// with <c>.lock</c>.
    /// </summary>
    public static bool IsValidBranchName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name == "@")
        {
            return false;
        }

        if (name.StartsWith('/') || name.EndsWith('/') || name.EndsWith('.'))
        {
            return false;
        }

        if (name.Contains("..", StringComparison.Ordinal)
            || name.Contains("//", StringComparison.Ordinal)
            || name.Contains("@{", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in name)
        {
            if (char.IsControl(ch) || ForbiddenChars.Contains(ch))
            {
                return false;
            }
        }

        return name.Split('/').All(component =>
            !component.StartsWith('.') && !component.EndsWith(".lock", StringComparison.Ordinal));
    }
}

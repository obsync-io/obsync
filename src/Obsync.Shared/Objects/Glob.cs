using System.Text.RegularExpressions;

namespace Obsync.Shared.Objects;

/// <summary>Case-insensitive glob matching: <c>*</c> = any run of characters, <c>?</c> = a single character.</summary>
public static class Glob
{
    public static bool IsMatch(string text, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

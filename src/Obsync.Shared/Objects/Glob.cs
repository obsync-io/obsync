using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Obsync.Shared.Objects;

/// <summary>Case-insensitive glob matching: <c>*</c> = any run of characters, <c>?</c> = a single character.</summary>
/// <remarks>
/// Each distinct pattern is translated and compiled once, then reused.
/// <para>
/// This used to rebuild the regex SOURCE STRING on every call — four string allocations per call —
/// and then hand it to the static <see cref="Regex.IsMatch(string, string, RegexOptions)"/> overload,
/// whose cache holds only <see cref="Regex.CacheSize"/> (15) entries. A job with more than fifteen
/// distinct ignore patterns therefore thrashed that cache and constructed a fresh <see cref="Regex"/>
/// on essentially every call — and the engine calls this per object, per pattern, twice per run
/// (once while planning, once while applying). Measured on a 5,000-object job: 62 ms at fifteen
/// patterns, 579 ms at sixteen. The cliff is exactly where the shared cache runs out.
/// </para>
/// <para>
/// A job's patterns are few and long-lived, so an unbounded dictionary is the right shape here —
/// it is keyed by pattern, not by the text being tested.
/// </para>
/// </remarks>
public static class Glob
{
    private static readonly ConcurrentDictionary<string, Regex> Compiled = new(StringComparer.Ordinal);

    public static bool IsMatch(string text, string pattern) => Translate(pattern).IsMatch(text);

    private static Regex Translate(string pattern) => Compiled.GetOrAdd(
        pattern,
        static p => new Regex(
            "^" + Regex.Escape(p)
                .Replace("\\*", ".*", StringComparison.Ordinal)
                .Replace("\\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
}

using System.Text;
using System.Text.RegularExpressions;

namespace Obsync.Shared.Scripting;

/// <summary>Controls how raw scripted DDL is normalized before hashing and writing.</summary>
public sealed class NormalizationOptions
{
    /// <summary>Convert all line endings to LF. Keeps diffs stable across platforms and tools.</summary>
    public bool NormalizeLineEndings { get; init; } = true;

    /// <summary>Remove trailing whitespace from every line.</summary>
    public bool TrimTrailingWhitespace { get; init; } = true;

    /// <summary>
    /// Strip the SSMS-style object header comment that embeds a "Script Date" timestamp, which
    /// would otherwise change the hash on every run.
    /// </summary>
    public bool StripObjectHeaderComment { get; init; } = true;

    /// <summary>Collapse runs of blank lines at the end and guarantee a single trailing newline.</summary>
    public bool EnsureSingleTrailingNewline { get; init; } = true;

    public static NormalizationOptions Default { get; } = new();
}

/// <summary>Produces canonical, deterministic script text for change detection.</summary>
public interface IScriptNormalizer
{
    string Normalize(string script, NormalizationOptions? options = null);
}

/// <inheritdoc cref="IScriptNormalizer" />
public sealed partial class ScriptNormalizer : IScriptNormalizer
{
    // This runs once per scripted object (up to ~1M per run) and its output feeds the SHA-256
    // content hashes stored in the state database, so it must stay byte-identical to the
    // historical Replace/Split/Join/TrimEnd pipeline. The single-pass form below exists purely
    // to cut allocations (~6 full string copies per call before) — never to change semantics.
    public string Normalize(string script, NormalizationOptions? options = null)
    {
        if (string.IsNullOrEmpty(script))
        {
            return string.Empty;
        }

        options ??= NormalizationOptions.Default;
        var text = script;

        // Every possible match of ObjectHeaderRegex starts with the literal "/*", so the regex
        // scan (the expensive part) can be skipped whenever that substring is absent. When it is
        // present the regex runs exactly as before, keeping behavior identical.
        if (options.StripObjectHeaderComment && text.Contains("/*", StringComparison.Ordinal))
        {
            text = ObjectHeaderRegex().Replace(text, string.Empty);
        }

        // Steady-state fast path: input that the remaining steps would leave unchanged is
        // returned as-is, allocation-free. Only valid when LF output is requested.
        if (options.NormalizeLineEndings && IsAlreadyNormalized(text, options))
        {
            return text;
        }

        return NormalizeCore(text, options);
    }

    /// <summary>
    /// Determines in one scan whether the line-ending conversion, per-line trailing-whitespace
    /// trim, and single-trailing-newline steps would all be no-ops for <paramref name="text"/>.
    /// Callers must only trust the result when <see cref="NormalizationOptions.NormalizeLineEndings"/>
    /// is enabled (otherwise the final LF-to-CRLF re-emit would still rewrite the text).
    /// </summary>
    private static bool IsAlreadyNormalized(string text, NormalizationOptions options)
    {
        if (text.Length == 0)
        {
            // The historical pipeline turns an empty remainder into "\n" when a trailing
            // newline is guaranteed; otherwise it stays empty.
            return !options.EnsureSingleTrailingNewline;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                return false;
            }

            // TrimEnd() removes every char.IsWhiteSpace character, so any such character at a
            // line end (immediately before '\n' or at end of input) means work is needed.
            // '\n' itself is whitespace but is the line delimiter, not trimmable line content.
            if (options.TrimTrailingWhitespace && c != '\n' && char.IsWhiteSpace(c)
                && (i + 1 == text.Length || text[i + 1] == '\n'))
            {
                return false;
            }
        }

        if (options.EnsureSingleTrailingNewline)
        {
            if (text[^1] != '\n')
            {
                return false;
            }

            if (text.Length > 1 && text[^2] == '\n')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Single-pass, single-buffer equivalent of the historical pipeline:
    /// CRLF/CR-to-LF conversion, optional per-line <c>TrimEnd()</c>, optional collapse of
    /// trailing newlines to exactly one, optional LF-to-CRLF re-emit.
    /// </summary>
    private static string NormalizeCore(string text, NormalizationOptions options)
    {
        var trim = options.TrimTrailingWhitespace;
        var builder = new StringBuilder(text.Length + 1);

        // Builder length up to (and including) the last non-whitespace char of the current line;
        // truncating to it at a line boundary reproduces string.TrimEnd() exactly.
        var lineContentEnd = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                // "\r\n" and a lone '\r' both become '\n', matching
                // Replace("\r\n", "\n").Replace('\r', '\n').
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                c = '\n';
            }

            if (c == '\n')
            {
                if (trim)
                {
                    builder.Length = lineContentEnd;
                }

                builder.Append('\n');
                lineContentEnd = builder.Length;
                continue;
            }

            builder.Append(c);
            if (!char.IsWhiteSpace(c))
            {
                lineContentEnd = builder.Length;
            }
        }

        if (trim)
        {
            builder.Length = lineContentEnd;
        }

        if (options.EnsureSingleTrailingNewline)
        {
            while (builder.Length > 0 && builder[^1] == '\n')
            {
                builder.Length--;
            }

            builder.Append('\n');
        }

        var result = builder.ToString();
        return options.NormalizeLineEndings ? result : result.Replace("\n", "\r\n");
    }

    // Matches the single-line SSMS header, e.g.
    //   /****** Object:  StoredProcedure [dbo].[x]    Script Date: 6/28/2026 11:00:00 PM ******/
    [GeneratedRegex(@"/\*+\s*Object:.*?Script Date:.*?\*+/[ \t]*\r?\n?", RegexOptions.IgnoreCase)]
    private static partial Regex ObjectHeaderRegex();
}

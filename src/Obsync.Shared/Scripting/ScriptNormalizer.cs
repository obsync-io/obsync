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
    public string Normalize(string script, NormalizationOptions? options = null)
    {
        if (string.IsNullOrEmpty(script))
        {
            return string.Empty;
        }

        options ??= NormalizationOptions.Default;
        var text = script;

        if (options.StripObjectHeaderComment)
        {
            text = ObjectHeaderRegex().Replace(text, string.Empty);
        }

        // Always work in LF internally; re-emit per the option at the end.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        if (options.TrimTrailingWhitespace)
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i] = lines[i].TrimEnd();
            }

            text = string.Join('\n', lines);
        }

        if (options.EnsureSingleTrailingNewline)
        {
            text = text.TrimEnd('\n') + "\n";
        }

        if (!options.NormalizeLineEndings)
        {
            text = text.Replace("\n", "\r\n");
        }

        return text;
    }

    // Matches the single-line SSMS header, e.g.
    //   /****** Object:  StoredProcedure [dbo].[x]    Script Date: 6/28/2026 11:00:00 PM ******/
    [GeneratedRegex(@"/\*+\s*Object:.*?Script Date:.*?\*+/[ \t]*\r?\n?", RegexOptions.IgnoreCase)]
    private static partial Regex ObjectHeaderRegex();
}

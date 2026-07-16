using System.Text;
using System.Text.RegularExpressions;
using Obsync.Shared.Scripting;

namespace Obsync.Shared.Tests;

/// <summary>
/// Verbatim copy of the ScriptNormalizer implementation that shipped before the single-pass
/// allocation rewrite. It is the behavioral oracle: the rewritten normalizer must produce
/// byte-identical output for every input, because normalized text feeds the SHA-256 content
/// hashes stored in the state database — a single differing byte would cause every deployed
/// installation to re-detect and re-commit every object on its next run.
/// Do not modernize or "fix" this class; it must stay frozen.
/// </summary>
internal sealed partial class ReferenceNormalizer
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

public sealed class ScriptNormalizerEquivalenceTests
{
    private readonly ScriptNormalizer _normalizer = new();
    private readonly ReferenceNormalizer _reference = new();

    /// <summary>
    /// Crafted corpus covering every edge the normalizer has observable behavior for:
    /// line-ending variants, trailing-whitespace variants (all Unicode whitespace classes that
    /// string.TrimEnd() trims), header-comment matches and near-misses, mixed endings,
    /// unicode content, and whitespace-only inputs.
    /// </summary>
    private static readonly string[] Corpus =
    [
        // Empty / bare line endings
        string.Empty,
        "\n",
        "\r\n",
        "\r",
        "\n\n\n",
        "\r\n\r\n",
        "\r\r",
        "\n\r", // LF then CR: not a CRLF pair, becomes two LFs

        // Trailing-newline variants
        "a",
        "a\n",
        "a\n\n\n\n",
        "a\r\nb\rc",
        "a\r\rb",
        "SELECT 1;", // no trailing newline

        // Trailing whitespace per line: every char class TrimEnd() removes
        "a \n",
        "a\t\n",
        "a \t \n",
        "a\u00A0\n", // NO-BREAK SPACE
        "a\u3000\n", // IDEOGRAPHIC SPACE
        "a\v\n", // vertical tab
        "a\f\n", // form feed
        "a\u0085\n", // NEL (whitespace, but not converted as a line ending)
        "a\u2028\n", // LINE SEPARATOR (whitespace, not a split delimiter)
        "a\u2029\n", // PARAGRAPH SEPARATOR
        "a\u200B\n", // ZERO WIDTH SPACE: NOT whitespace, must NOT be trimmed
        "a\u00A0b\n", // whitespace mid-line only: must be preserved
        "a  ", // trailing spaces, no newline
        "line1  \r\nline2\t\rline3 \n",

        // Whitespace-only lines and inputs
        "  \n\t\n",
        " \u00A0\u3000\v\f",
        "\t",
        " ",
        "\u00A0",
        "a\n   \n\t\nb\n", // whitespace-only lines between content
        "a\n \n\t\n \n", // trailing blank-ish lines with whitespace

        // SSMS header: exact and variant matches
        "/****** Object:  StoredProcedure [dbo].[x]    Script Date: 6/28/2026 11:00:00 PM ******/\r\nCREATE PROCEDURE dbo.x AS SELECT 1;\r\n",
        "/****** Object:  View [dbo].[v]    Script Date: 1/1/2026 1:00:00 AM ******/\nCREATE VIEW dbo.v AS SELECT 1 AS a;\n",
        "/****** Object:  Table [dbo].[t]    Script Date: 2/2/2026 2:00:00 PM ******/", // no trailing newline
        "/****** Object:  Table [dbo].[t]    Script Date: 2/2/2026 ******/ \t \nCREATE TABLE dbo.t (i int);\n", // trailing spaces/tabs after header
        "/* object: t script date: 1/1/2026 */\nbody\n", // lowercase (IgnoreCase)
        "/*Object:Script Date:*/", // minimal, zero-width lazy segments
        "/*\n Object: x Script Date: 1/1/2026 */\nbody\n", // \s* crossing a newline between /* and Object:
        "/* Object: a\rScript Date: b */\nbody\n", // '.' matches \r between Object: and Script Date:
        "/* Object: a */ Script Date: b */\nx\n", // lazy .*? spanning an inner */
        "SELECT 1;\n/****** Object: View [dbo].[v] Script Date: 2/2/2026 ******/\nSELECT 2;\n", // header mid-file
        "/****** Object: A Script Date: 1/1 ******/\nbody1\n/****** Object: B Script Date: 2/2 ******/\nbody2\n", // two headers

        // Header near-misses: regex must NOT fire, text preserved
        "/****** Object:  Table [dbo].[t] ******/\nCREATE TABLE t (i int);\n", // no Script Date
        "/****** Script Date: 1/1/2026 ******/\nSELECT 1;\n", // no Object:
        "-- Script Date: 1/1/2026\nSELECT 1;\n", // Script Date present, no /* (guard soundness)
        "/* just a comment */\nSELECT 1;\n", // /* present, no header
        "/ * Object: x Script Date: y * /\nSELECT 1;\n", // separated slash-star
        "/****** Object: x Script\nDate: y ******/\nSELECT 1;\n", // '.' cannot cross \n between Object: and Script Date:

        // Mixed endings and unicode content
        "a\r\rb\r\nc\n\rd",
        "SELECT N'héllo 漢字';\n",
        "🚀\n", // surrogate pair (rocket) at line end
        "select '🚀'  \r\nGO\r\n",
    ];

    private static readonly NormalizationOptions[] AllOptionCombinations = BuildAllOptionCombinations();

    private static NormalizationOptions[] BuildAllOptionCombinations()
    {
        var combos = new List<NormalizationOptions>();
        foreach (var normalizeLineEndings in new[] { true, false })
        {
            foreach (var trimTrailingWhitespace in new[] { true, false })
            {
                foreach (var stripObjectHeaderComment in new[] { true, false })
                {
                    foreach (var ensureSingleTrailingNewline in new[] { true, false })
                    {
                        combos.Add(new NormalizationOptions
                        {
                            NormalizeLineEndings = normalizeLineEndings,
                            TrimTrailingWhitespace = trimTrailingWhitespace,
                            StripObjectHeaderComment = stripObjectHeaderComment,
                            EnsureSingleTrailingNewline = ensureSingleTrailingNewline,
                        });
                    }
                }
            }
        }

        return [.. combos];
    }

    private static string Describe(string input) =>
        string.Concat(input.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}"));

    [Fact]
    public void Corpus_MatchesReference_ByteForByte_AcrossAllOptionCombinations()
    {
        foreach (var options in AllOptionCombinations)
        {
            foreach (var input in Corpus)
            {
                var expected = _reference.Normalize(input, options);
                var actual = _normalizer.Normalize(input, options);

                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    Assert.Fail(
                        $"Mismatch for input <{Describe(input)}> with options " +
                        $"(LF={options.NormalizeLineEndings}, Trim={options.TrimTrailingWhitespace}, " +
                        $"Strip={options.StripObjectHeaderComment}, Newline={options.EnsureSingleTrailingNewline}): " +
                        $"expected <{Describe(expected)}> but got <{Describe(actual)}>.");
                }
            }
        }
    }

    [Fact]
    public void Corpus_MatchesReference_WithDefaultOptions()
    {
        foreach (var input in Corpus)
        {
            Assert.Equal(_reference.Normalize(input), _normalizer.Normalize(input));
        }
    }

    [Fact]
    public void LargeSyntheticScript_MatchesReference()
    {
        // ~1MB script with a header, mixed endings, trailing whitespace, and blank runs.
        var builder = new StringBuilder(1_100_000);
        builder.Append("/****** Object:  StoredProcedure [dbo].[big]    Script Date: 6/28/2026 11:00:00 PM ******/\r\n");
        while (builder.Length < 1_000_000)
        {
            builder.Append("CREATE PROCEDURE dbo.p AS\r\nSELECT 'payload-é漢';   \r\n\t\r\n\r\nGO \r\n");
        }

        var input = builder.ToString();

        Assert.Equal(_reference.Normalize(input), _normalizer.Normalize(input));
    }

    [Fact]
    public void Fuzz_RandomInputs_MatchReference_Deterministic()
    {
        char[] alphabet =
        [
            '\r', '\n', '\t', ' ', '\u00A0', '\u3000', '\v', '\f',
            'a', 'b', 'Z', 'é', '"', '\'', '[', ']', ';', '-',
            '*', '/', 'O', 'b', 'j', 'e', 'c', 't', ':', 'S', 'r', 'i', 'p', 'D', 'a', 'e',
        ];

        var random = new Random(20260716); // fixed seed: failures must be reproducible
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            var length = random.Next(0, 65);
            var chars = new char[length];
            for (var i = 0; i < length; i++)
            {
                chars[i] = alphabet[random.Next(alphabet.Length)];
            }

            var input = new string(chars);
            var options = AllOptionCombinations[random.Next(AllOptionCombinations.Length)];

            var expected = _reference.Normalize(input, options);
            var actual = _normalizer.Normalize(input, options);

            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                Assert.Fail(
                    $"Fuzz mismatch at iteration {iteration} for input <{Describe(input)}> with options " +
                    $"(LF={options.NormalizeLineEndings}, Trim={options.TrimTrailingWhitespace}, " +
                    $"Strip={options.StripObjectHeaderComment}, Newline={options.EnsureSingleTrailingNewline}): " +
                    $"expected <{Describe(expected)}> but got <{Describe(actual)}>.");
            }
        }
    }

    [Fact]
    public void Corpus_NormalizeIsIdempotent_WithDefaultOptions()
    {
        foreach (var input in Corpus)
        {
            var once = _normalizer.Normalize(input);
            var twice = _normalizer.Normalize(once);

            if (!string.Equals(once, twice, StringComparison.Ordinal))
            {
                Assert.Fail(
                    $"Not idempotent for input <{Describe(input)}>: first <{Describe(once)}>, second <{Describe(twice)}>.");
            }
        }
    }

    [Fact]
    public void AlreadyNormalizedInput_ReturnsSameInstance()
    {
        // Steady-state inputs: LF endings, no trailing whitespace, single trailing newline,
        // and nothing the header regex could ever match.
        string[] steadyState =
        [
            "CREATE VIEW dbo.v AS SELECT 1 AS a;\nGO\n",
            "a\nb\n",
            "a\n\nb\n", // interior blank line is already canonical
            "a\u00A0b\n", // unicode whitespace mid-line is preserved content
            "-- Script Date: 1/1/2026\nSELECT 1;\n", // mentions Script Date but has no "/*"
            "\n",
        ];

        foreach (var input in steadyState)
        {
            Assert.Same(input, _normalizer.Normalize(input));
        }
    }
}

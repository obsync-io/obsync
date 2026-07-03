using Obsync.Shared.Objects;

namespace Obsync.Shared.Scripting;

/// <summary>
/// Object-exclusion rules parsed from a <c>.obsyncignore</c> file (a versioned, .gitignore-style file
/// in the destination folder). Excludes objects by schema, name pattern, or type. Merged with a job's
/// model-level <c>IgnorePatterns</c> at run time.
/// </summary>
public sealed class IgnoreRules
{
    /// <summary>Schema globs — any object whose schema matches is excluded.</summary>
    public List<string> Schemas { get; } = [];

    /// <summary>Object name globs (matched against <c>schema.name</c> and the bare name).</summary>
    public List<string> ObjectPatterns { get; } = [];

    /// <summary>Object types to exclude entirely.</summary>
    public HashSet<SqlObjectType> Types { get; } = [];

    public bool IsEmpty => Schemas.Count == 0 && ObjectPatterns.Count == 0 && Types.Count == 0;

    /// <summary>Folds a job's model-level ignore patterns into the object-name rules.</summary>
    public void AddObjectPatterns(IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                ObjectPatterns.Add(pattern.Trim());
            }
        }
    }

    /// <summary>True when the object should be excluded from scripting.</summary>
    public bool Matches(SqlObjectType type, string schema, string name)
    {
        if (Types.Contains(type))
        {
            return true;
        }

        foreach (var glob in Schemas)
        {
            if (Glob.IsMatch(schema, glob))
            {
                return true;
            }
        }

        var qualified = string.IsNullOrEmpty(schema) ? name : $"{schema}.{name}";
        foreach (var glob in ObjectPatterns)
        {
            if (Glob.IsMatch(qualified, glob) || Glob.IsMatch(name, glob))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses <c>.obsyncignore</c> text. Lines: <c># comments</c> (whole-line and inline) and blanks are
    /// skipped; a <c>schemas:</c> / <c>objects:</c> / <c>types:</c> prefix takes a comma-separated list;
    /// a bare line is an object glob. Unknown <c>types:</c> tokens are skipped.
    /// </summary>
    public static IgnoreRules Parse(string? content)
    {
        var rules = new IgnoreRules();
        if (string.IsNullOrWhiteSpace(content))
        {
            return rules;
        }

        foreach (var rawLine in content.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                var key = line[..colon].Trim().ToLowerInvariant();
                var values = line[(colon + 1)..]
                    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                switch (key)
                {
                    case "schema":
                    case "schemas":
                        rules.Schemas.AddRange(values);
                        break;
                    case "object":
                    case "objects":
                    case "pattern":
                    case "patterns":
                        rules.ObjectPatterns.AddRange(values);
                        break;
                    case "type":
                    case "types":
                        foreach (var value in values)
                        {
                            if (SqlObjectTypeAliases.TryResolve(value, out var type))
                            {
                                rules.Types.Add(type);
                            }
                        }

                        break;
                    default:
                        // Unknown directive (likely a typo) — skip rather than treat as a stray glob.
                        break;
                }

                continue;
            }

            // A bare line is an object glob (e.g. dbo.tmp_*).
            rules.ObjectPatterns.Add(line);
        }

        return rules;
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        return hash >= 0 ? line[..hash] : line;
    }
}

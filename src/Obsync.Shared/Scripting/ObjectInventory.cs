using System.Text.Json;

namespace Obsync.Shared.Scripting;

/// <summary>One object in the database inventory manifest. Only stable, deterministic fields are
/// recorded — volatile values such as <c>object_id</c> are deliberately excluded so the file
/// changes only when an object's script actually changes.</summary>
public sealed record ObjectInventoryEntry(string Type, string Schema, string Name, string Path, string Hash);

/// <summary>The serialized shape of <c>metadata/object-inventory.json</c>.</summary>
public sealed record ObjectInventoryDocument
{
    public required string Server { get; init; }
    public required string Database { get; init; }
    public required int ObjectCount { get; init; }
    public required IReadOnlyDictionary<string, int> CountsByType { get; init; }
    public required IReadOnlyList<ObjectInventoryEntry> Objects { get; init; }
}

/// <summary>
/// Serializes the per-database object inventory manifest. Output is fully deterministic: entries
/// are sorted, line endings are forced to LF, and no timestamp or run identifier is included, so
/// the file only changes when the underlying object set or hashes change.
/// </summary>
public static class ObjectInventoryWriter
{
    // NewLine forces LF regardless of platform so the committed file is byte-stable, without a
    // post-hoc Replace that would copy the (potentially ~100 MB) document a second time.
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, NewLine = "\n" };

    public static string Serialize(string server, string database, IEnumerable<ObjectInventoryEntry> entries)
    {
        return JsonSerializer.Serialize(BuildDocument(server, database, entries), Options) + "\n";
    }

    /// <summary>
    /// Streams the manifest to <paramref name="destination"/> — byte-for-byte what
    /// <see cref="Serialize"/> produces as UTF-8 (including the trailing LF), without ever
    /// materializing the document as one string. At VLDB scale the string form is hundreds of
    /// megabytes; the engine hashes and writes the streamed bytes instead.
    /// <see cref="Serialize"/> stays as the reference implementation the byte-identity test
    /// compares against.
    /// </summary>
    public static async Task WriteAsync(
        Stream destination, string server, string database, IEnumerable<ObjectInventoryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        await JsonSerializer.SerializeAsync(destination, BuildDocument(server, database, entries), Options, cancellationToken)
            .ConfigureAwait(false);
        destination.WriteByte((byte)'\n');
    }

    private static ObjectInventoryDocument BuildDocument(
        string server, string database, IEnumerable<ObjectInventoryEntry> entries)
    {
        var ordered = entries
            .OrderBy(e => e.Type, StringComparer.Ordinal)
            .ThenBy(e => e.Schema, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in ordered)
        {
            counts[entry.Type] = counts.GetValueOrDefault(entry.Type) + 1;
        }

        return new ObjectInventoryDocument
        {
            Server = server,
            Database = database,
            ObjectCount = ordered.Count,
            CountsByType = counts,
            Objects = ordered,
        };
    }
}

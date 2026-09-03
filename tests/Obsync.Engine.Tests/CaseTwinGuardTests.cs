using System.Collections.Concurrent;
using Obsync.Engine;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Scripting;

namespace Obsync.Engine.Tests;

/// <summary>
/// Two objects whose names differ only by letter case — possible only under a case-sensitive or
/// binary database collation — cannot be versioned as separate files on Windows: they map to two
/// paths that differ only in case, which NTFS treats as one file. Before this guard the second
/// object silently overwrote the first, the surviving file kept one object's name and the other's
/// body, and on a first run the two concurrent writers collided on the same <c>.obsync-tmp</c> path
/// and failed the whole run with a sharing violation that named neither object.
/// </summary>
public sealed class CaseTwinGuardTests
{
    private static ConcurrentDictionary<string, ScriptedObjectIdentity> NewMap() =>
        new(StringComparer.OrdinalIgnoreCase);

    private static void Guard(
        ConcurrentDictionary<string, ScriptedObjectIdentity> map, ScriptedObjectIdentity identity) =>
        SyncEngine.GuardAgainstCaseTwin(map, SyncEngine.StateKey(identity), identity, "SalesDb");

    private static ScriptedObjectIdentity Proc(string schema, string name) =>
        new(SqlObjectType.StoredProcedure, schema, name);

    [Fact]
    public void TwoObjectsDifferingOnlyByCase_AreRejected_NamingBoth()
    {
        var map = NewMap();
        Guard(map, Proc("dbo", "Foo"));

        var ex = Assert.Throws<InvalidOperationException>(() => Guard(map, Proc("dbo", "FOO")));

        // The message has to name both objects: the whole point is that the user can find them.
        Assert.Contains("dbo.Foo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dbo.FOO", ex.Message, StringComparison.Ordinal);
        Assert.Contains("differ only by letter case", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SalesDb", ex.Message, StringComparison.Ordinal);
        // ...and point at both remedies, since one of them is what the caller must actually do.
        Assert.Contains(".obsyncignore", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rename", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A case-only difference in the SCHEMA collides on the same file just as surely.</summary>
    [Fact]
    public void TwoSchemasDifferingOnlyByCase_AreRejected()
    {
        var map = NewMap();
        Guard(map, Proc("Sales", "Order"));

        var ex = Assert.Throws<InvalidOperationException>(() => Guard(map, Proc("SALES", "Order")));

        Assert.Contains("Sales.Order", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SALES.Order", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard must not fire on a re-entry for the same object. Several paths mark an object more
    /// than once, and turning that into a failed run would be a far worse bug than the one fixed.
    /// </summary>
    [Fact]
    public void TheSameObjectTwice_IsNotATwin()
    {
        var map = NewMap();
        Guard(map, Proc("dbo", "Foo"));
        Guard(map, Proc("dbo", "Foo"));
        Guard(map, Proc("dbo", "Foo"));
    }

    [Fact]
    public void OrdinaryDistinctObjects_AreNotTwins()
    {
        var map = NewMap();
        Guard(map, Proc("dbo", "Foo"));
        Guard(map, Proc("dbo", "Bar"));
        Guard(map, Proc("sales", "Foo"));
        Guard(map, Proc("dbo", "Foo2"));
    }

    /// <summary>
    /// Identity includes the object type, so a procedure and a view may share a name — they land in
    /// different folders and cannot collide on a file.
    /// </summary>
    [Fact]
    public void SameNameDifferentType_IsNotATwin()
    {
        var map = NewMap();
        Guard(map, new ScriptedObjectIdentity(SqlObjectType.StoredProcedure, "dbo", "Thing"));
        Guard(map, new ScriptedObjectIdentity(SqlObjectType.View, "dbo", "Thing"));
    }

    /// <summary>
    /// Non-ASCII twins were the one case the pre-existing state-side guard could still catch,
    /// because SQLite's NOCASE folds ASCII only. This guard must catch them too, so the behaviour
    /// no longer depends on which alphabet the name is written in.
    /// </summary>
    [Fact]
    public void NonAsciiTwins_AreRejectedToo()
    {
        var map = NewMap();
        Guard(map, Proc("dbo", "Ärger"));

        Assert.Throws<InvalidOperationException>(() => Guard(map, Proc("dbo", "ärger")));
    }

    /// <summary>
    /// The guard fires on the second arrival whichever order the workers reach it in, so the run
    /// fails deterministically even though the write race did not.
    /// </summary>
    [Fact]
    public void EitherArrivalOrder_Fails()
    {
        var lowerFirst = NewMap();
        Guard(lowerFirst, Proc("dbo", "foo"));
        Assert.Throws<InvalidOperationException>(() => Guard(lowerFirst, Proc("dbo", "FOO")));

        var upperFirst = NewMap();
        Guard(upperFirst, Proc("dbo", "FOO"));
        Assert.Throws<InvalidOperationException>(() => Guard(upperFirst, Proc("dbo", "foo")));
    }

    /// <summary>
    /// Exactly one of two racing workers must fail: <c>GetOrAdd</c> decides the winner, so the pair
    /// can never both succeed (silent overwrite) or both fail (a confusing double fault).
    /// </summary>
    [Fact]
    public void UnderConcurrency_ExactlyOneOfTheTwinsFails()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var map = NewMap();
            var failures = 0;

            Parallel.Invoke(
                () => { try { Guard(map, Proc("dbo", "Foo")); } catch (InvalidOperationException) { Interlocked.Increment(ref failures); } },
                () => { try { Guard(map, Proc("dbo", "FOO")); } catch (InvalidOperationException) { Interlocked.Increment(ref failures); } });

            Assert.Equal(1, failures);
        }
    }

    /// <summary>
    /// The reason the guard has to exist at all: the mapper cannot resolve this itself, because it
    /// sees one identity at a time and has no way to know a twin exists. Its two outputs differ
    /// only by case — distinct strings, one file on a case-insensitive filesystem.
    /// </summary>
    [Fact]
    public void TheMapperCannotDisambiguateTwins_WhichIsWhyTheGuardExists()
    {
        var mapper = new ObjectFilePathMapper();

        var first = mapper.MapRelativePath(Proc("dbo", "Foo"));
        var second = mapper.MapRelativePath(Proc("dbo", "FOO"));

        Assert.NotEqual(first, second);
        Assert.Equal(first, second, StringComparer.OrdinalIgnoreCase);
    }
}

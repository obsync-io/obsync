using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Xunit;

namespace Obsync.Shared.Tests;

/// <summary>
/// The prior-state map holds one entry per tracked object for the whole of a database pass, and it
/// is the structure a no-change run pays for while a first run does not — which is why steady-state
/// peak memory measured HIGHER than the initial scrape. At the scale this product targets, the
/// difference between the shape it loads and the shape it needs is measured in hundreds of megabytes.
///
/// These tests measure that rather than asserting it, so a change that quietly reintroduces the wide
/// row is caught here instead of on a customer's million-object database.
/// </summary>
public sealed class TrackedObjectSnapshotSizeTests
{
    private const int Objects = 20_000;

    private static long Measure(Action<int> allocate)
    {
        // Settle first, then measure only what the loop allocates. GetAllocatedBytesForCurrentThread
        // counts allocation rather than live set, which is what matters here: every one of these is
        // held for the whole pass, so allocated and retained are the same thing.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Objects; i++)
        {
            allocate(i);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void TheSnapshotIsSubstantiallySmallerThanThePersistedRow()
    {
        var wide = new List<TrackedObjectState>(Objects);
        var slim = new List<TrackedObjectSnapshot>(Objects);

        var wideBytes = Measure(i => wide.Add(new TrackedObjectState
        {
            Id = i,
            JobId = Guid.Empty,
            DatabaseName = "db",
            ObjectType = SqlObjectType.StoredProcedure,
            SchemaName = "dbo",
            ObjectName = $"usp_Object_{i:D6}",
            FilePath = $"db/procedures/dbo.usp_Object_{i:D6}.sql",
            LastHash = new string('a', 64),
        }));

        var slimBytes = Measure(i => slim.Add(new TrackedObjectSnapshot
        {
            Id = i,
            ObjectType = SqlObjectType.StoredProcedure,
            SchemaName = "dbo",
            ObjectName = $"usp_Object_{i:D6}",
            FilePath = $"db/procedures/dbo.usp_Object_{i:D6}.sql",
            LastHash = new string('a', 64),
        }));

        // Both allocate the same strings, so the difference is the row shape alone. The wide row
        // carries nine fields a run never reads: job and database identity, the object id, three
        // timestamps, the commit sha, the run id, the status and an error message.
        var savedPerObject = (wideBytes - slimBytes) / (double)Objects;
        Assert.True(
            savedPerObject >= 80,
            $"The slim snapshot saved only {savedPerObject:N0} bytes per object "
            + $"({wideBytes:N0} against {slimBytes:N0} for {Objects:N0}). If the snapshot has grown a "
            + "field a run does not read, remove it: this structure is held once per tracked object "
            + "for the whole database pass.");
    }

    [Fact]
    public void TheSnapshotCarriesOnlyWhatARunReads()
    {
        // A guard on the shape rather than the size. Every property here is read by the engine —
        // Id to delete a vanished object's state, the identity trio to report and locate it,
        // FilePath to clean up a stale path, LastHash to decide whether anything changed. Adding a
        // property means adding it to a million-element map, so it should have to be argued for.
        Assert.Equal(
            new[] { "Id", "ObjectType", "SchemaName", "ObjectName", "FilePath", "LastHash" }.Order(),
            typeof(TrackedObjectSnapshot).GetProperties().Select(p => p.Name).Order());
    }
}

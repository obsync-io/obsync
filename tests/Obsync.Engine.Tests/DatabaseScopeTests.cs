using Obsync.Engine;
using Obsync.Shared.Models;

namespace Obsync.Engine.Tests;

public sealed class DatabaseScopeTests
{
    private static SqlDatabaseInfo Db(string name, bool online = true) =>
        new() { Name = name, IsOnline = online, SizeMb = 10 };

    [Fact]
    public void FilterUserDatabases_ExcludesCaseInsensitively_AndOrdersByName()
    {
        var resolved = SyncEngine.FilterUserDatabases(
            [Db("Zeta"), Db("alpha"), Db("Staging"), Db("Mid")],
            excludedDatabases: ["STAGING"],
            out var skippedOffline);

        Assert.Equal(["alpha", "Mid", "Zeta"], resolved);
        Assert.Empty(skippedOffline);
    }

    [Fact]
    public void FilterUserDatabases_SkipsOfflineDatabases_AndReportsThem()
    {
        var resolved = SyncEngine.FilterUserDatabases(
            [Db("Online1"), Db("Restoring", online: false), Db("Online2")],
            excludedDatabases: [],
            out var skippedOffline);

        Assert.Equal(["Online1", "Online2"], resolved);
        Assert.Equal(["Restoring"], skippedOffline);
    }

    [Fact]
    public void FilterUserDatabases_DoesNotReportExcludedOfflineDatabases()
    {
        SyncEngine.FilterUserDatabases(
            [Db("Kept"), Db("IgnoredAndOffline", online: false)],
            excludedDatabases: ["ignoredandoffline"],
            out var skippedOffline);

        // Excluded databases are the user's explicit choice — no offline warning noise for them.
        Assert.Empty(skippedOffline);
    }

    [Fact]
    public void FilterUserDatabases_EverythingExcluded_ReturnsEmpty()
    {
        var resolved = SyncEngine.FilterUserDatabases(
            [Db("A"), Db("B")],
            excludedDatabases: ["A", "B"],
            out _);

        Assert.Empty(resolved);
    }
}

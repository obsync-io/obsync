using Microsoft.Data.Sqlite;

namespace Obsync.Integration.Tests;

/// <summary>
/// Releases the pooled connections for ONE test database so its file can be deleted.
/// </summary>
/// <remarks>
/// Test classes used to call <see cref="SqliteConnection.ClearAllPools"/> in their disposal. That
/// method is process-global, and xUnit runs test classes in parallel — so a class finishing its work
/// disposed the pooled connections belonging to every OTHER class still running, and those threw
/// <c>ObjectDisposedException: SQLitePCL.sqlite3</c> from whatever query happened to be in flight.
/// <para>
/// The result was a suite that failed roughly one run in four, in a different unrelated test each
/// time — the kind of failure that gets re-run until it passes rather than diagnosed. Clearing only
/// this database's pool leaves every other test alone.
/// </para>
/// </remarks>
internal static class TestDatabase
{
    /// <summary>
    /// Clears every connection string a test may have opened this database with. The pool is keyed
    /// by the exact string, so a form that is missed here silently clears nothing and the file stays
    /// locked — which is a failed cleanup, not a failed test, but it looks like the latter.
    /// </summary>
    public static void ReleasePool(string databasePath)
    {
        // What SqliteConnectionFactory builds for production code paths...
        var factoryForm = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString();

        // ...and the bare form the migration tests use, which seed a schema directly rather than
        // through the factory (they are testing what the factory would find).
        var bareForm = $"Data Source={databasePath}";

        foreach (var connectionString in new[] { factoryForm, bareForm })
        {
            using var connection = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(connection);
        }
    }
}

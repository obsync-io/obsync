using System.Collections;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Management.Smo;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Scripting;
using SmoServer = Microsoft.SqlServer.Management.Smo.Server;

namespace Obsync.Smo;

/// <summary>
/// The high-fidelity path: scripts tables and complex objects (UDTs, partitions, full-text,
/// assemblies, security policies, etc.) via SQL Server Management Objects. Heavy collections use
/// bulk prefetch with narrowed init-fields so per-object scripting does not trigger N+1 round trips.
/// Large work lists are partitioned across worker connections (SMO is not thread-safe across a
/// shared connection, so each slice gets its own Server) up to the request's scripting parallelism.
/// </summary>
public sealed class SmoScriptProvider : IObjectScriptProvider
{
    /// <summary>
    /// Work lists smaller than this are scripted sequentially on the primary connection — the
    /// fan-out cost (one extra connection + collection enumeration per slice) beats the win.
    /// The same constant sizes the slices: K = min(parallelism, count / 32).
    /// </summary>
    internal const int MinItemsPerSlice = 32;

    /// <summary>
    /// Largest work list the partitioned path bulk-prefetches child metadata for. Prefetch caches
    /// ~7 KB per table on EVERY slice connection (measured), so an unbounded 8-way prefetch of a
    /// 100k-table database would hold multiple gigabytes; above the ceiling scripting stays lazy.
    /// </summary>
    internal const int PrefetchCeiling = 25_000;

    private readonly ILogger<SmoScriptProvider> _logger;

    public SmoScriptProvider(ILogger<SmoScriptProvider> logger) => _logger = logger;

    public ScriptingStrategy Strategy => ScriptingStrategy.Smo;

    private sealed record SmoTypeMap(
        Func<Database, IEnumerable> GetCollection,
        bool SchemaScoped,
        bool HasIsSystemObject,
        Type? HeavyClrType = null);

    private static readonly IReadOnlyDictionary<SqlObjectType, SmoTypeMap> Map = new Dictionary<SqlObjectType, SmoTypeMap>
    {
        // HasIsSystemObject must match the SMO class EXACTLY: reading the property goes through a
        // dynamic binder, and asking a class that lacks it (e.g. UserDefinedDataType) throws at
        // runtime and fails the whole run. Only Table, User, and SqlAssembly expose it (SMO 181).
        // SmoTypeMapTests verifies every flag against the real SMO types by reflection.
        [SqlObjectType.Table] = new(d => d.Tables, true, true, typeof(Table)),
        [SqlObjectType.User] = new(d => d.Users, false, true),
        [SqlObjectType.Role] = new(d => d.Roles, false, false),
        [SqlObjectType.ApplicationRole] = new(d => d.ApplicationRoles, false, false),
        [SqlObjectType.UserDefinedDataType] = new(d => d.UserDefinedDataTypes, true, false),
        [SqlObjectType.UserDefinedTableType] = new(d => d.UserDefinedTableTypes, true, false, typeof(UserDefinedTableType)),
        [SqlObjectType.XmlSchemaCollection] = new(d => d.XmlSchemaCollections, true, false),
        [SqlObjectType.UserDefinedType] = new(d => d.UserDefinedTypes, true, false),
        [SqlObjectType.UserDefinedAggregate] = new(d => d.UserDefinedAggregates, true, false),
        [SqlObjectType.PartitionFunction] = new(d => d.PartitionFunctions, false, false),
        [SqlObjectType.PartitionScheme] = new(d => d.PartitionSchemes, false, false),
        [SqlObjectType.Assembly] = new(d => d.Assemblies, false, true),
        [SqlObjectType.FullTextCatalog] = new(d => d.FullTextCatalogs, false, false),
        [SqlObjectType.ColumnMasterKey] = new(d => d.ColumnMasterKeys, false, false),
        [SqlObjectType.ColumnEncryptionKey] = new(d => d.ColumnEncryptionKeys, false, false),
        [SqlObjectType.SecurityPolicy] = new(d => d.SecurityPolicies, true, false),
    };

    public async IAsyncEnumerable<RawScriptedObject> ScriptAsync(
        ScriptRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var server = SmoConnection.BuildServer(request);
        await SmoConnection.ConnectWithRetryAsync(server, request.MaxRetries, _logger, cancellationToken).ConfigureAwait(false);
        ApplyLockTimeout(server, request);

        var database = server.Databases[request.Database]
            ?? throw new InvalidOperationException($"Database '{request.Database}' was not found on the server.");
        var options = SmoScriptingOptionsFactory.Create(request.Selection);
        var schemaFilter = request.Selection.SchemaFilter;

        foreach (var type in request.Types)
        {
            if (!Map.TryGetValue(type, out var typeMap))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            SetInitFields(server, typeMap);

            // Phase 1 (primary connection): materialize the filtered work list with light init
            // fields. Only Table is watermark-capable on the SMO path — DateLastModified carries
            // the same catalog value as sys.objects.modify_date.
            var watermark = type == SqlObjectType.Table && request.IncrementalWatermarks is { } watermarks
                && watermarks.TryGetValue(type, out var floor) ? floor : (DateTime?)null;

            var work = new List<(string Schema, string Name, IScriptable Instance)>();
            foreach (var obj in typeMap.GetCollection(database))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!ShouldScript(obj, type, typeMap, schemaFilter, out var schema, out var name))
                {
                    continue;
                }

                // Skip WITHOUT yielding: the engine's snapshot pass already marked it as seen.
                if (watermark is { } wm && obj is Table table && table.DateLastModified < wm)
                {
                    continue;
                }

                work.Add((schema, name, (IScriptable)obj));
            }

            var sliceCount = ComputeSliceCount(work.Count, request.ScriptingParallelism);
            if (sliceCount <= 1)
            {
                // Prefetch only pays off for a full sweep of a heavy collection; a watermark has
                // already narrowed the list, and lazy loading per object is cheaper than bulk
                // prefetching children for every table in the database.
                if (watermark is null)
                {
                    Prefetch(database, typeMap, options, type);
                }

                var yielded = 0;
                foreach (var (schema, name, instance) in work)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return ScriptOne(instance, new ScriptedObjectIdentity(type, schema, name), options);
                    // Periodic, not per-object: a thread-pool hop per item is measurable overhead
                    // at scale; every 64th keeps the enumerator cooperative without the cost.
                    if ((++yielded & 63) == 0)
                    {
                        await Task.Yield();
                    }
                }

                continue;
            }

            if (type == SqlObjectType.Table && work.Count >= 5000 && watermark is null)
            {
                // Expectation-setting only: a full SMO sweep at this scale is the slow one-time
                // cost; steady-state incremental runs skip unchanged tables entirely.
                _logger.LogInformation(
                    "Scripting {Count:N0} tables via SMO — a full table sweep at this scale takes a while; " +
                    "incremental runs will skip unchanged tables.", work.Count);
            }

            var names = work.Select(w => (w.Schema, w.Name)).ToList();
            // Prefetch pays off exactly like the sequential branch: only on a full sweep (no
            // watermark). A watermark-narrowed list scripts a handful of objects, and bulk-loading
            // children for every table in the database would cost more than the lazy loads saved.
            // Measured on 2,000 tables: 230s lazy → 54s prefetched (4.2×), at ~7 KB of cached
            // child metadata per table PER SLICE — hence the ceiling: past ~25k tables the 8-way
            // duplicated prefetch would hold gigabytes, so huge sweeps stay lazy (and slow) rather
            // than risking memory; the startup log above sets that expectation.
            var prefetch = watermark is null && work.Count <= PrefetchCeiling;
            await foreach (var raw in ScriptPartitionedAsync(
                request, type, typeMap, names, sliceCount, prefetch, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return raw;
            }
        }
    }

    /// <summary>
    /// Phase 2 for a large work list: partition it into contiguous slices and script each slice on
    /// its own connection, funneling results through a channel back to the single enumerator.
    /// Fault discipline mirrors <c>ChannelPipeline</c>: per-object failures become
    /// <see cref="RawScriptedObject.Skipped"/>, while the first REAL slice-level fault (e.g. a
    /// connection failure) cancels the peers and completes the channel with that exception.
    /// </summary>
    private async IAsyncEnumerable<RawScriptedObject> ScriptPartitionedAsync(
        ScriptRequest request, SqlObjectType type, SmoTypeMap typeMap,
        IReadOnlyList<(string Schema, string Name)> work, int sliceCount, bool prefetch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Unbounded is fine: items are strings the workers have already produced.
        var channel = Channel.CreateUnbounded<RawScriptedObject>(new UnboundedChannelOptions { SingleReader = true });

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linkedCts.Token;

        // First non-cancellation fault wins; teardown-induced OperationCanceledExceptions never
        // mask a real error.
        var gate = new object();
        Exception? failure = null;
        void Record(Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                return;
            }

            lock (gate)
            {
                failure ??= ex;
            }
        }

        var workers = PartitionSlices(work.Count, sliceCount)
            .Select(slice => Task.Run(async () =>
            {
                try
                {
                    await ScriptSliceAsync(request, type, typeMap, work, slice, prefetch, channel.Writer, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Record(ex);
                    await linkedCts.CancelAsync().ConfigureAwait(false); // stop the sibling slices
                }
            }, CancellationToken.None))
            .ToArray();

        // Workers swallow their own faults into `failure`, so WhenAll cannot throw; completing the
        // channel with the captured failure (or null) is what unblocks — and faults — the reader.
        var completion = Task.Run(async () =>
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
            channel.Writer.Complete(failure);
        }, CancellationToken.None);

        await foreach (var raw in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return raw;
        }

        await completion.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Scripts one contiguous slice of the work list on its own dedicated SMO connection.</summary>
    private async Task ScriptSliceAsync(
        ScriptRequest request, SqlObjectType type, SmoTypeMap typeMap,
        IReadOnlyList<(string Schema, string Name)> work, (int Offset, int Count) slice, bool prefetch,
        ChannelWriter<RawScriptedObject> writer, CancellationToken cancellationToken)
    {
        // SMO is not thread-safe across a shared connection — every slice builds its own Server.
        var server = SmoConnection.BuildServer(request);
        try
        {
            await SmoConnection.ConnectWithRetryAsync(server, request.MaxRetries, _logger, cancellationToken).ConfigureAwait(false);
            ApplyLockTimeout(server, request);
            SetInitFields(server, typeMap);

            var database = server.Databases[request.Database]
                ?? throw new InvalidOperationException($"Database '{request.Database}' was not found on the server.");
            // ScriptingOptions is mutable — never share one instance across slices.
            var options = SmoScriptingOptionsFactory.Create(request.Selection);

            // Without this, every Script() call lazily fetches the object's children (columns,
            // indexes, constraints, triggers, extended properties) with per-object round trips —
            // the N+1 that made full table sweeps an order of magnitude slower than the bulk
            // reads below. Each slice prefetches on ITS OWN connection (SMO caches per Server);
            // the child metadata is duplicated across slices, which measured cheaper than the
            // round trips it replaces. Prefetch stays an optimization: on failure the code path
            // below still works lazily (see Prefetch's catch).
            if (prefetch)
            {
                Prefetch(database, typeMap, options, type);
            }

            for (var i = slice.Offset; i < slice.Offset + slice.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (schema, name) = work[i];
                var identity = new ScriptedObjectIdentity(type, schema, name);

                RawScriptedObject result;
                try
                {
                    result = Resolve(database, typeMap, schema, name) is IScriptable instance
                        ? ScriptOne(instance, identity, options)
                        : RawScriptedObject.Skipped(identity,
                            "The object was no longer found when scripting reached it (it may have been dropped during the run).");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Failed to resolve {Type} {Schema}.{Name}: {Message}", type, schema, name, ex.Message);
                    result = RawScriptedObject.Skipped(identity, $"SMO could not script this object: {ex.Message}");
                }

                await writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                server.ConnectionContext.Disconnect();
            }
            catch (Exception ex)
            {
                // Best-effort teardown; a failed disconnect must not mask the slice's outcome.
                _logger.LogDebug("Disconnecting a scripting worker connection failed: {Message}", ex.Message);
            }
        }
    }

    /// <summary>
    /// How many slices a work list is split into: 1 (sequential on the primary connection) when
    /// the list is small or parallelism is off, otherwise capped by both the parallelism and the
    /// per-slice minimum so tiny slices never spawn their own connections.
    /// </summary>
    internal static int ComputeSliceCount(int itemCount, int parallelism) =>
        parallelism <= 1 || itemCount < MinItemsPerSlice
            ? 1
            : Math.Min(parallelism, Math.Max(1, itemCount / MinItemsPerSlice));

    /// <summary>
    /// Splits <paramref name="itemCount"/> items into <paramref name="sliceCount"/> contiguous
    /// (offset, count) slices whose sizes differ by at most one and sum to the item count.
    /// </summary>
    internal static IReadOnlyList<(int Offset, int Count)> PartitionSlices(int itemCount, int sliceCount)
    {
        var slices = new List<(int Offset, int Count)>(sliceCount);
        var baseSize = itemCount / sliceCount;
        var remainder = itemCount % sliceCount;
        var offset = 0;
        for (var i = 0; i < sliceCount; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            slices.Add((offset, size));
            offset += size;
        }

        return slices;
    }

    // Scripting failures become reported skips, never silent drops — the caller yields the result.
    private RawScriptedObject ScriptOne(IScriptable instance, ScriptedObjectIdentity identity, ScriptingOptions options)
    {
        try
        {
            var batches = instance.Script(options).Cast<string>();
            return RawScriptedObject.Scripted(identity, string.Join("\nGO\n", batches));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to script {Type} {Schema}.{Name}: {Message}",
                identity.Type, identity.Schema, identity.Name, ex.Message);
            return RawScriptedObject.Skipped(identity, $"SMO could not script this object: {ex.Message}");
        }
    }

    /// <summary>Looks an object up by key in its (freshly loaded) collection; null when it vanished mid-run.</summary>
    private static object? Resolve(Database database, SmoTypeMap typeMap, string schema, string name)
    {
        dynamic collection = typeMap.GetCollection(database);
        return typeMap.SchemaScoped ? collection[name, schema] : collection[name];
    }

    // Bound how long SMO's metadata reads wait on locks (fail fast on a busy server). 0 = unset.
    private static void ApplyLockTimeout(SmoServer server, ScriptRequest request)
    {
        if (request.SqlLockTimeoutSeconds > 0)
        {
            server.ConnectionContext.ExecuteNonQuery($"SET LOCK_TIMEOUT {request.SqlLockTimeoutSeconds * 1000};");
        }
    }

    // Narrow the heavy collections' enumeration to the fields the filters need. DateLastModified
    // rides along for Table so the incremental watermark check never triggers per-object fetches.
    private static void SetInitFields(SmoServer server, SmoTypeMap typeMap)
    {
        if (typeMap.HeavyClrType is null)
        {
            return;
        }

        // Request only fields the class actually has — asking for a missing one throws.
        string[] fields = typeMap.HeavyClrType == typeof(Table)
            ? ["Schema", "Name", "IsSystemObject", "DateLastModified"]
            : typeMap.HasIsSystemObject
                ? ["Schema", "Name", "IsSystemObject"]
                : ["Schema", "Name"];
        server.SetDefaultInitFields(typeMap.HeavyClrType, fields);
    }

    private void Prefetch(Database database, SmoTypeMap typeMap, ScriptingOptions options, SqlObjectType type)
    {
        if (typeMap.HeavyClrType is null)
        {
            return;
        }

        try
        {
            database.PrefetchObjects(typeMap.HeavyClrType, options);
        }
        catch (Exception ex)
        {
            // Prefetch is an optimization only; lazy loading still produces correct output.
            _logger.LogWarning("Prefetch for {Type} failed; falling back to lazy loading: {Message}", type, ex.Message);
        }
    }

    private static bool ShouldScript(
        object obj, SqlObjectType type, SmoTypeMap typeMap, ICollection<string> schemaFilter,
        out string schema, out string name)
    {
        name = ((NamedSmoObject)obj).Name;
        schema = obj is ScriptSchemaObjectBase scoped ? scoped.Schema : string.Empty;

        if (typeMap.HasIsSystemObject && GetIsSystemObject(obj))
        {
            return false;
        }

        switch (type)
        {
            case SqlObjectType.Role when obj is DatabaseRole role && (role.IsFixedRole || role.Name == "public"):
                return false;
            case SqlObjectType.User when IsSystemUser(name):
                return false;
        }

        if (typeMap.SchemaScoped && schemaFilter.Count > 0 && !schemaFilter.Contains(schema))
        {
            return false;
        }

        return true;
    }

    private static bool GetIsSystemObject(object obj) => (bool)((dynamic)obj).IsSystemObject;

    private static bool IsSystemUser(string name) =>
        name is "dbo" or "guest" or "sys" or "INFORMATION_SCHEMA"
        || name.StartsWith("NT AUTHORITY\\", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("NT SERVICE\\", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("##", StringComparison.Ordinal);
}

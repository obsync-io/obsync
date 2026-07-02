using System.Collections;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Management.Common;
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
/// </summary>
public sealed class SmoScriptProvider : IObjectScriptProvider
{
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
        [SqlObjectType.Table] = new(d => d.Tables, true, true, typeof(Table)),
        [SqlObjectType.User] = new(d => d.Users, false, true),
        [SqlObjectType.Role] = new(d => d.Roles, false, false),
        [SqlObjectType.ApplicationRole] = new(d => d.ApplicationRoles, false, false),
        [SqlObjectType.UserDefinedDataType] = new(d => d.UserDefinedDataTypes, true, true),
        [SqlObjectType.UserDefinedTableType] = new(d => d.UserDefinedTableTypes, true, true, typeof(UserDefinedTableType)),
        [SqlObjectType.XmlSchemaCollection] = new(d => d.XmlSchemaCollections, true, true),
        [SqlObjectType.UserDefinedType] = new(d => d.UserDefinedTypes, true, true),
        [SqlObjectType.UserDefinedAggregate] = new(d => d.UserDefinedAggregates, true, true),
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
        var server = BuildServer(request);
        await ConnectWithRetryAsync(server, request.MaxRetries, cancellationToken).ConfigureAwait(false);
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
            Prefetch(database, typeMap, options, type);

            foreach (var obj in typeMap.GetCollection(database))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!ShouldScript(obj, type, typeMap, schemaFilter, out var schema, out var name))
                {
                    continue;
                }

                // yield return cannot live inside a try/catch, so capture the outcome first.
                string? script = null;
                string? skipReason = null;
                try
                {
                    var batches = ((IScriptable)obj).Script(options).Cast<string>();
                    script = string.Join("\nGO\n", batches);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to script {Type} {Schema}.{Name}: {Message}", type, schema, name, ex.Message);
                    skipReason = $"SMO could not script this object: {ex.Message}";
                }

                var identity = new ScriptedObjectIdentity(type, schema, name);
                if (skipReason is not null)
                {
                    // Report the failure instead of silently dropping the object.
                    yield return RawScriptedObject.Skipped(identity, skipReason);
                    continue;
                }

                yield return RawScriptedObject.Scripted(identity, script!);
                await Task.Yield();
            }
        }
    }

    private void Prefetch(Database database, SmoTypeMap typeMap, ScriptingOptions options, SqlObjectType type)
    {
        if (typeMap.HeavyClrType is null)
        {
            return;
        }

        try
        {
            database.Parent.SetDefaultInitFields(typeMap.HeavyClrType, "Schema", "Name", "IsSystemObject");
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

    /// <summary>
    /// Forces the SMO server connection up front, retrying transient failures (deadlocks, timeouts,
    /// transport/connection blips) with a short growing backoff. The initial connect is where nearly
    /// all transient SMO failures surface; per-object scripting failures are handled separately (as skips).
    /// </summary>
    private async Task ConnectWithRetryAsync(SmoServer server, int maxRetries, CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, maxRetries);
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                server.ConnectionContext.Connect();
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                _logger.LogWarning(
                    "Transient SMO connection failure (attempt {Attempt}/{Max}); retrying: {Message}",
                    attempt, maxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // SQL error numbers worth retrying (transport blips, deadlocks, timeouts, failover). Kept local
    // to avoid coupling the SMO project to the metadata provider's retry helper.
    private static readonly HashSet<int> TransientSqlNumbers =
        [-2, 20, 64, 233, 1205, 1222, 4060, 10053, 10054, 10060, 40197, 40501, 40613];

    private static bool IsTransient(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql && sql.Errors.Cast<SqlError>().Any(err => TransientSqlNumbers.Contains(err.Number)))
            {
                return true;
            }

            if (e is TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    private static SmoServer BuildServer(ScriptRequest request)
    {
        var connection = new ServerConnection
        {
            ServerInstance = request.Profile.ServerName,
            EncryptConnection = request.Profile.Encrypt,
            TrustServerCertificate = request.Profile.TrustServerCertificate,
            StatementTimeout = request.CommandTimeoutSeconds,
            ConnectTimeout = request.Profile.ConnectTimeoutSeconds,
        };

        if (request.Profile.AuthenticationMode == SqlAuthenticationMode.SqlLogin)
        {
            connection.LoginSecure = false;
            connection.Login = request.Profile.Username ?? string.Empty;
            connection.Password = request.Password ?? string.Empty;
        }
        else
        {
            connection.LoginSecure = true;
        }

        return new SmoServer(connection);
    }
}

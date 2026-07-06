using System.Collections;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Management.Smo;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Scripting;
using SmoServer = Microsoft.SqlServer.Management.Smo.Server;

namespace Obsync.Smo;

/// <summary>
/// Scripts server-level (instance-scoped) objects via SMO: logins, user-defined server roles,
/// credentials, linked servers, and SQL Agent jobs/operators/alerts. Login scripts carry SMO's
/// placeholder hashed password — never the real secret — so the files document existence and
/// configuration, not credentials. Objects SMO cannot script are reported as skips, never dropped.
/// </summary>
public sealed class SmoServerScriptProvider : IServerObjectScriptProvider
{
    private readonly ILogger<SmoServerScriptProvider> _logger;

    public SmoServerScriptProvider(ILogger<SmoServerScriptProvider> logger) => _logger = logger;

    private static readonly Dictionary<SqlObjectType, Func<SmoServer, IEnumerable>> Map = new()
    {
        [SqlObjectType.ServerLogin] = s => s.Logins,
        [SqlObjectType.ServerRole] = s => s.Roles,
        [SqlObjectType.ServerCredential] = s => s.Credentials,
        [SqlObjectType.LinkedServer] = s => s.LinkedServers,
        [SqlObjectType.AgentJob] = s => s.JobServer.Jobs,
        [SqlObjectType.AgentOperator] = s => s.JobServer.Operators,
        [SqlObjectType.AgentAlert] = s => s.JobServer.Alerts,
    };

    /// <summary>The server-level object types this provider can script (mirrors the catalog's server band).</summary>
    public static IReadOnlyCollection<SqlObjectType> SupportedTypes => Map.Keys;

    public async IAsyncEnumerable<RawScriptedObject> ScriptAsync(
        ScriptRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var server = SmoConnection.BuildServer(request);
        await SmoConnection.ConnectWithRetryAsync(server, request.MaxRetries, _logger, cancellationToken).ConfigureAwait(false);

        // Bound how long SMO's metadata reads wait on locks (fail fast on a busy server). 0 = unset.
        if (request.SqlLockTimeoutSeconds > 0)
        {
            server.ConnectionContext.ExecuteNonQuery($"SET LOCK_TIMEOUT {request.SqlLockTimeoutSeconds * 1000};");
        }

        var options = SmoScriptingOptionsFactory.Create(request.Selection);

        foreach (var type in request.Types)
        {
            if (!Map.TryGetValue(type, out var getCollection))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // The Agent collections live in msdb, where missing permissions (SQLAgentReaderRole) or
            // an absent Agent (e.g. Express edition) fail the whole collection, not one object.
            // Report that as a single skip for the type instead of failing the run.
            List<object> objects;
            string? enumerateFailure = null;
            try
            {
                objects = [.. getCollection(server).Cast<object>()];
            }
            catch (Exception ex)
            {
                objects = [];
                _logger.LogWarning("Failed to enumerate server {Type} objects: {Message}", type, ex.Message);
                enumerateFailure = $"Could not enumerate this server object type: {ex.Message}";
            }

            if (enumerateFailure is not null)
            {
                yield return RawScriptedObject.Skipped(
                    new ScriptedObjectIdentity(type, string.Empty, SqlObjectTypeCatalog.Get(type).DisplayName),
                    enumerateFailure);
                continue;
            }

            foreach (var obj in objects)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = ((NamedSmoObject)obj).Name;
                if (!ShouldScriptServerObject(type, name, (obj as ServerRole)?.IsFixedRole ?? false))
                {
                    continue;
                }

                // yield return cannot live inside a try/catch, so capture the outcome first.
                string? script = null;
                string? skipReason = null;
                try
                {
                    // SMO's Credential exposes no scripting surface (it does not implement
                    // IScriptable), so its CREATE statement is composed from properties instead.
                    script = obj is Credential credential
                        ? ScriptCredential(credential)
                        : string.Join("\nGO\n", ((IScriptable)obj).Script(options).Cast<string>());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to script server {Type} {Name}: {Message}", type, name, ex.Message);
                    skipReason = $"SMO could not script this object: {ex.Message}";
                }

                var identity = new ScriptedObjectIdentity(type, string.Empty, name);
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

    /// <summary>
    /// The server-object filter: skips system certificate logins (<c>##…##</c>) and fixed server
    /// roles including <c>public</c>. Everything else is scripted.
    /// </summary>
    public static bool ShouldScriptServerObject(SqlObjectType type, string name, bool isFixedRole) => type switch
    {
        SqlObjectType.ServerLogin => !name.StartsWith("##", StringComparison.Ordinal),
        SqlObjectType.ServerRole => !isFixedRole && name != "public",
        _ => true,
    };

    // The credential's secret is never readable — like login passwords, the script documents the
    // credential's existence and identity, not the secret itself.
    private static string ScriptCredential(Credential credential)
    {
        var script = $"CREATE CREDENTIAL {Quote(credential.Name)} WITH IDENTITY = N'{Escape(credential.Identity)}'";
        if (!string.IsNullOrEmpty(credential.ProviderName))
        {
            script += $" FOR CRYPTOGRAPHIC PROVIDER {Quote(credential.ProviderName)}";
        }

        return script + ";";
    }

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string Escape(string literal) => literal.Replace("'", "''", StringComparison.Ordinal);
}

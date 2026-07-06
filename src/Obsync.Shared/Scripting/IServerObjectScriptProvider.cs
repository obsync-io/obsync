namespace Obsync.Shared.Scripting;

/// <summary>
/// Produces scripts for server-level (instance-scoped) objects: logins, server roles, credentials,
/// linked servers, and SQL Agent jobs/operators/alerts. The request's
/// <see cref="ScriptRequest.Database"/> is empty for the server scope — implementations connect at
/// the instance level and never use it.
/// </summary>
public interface IServerObjectScriptProvider
{
    /// <summary>Streams scripted server-level objects for the requested types. Honors cancellation.</summary>
    IAsyncEnumerable<RawScriptedObject> ScriptAsync(ScriptRequest request, CancellationToken cancellationToken = default);
}

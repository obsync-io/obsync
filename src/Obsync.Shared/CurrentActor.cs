namespace Obsync.Shared;

/// <summary>
/// The current OS identity, used to attribute audit events and runs. In the desktop app this is the
/// interactive user; in the Obsync Windows Service it is the service account the process runs under.
/// Dependency-free (no Windows-only assembly), so it is usable from every layer.
/// </summary>
public static class CurrentActor
{
    /// <summary>The identity as <c>DOMAIN\user</c> (or <c>MACHINE\user</c> for a local account).</summary>
    public static string Name
    {
        get
        {
            try
            {
                return $"{Environment.UserDomainName}\\{Environment.UserName}";
            }
            catch
            {
                return Environment.UserName;
            }
        }
    }
}

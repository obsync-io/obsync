using Microsoft.Win32;

namespace Obsync.App.Services;

/// <summary>
/// Best-effort detection of a SQL Server instance installed on this machine, used to pre-fill the
/// server name when adding a new server. Reads the SQL Server registry keys; never throws.
/// </summary>
internal static class LocalServerDetector
{
    public static string? Detect()
    {
        try
        {
            // Full engine instances installed locally.
            using (var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL"))
            {
                if (key is not null)
                {
                    var instances = key.GetValueNames();
                    if (Array.Exists(instances, i => i.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)))
                    {
                        return "localhost";
                    }

                    var named = Array.Find(instances, i => i.Equals("SQLEXPRESS", StringComparison.OrdinalIgnoreCase))
                        ?? (instances.Length > 0 ? instances[0] : null);
                    if (named is not null)
                    {
                        return $"localhost\\{named}";
                    }
                }
            }

            // LocalDB (common on developer machines).
            using var localDb = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Microsoft SQL Server\Local DB\Installed Versions");
            if (localDb is not null && localDb.GetSubKeyNames().Length > 0)
            {
                return "(localdb)\\MSSQLLocalDB";
            }
        }
        catch
        {
            // Detection is best-effort; ignore any registry/access issues.
        }

        return null;
    }
}

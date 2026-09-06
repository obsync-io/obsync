using System.Collections.Concurrent;
using System.Diagnostics;

namespace Obsync.Git;

/// <summary>
/// Every <c>git.exe</c> this process has running right now, so a host that is being terminated can
/// take them with it.
/// </summary>
/// <remarks>
/// <see cref="GitCommandRunner"/> already kills the whole git process tree whenever a command is
/// cancelled or times out, which covers every ordinary path. This exists for the one path that is
/// not ordinary: the service host terminating itself because its shutdown budget expired.
///
/// <para>
/// That terminate ends only the host process. Any <c>git.exe</c> it spawned keeps running — and
/// git is not some unrelated program, it is
/// <c>[INSTALLFOLDER]\tools\git\cmd\git.exe</c>, bundled inside the very directory an uninstall is
/// trying to delete and an upgrade is trying to replace. An orphan there holds the MinGit tree open
/// and defeats the whole reason for terminating in the first place.
/// </para>
///
/// <para>
/// It cannot be solved by killing our own tree: <see cref="Process.Kill(bool)"/> refuses when the
/// tree contains the calling process. So the children are tracked explicitly.
/// </para>
///
/// <para>
/// A stale entry is harmless. Registration is removed in a <c>finally</c>, and
/// <see cref="KillAll"/> tolerates a process that has already exited — the collection is a hint for
/// one last sweep, never a source of truth.
/// </para>
/// </remarks>
public static class RunningGitProcesses
{
    private static readonly ConcurrentDictionary<int, Process> Live = new();

    /// <summary>Tracks a started git process until the returned handle is disposed.</summary>
    public static IDisposable Track(Process process)
    {
        Live[process.Id] = process;
        return new Registration(process.Id);
    }

    /// <summary>
    /// Kills every tracked git process and its descendants. Best effort by design: this runs while
    /// the host is already going down, so a failure here must never prevent the host from exiting.
    /// </summary>
    /// <returns>How many processes were still running and were killed.</returns>
    public static int KillAll()
    {
        var killed = 0;
        foreach (var (id, process) in Live.ToArray())
        {
            Live.TryRemove(id, out _);
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                killed++;
            }
            catch
            {
                // Exited between the check and the kill, already reaped, or access denied. Nothing
                // useful to do while terminating.
            }
        }

        return killed;
    }

    private sealed class Registration : IDisposable
    {
        private readonly int _id;

        public Registration(int id) => _id = id;

        public void Dispose() => Live.TryRemove(_id, out _);
    }
}

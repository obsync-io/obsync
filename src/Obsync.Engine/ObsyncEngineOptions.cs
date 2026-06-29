namespace Obsync.Engine;

/// <summary>Engine-wide configuration.</summary>
public sealed class ObsyncEngineOptions
{
    /// <summary>Root folder under which per-repository Git workspaces are cloned.</summary>
    public string WorkspacesRoot { get; set; } = string.Empty;

    /// <summary>Email used as the Git committer identity for Obsync commits.</summary>
    public string CommitterEmail { get; set; } = "obsync@localhost";
}

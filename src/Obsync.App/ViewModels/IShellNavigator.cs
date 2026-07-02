namespace Obsync.App.ViewModels;

/// <summary>Drives top-level shell navigation, including drill-down into a single job's workspace.</summary>
public interface IShellNavigator
{
    /// <summary>Shows the job workspace (detail) page for a job. <paramref name="origin"/> is the
    /// section the drill-down started from, so the rail highlight and "Back" return there.</summary>
    Task ShowJobDetailAsync(Guid jobId, string origin = "Jobs");

    /// <summary>Shows one of the top-level sections (Dashboard, Jobs, Connections, …).</summary>
    Task ShowSectionAsync(string section);
}

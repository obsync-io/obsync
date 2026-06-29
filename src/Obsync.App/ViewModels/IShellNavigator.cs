namespace Obsync.App.ViewModels;

/// <summary>Drives top-level shell navigation, including drill-down into a single job's workspace.</summary>
public interface IShellNavigator
{
    /// <summary>Shows the job workspace (detail) page for a job.</summary>
    Task ShowJobDetailAsync(Guid jobId);

    /// <summary>Shows one of the top-level sections (Dashboard, Jobs, Connections, …).</summary>
    Task ShowSectionAsync(string section);
}

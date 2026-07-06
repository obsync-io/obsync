namespace Obsync.App.ViewModels;

/// <summary>One in-app notification card, shown bottom-right in the shell until dismissed or timed out.</summary>
public sealed class ToastItem
{
    public required string Title { get; init; }
    public required string Message { get; init; }

    /// <summary>True for a failure (red accent); false for a warning (amber accent).</summary>
    public bool IsError { get; init; }

    /// <summary>"View details" opens this job's workspace; null opens the History page instead.</summary>
    public Guid? JobId { get; init; }
}

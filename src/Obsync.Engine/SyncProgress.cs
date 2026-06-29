namespace Obsync.Engine;

/// <summary>The phase a run is currently in, for friendly progress reporting.</summary>
public enum SyncPhase
{
    Connecting,
    PreparingRepository,
    Scripting,
    DetectingChanges,
    Committing,
    Pushing,
    Completed,
}

/// <summary>A progress update emitted while a run executes.</summary>
public sealed record SyncProgress(SyncPhase Phase, string Message, int Done = 0, int Total = 0);

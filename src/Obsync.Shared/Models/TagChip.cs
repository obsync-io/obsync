namespace Obsync.Shared.Models;

/// <summary>A job/run tag classified for display: its text and whether it marks production.</summary>
public sealed record TagChip(string Text, bool IsProduction);

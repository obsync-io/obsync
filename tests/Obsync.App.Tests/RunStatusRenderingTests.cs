using System.Globalization;
using NSubstitute;
using Obsync.App.Converters;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;

namespace Obsync.App.Tests;

/// <summary>
/// Every <see cref="RunStatus"/> has to render as something a user can read. The converters all
/// end in a <c>_ =&gt;</c> fallback, so a newly added status does not fail the build or throw — it
/// silently renders as "Not run" in a neutral colour, which is exactly how a status can ship
/// looking like a bug. These tests make that omission fail instead.
/// </summary>
public sealed class RunStatusRenderingTests
{
    private static readonly StatusToTextConverter Text = new();

    [Fact]
    public void EveryRunStatus_HasItsOwnLabel()
    {
        foreach (var status in Enum.GetValues<RunStatus>())
        {
            var label = (string)Text.Convert(status, typeof(string), null, CultureInfo.InvariantCulture);

            Assert.False(
                string.IsNullOrWhiteSpace(label),
                $"{status} renders as blank.");
            Assert.False(
                label == "Not run",
                $"{status} fell through to the \"Not run\" fallback instead of getting a label.");
        }
    }

    /// <summary>The fallback still has to work for the null a never-run job supplies.</summary>
    [Fact]
    public void NoStatus_StillRendersTheNotRunFallback() =>
        Assert.Equal("Not run", Text.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void SkippedRendersAsSkipped() =>
        Assert.Equal("Skipped", Text.Convert(RunStatus.Skipped, typeof(string), null, CultureInfo.InvariantCulture));

    /// <summary>
    /// The History filter is how a user finds these rows once they know to look, so a status the
    /// engine can write must be selectable there.
    /// </summary>
    [Fact]
    public void EveryRunStatus_IsSelectableInTheHistoryFilter()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var history = new HistoryViewModel(
            Substitute.For<IRunRepository>(), Substitute.For<IJobRepository>(),
            Substitute.For<IRepositoryProfileRepository>(), Substitute.For<IRunReportWriter>(),
            Substitute.For<IAppSettingsRepository>(), clock, Substitute.For<IJobRunCoordinator>());

        var filterable = history.StatusOptions
            .Where(f => f.Status is not null)
            .Select(f => f.Status!.Value)
            .ToHashSet();

        foreach (var status in Enum.GetValues<RunStatus>())
        {
            Assert.True(filterable.Contains(status), $"{status} cannot be filtered for in History.");
        }
    }
}

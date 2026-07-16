using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.Tests;

/// <summary>
/// Layout regression net for the Jobs and Dashboard tables: at a 960-wide content area (the window
/// MinWidth) no column header may truncate, no badge/action cell may be wider than its column, and
/// the columns together must fit the grid — the historical defects were a truncated "Changes"
/// header and an Actions column clipping its own buttons. Rows carry worst-case content ("No
/// changes" badge, paused + overdue jobs, full action button set) so the widest real cells are
/// exercised, with virtualization off so every row actually realizes.
/// </summary>
[Collection("wpf application")]
public sealed class TableLayoutTests
{
    private const double Width = 960;
    private const double Tolerance = 0.5;

    [Fact]
    public void JobsAndDashboardTables_FitA960WideContentArea_WithoutTruncation()
    {
        Exception? failure = null;
        var problems = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? DesignSystemTests.CreateApp();

                var jobs = SampleJobs();
                var jobsVm = NewJobsViewModel(jobs);
                jobsVm.LoadAsync().GetAwaiter().GetResult();
                problems.AddRange(Probe("JobsView", new Views.JobsView { DataContext = jobsVm }));

                var dashboardVm = NewDashboardViewModel(jobs);
                dashboardVm.LoadAsync().GetAwaiter().GetResult();
                problems.AddRange(Probe("DashboardView", new Views.DashboardView { DataContext = dashboardVm }));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(failure is null, $"Layout probe failed: {failure}");
        Assert.True(problems.Count == 0, $"Layout problems at {Width}px:\n{string.Join("\n", problems)}");
    }

    // Worst-case rows: the widest status badge ("No changes"), a paused job, an overdue job, tags,
    // and full timestamps.
    private static SyncJob[] SampleJobs()
    {
        var past = new DateTimeOffset(2026, 6, 28, 23, 58, 0, TimeSpan.Zero);
        return
        [
            new SyncJob
            {
                Name = "Nightly production sync", Enabled = true, Tags = ["prod", "finance"],
                Schedule = new ScheduleProfile { Kind = ScheduleKind.Daily },
                RunSummary = new JobRunSummary
                {
                    LastStatus = RunStatus.NoChanges, LastRunAt = past, LastChangeCount = 12345, NextRunAt = past,
                },
            },
            new SyncJob
            {
                Name = "Paused job", Enabled = false,
                Schedule = new ScheduleProfile { Kind = ScheduleKind.Weekly },
                RunSummary = new JobRunSummary { LastStatus = RunStatus.Succeeded, LastRunAt = past },
            },
            new SyncJob
            {
                Name = "Failed job", Enabled = true,
                Schedule = new ScheduleProfile { Kind = ScheduleKind.Hourly },
                RunSummary = new JobRunSummary { LastStatus = RunStatus.Failed, LastRunAt = past, NextRunAt = past.AddHours(1) },
            },
        ];
    }

    private static JobsViewModel NewJobsViewModel(SyncJob[] jobs)
    {
        var (repository, settings, clock, health) = CommonMocks(jobs);
        return new JobsViewModel(
            repository, Substitute.For<IConnectionProfileRepository>(), Substitute.For<IRepositoryProfileRepository>(),
            Substitute.For<IJobRunCoordinator>(), Substitute.For<IAuditWriter>(), settings,
            Substitute.For<IJobConfigPorter>(), health, clock);
    }

    private static DashboardViewModel NewDashboardViewModel(SyncJob[] jobs)
    {
        var (repository, settings, clock, health) = CommonMocks(jobs);
        var runs = Substitute.For<IRunRepository>();
        runs.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SyncRun>>([]));
        return new DashboardViewModel(
            repository, Substitute.For<IConnectionProfileRepository>(), Substitute.For<IRepositoryProfileRepository>(),
            runs, Substitute.For<IObjectStateRepository>(), Substitute.For<IJobRunCoordinator>(),
            Substitute.For<IShellNavigator>(), settings, health, clock);
    }

    private static (IJobRepository Jobs, IAppSettingsRepository Settings, IClock Clock, ISchedulerHealthService Health)
        CommonMocks(SyncJob[] jobs)
    {
        var repository = Substitute.For<IJobRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SyncJob>>([.. jobs]));

        var settings = Substitute.For<IAppSettingsRepository>();
        settings.GetProductionTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["prod"]));

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));

        var health = Substitute.For<ISchedulerHealthService>();
        health.GetAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SchedulerHealth(SchedulerHealthStatus.Healthy, "Healthy")));

        return (repository, settings, clock, health);
    }

    private static List<string> Probe(string view, UIElement element)
    {
        // Rooting under a (never shown) Window connects StaticResource lookups, as at runtime.
        _ = new Window { Width = Width, Height = 900, Content = element };
        element.Measure(new Size(Width, 900));
        element.Arrange(new Rect(0, 0, Width, 900));
        element.UpdateLayout();

        var problems = new List<string>();
        var grids = Descendants(element).OfType<DataGrid>()
            .Where(g => g.Visibility == Visibility.Visible)
            .ToList();
        if (grids.Count == 0)
        {
            problems.Add($"{view}: no visible DataGrid found — the probe checked nothing.");
        }

        foreach (var grid in grids)
        {
            // Rows must realize so the badge/action cells below actually exist.
            VirtualizingPanel.SetIsVirtualizing(grid, false);
            grid.UpdateLayout();

            var columns = grid.Columns.Sum(c => c.ActualWidth);
            if (columns > grid.ActualWidth + Tolerance)
            {
                problems.Add($"{view}: columns need {columns:F1}px but the grid is {grid.ActualWidth:F1}px — the trailing column clips.");
            }

            // In this detached probe star columns collapse to their MinWidth (the viewport-driven
            // star distribution only happens in a presented window), so these checks exercise the
            // WORST case: every flexible column at its floor.
            foreach (var header in Descendants(grid).OfType<DataGridColumnHeader>())
            {
                foreach (var text in Descendants(header).OfType<TextBlock>())
                {
                    var full = FullTextWidth(text);
                    var available = header.ActualWidth - header.Padding.Left - header.Padding.Right;
                    if (full > available + Tolerance)
                    {
                        problems.Add($"{view}: header '{text.Text}' needs {full:F1}px but got {available:F1}px.");
                    }
                }
            }

            var rows = 0;
            foreach (var cell in Descendants(grid).OfType<DataGridCell>())
            {
                rows++;
                // A horizontal StackPanel (badges, action buttons) measures its children unbounded,
                // so a DesiredSize wider than the cell's content area (its template pads 12+12)
                // means visible clipping.
                foreach (var panel in Descendants(cell).OfType<StackPanel>()
                             .Where(p => p.Orientation == Orientation.Horizontal))
                {
                    if (panel.DesiredSize.Width > cell.ActualWidth - 24 + Tolerance)
                    {
                        problems.Add($"{view}: a cell in column '{cell.Column?.Header}' needs {panel.DesiredSize.Width:F1}px but has {cell.ActualWidth - 24:F1}px.");
                    }
                }
            }

            if (rows == 0)
            {
                problems.Add($"{view}: no rows realized — the badge/action checks did not run.");
            }
        }

        return problems;
    }

    /// <summary>The width the TextBlock's full text would need (no trimming), from its own
    /// effective font settings and the probe's real DPI.</summary>
    private static double FullTextWidth(TextBlock text)
    {
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var formatted = new FormattedText(
            text.Text, CultureInfo.CurrentUICulture, text.FlowDirection, typeface, text.FontSize,
            Brushes.Black, null, TextOptions.GetTextFormattingMode(text),
            VisualTreeHelper.GetDpi(text).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grandChild in Descendants(child))
            {
                yield return grandChild;
            }
        }
    }
}

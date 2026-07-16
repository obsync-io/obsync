using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.App.Views;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.Tests;

/// <summary>
/// Headless render probe for the History page's Runs grid at the narrowest supported layout: the
/// 960 px minimum window width minus the expanded navigation rail. Asserts the columns fit the
/// grid (no horizontal clipping) and that no cell's content wants more width than its cell has —
/// the truncation class of defect the 0.8.x grid shipped with.
/// </summary>
[Collection("wpf application")]
public sealed class HistoryRunsRenderTests
{
    /// <summary>The content area at the 960 px minimum: 960 − 224 rail − 1 rail border.</summary>
    private const double NarrowContentWidth = 735;

    [Fact]
    public void HistoryRuns_FitAndRender_AtTheMinimumWindowWidth()
    {
        Exception? failure = null;
        string? pngPath = null;
        var clipped = new List<string>();
        double columnSum = 0, gridWidth = 0;

        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? CreateApp();

                var now = DateTimeOffset.Now;
                var warning = new SyncRun
                {
                    JobName = "Nightly schema sync with a deliberately long name", RunKey = "20260716-020000",
                    Status = RunStatus.Warning, Trigger = RunTrigger.Scheduled, StartedAt = now.AddHours(-2),
                    TriggeredBy = @"CONTOSO\svc-obsync", Databases = "SalesDB, CRM",
                    ObjectsScanned = 42120, ObjectsAdded = 3, ObjectsModified = 12, ObjectsDeleted = 1, ObjectsFailed = 12,
                    CommitSha = new string('a', 40), Tags = ["prod"],
                };
                var prRun = new SyncRun
                {
                    JobName = "PR sync", RunKey = "20260715-150000", Status = RunStatus.Succeeded,
                    Trigger = RunTrigger.CatchUp, StartedAt = now.AddDays(-1), Databases = "SalesDB",
                    ObjectsScanned = 900, ObjectsModified = 2, CommitSha = new string('b', 40),
                    PullRequestNumber = 123, PullRequestUrl = "https://github.com/acme/sql-history/pull/123",
                };
                var idle = new SyncRun
                {
                    JobName = "Ad-hoc export", RunKey = "20260714-120000", Status = RunStatus.NoChanges,
                    Trigger = RunTrigger.Manual, StartedAt = now.AddDays(-2), Databases = "CRM", ObjectsScanned = 15000,
                };

                var runs = Substitute.For<IRunRepository>();
                runs.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<SyncRun>>([warning, prRun, idle]));

                var clock = Substitute.For<IClock>();
                clock.UtcNow.Returns(now);

                var viewModel = new HistoryViewModel(
                    runs, Substitute.For<IJobRepository>(), Substitute.For<IRepositoryProfileRepository>(),
                    Substitute.For<IRunReportWriter>(), Substitute.For<IAppSettingsRepository>(), clock,
                    Substitute.For<IJobRunCoordinator>());
                viewModel.LoadAsync().GetAwaiter().GetResult();

                var view = new HistoryView { DataContext = viewModel };
                _ = new Window { Width = NarrowContentWidth, Height = 700, Content = view };
                view.Measure(new Size(NarrowContentWidth, 700));
                view.Arrange(new Rect(0, 0, NarrowContentWidth, 700));
                view.UpdateLayout();

                var grid = FindVisual<DataGrid>(view) ?? throw new InvalidOperationException("Runs grid not found.");
                gridWidth = grid.ActualWidth;
                columnSum = grid.Columns.Sum(c => c.ActualWidth);

                // No realized cell may want more width than its column gives it (the status badge,
                // trigger text, and run-time lines would report a larger desired width if clipped).
                foreach (var cell in FindVisuals<DataGridCell>(grid))
                {
                    if (cell.Column is not null && cell.DesiredSize.Width > cell.ActualWidth + 0.5)
                    {
                        clipped.Add($"{cell.Column.Header}: needs {cell.DesiredSize.Width:F1}, has {cell.ActualWidth:F1}");
                    }
                }

                var bitmap = new RenderTargetBitmap((int)NarrowContentWidth, 700, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var directory = Path.Combine(Path.GetTempPath(), "obsync-tests");
                Directory.CreateDirectory(directory);
                pngPath = Path.Combine(directory, "history-runs-960.png");
                using var stream = File.Create(pngPath);
                encoder.Save(stream);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(failure is null, $"Headless render of the History runs grid failed: {failure}");
        Assert.True(columnSum <= gridWidth + 0.5, $"Columns overflow the grid at 960 px: {columnSum:F1} > {gridWidth:F1}");
        Assert.True(clipped.Count == 0, $"Clipped cells at 960 px:\n{string.Join("\n", clipped)}");
        Assert.True(File.Exists(pngPath), "The render probe did not produce a PNG.");
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject =>
        FindVisuals<T>(root).FirstOrDefault();

    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisuals<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static Application CreateApp()
    {
        var app = new Obsync.App.App();
        app.InitializeComponent();
        return app;
    }
}

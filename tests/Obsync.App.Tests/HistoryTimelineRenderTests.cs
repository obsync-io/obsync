using System.IO;
using System.Windows;
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
using Obsync.Shared.Objects;

namespace Obsync.App.Tests;

/// <summary>
/// Headless render probe for the History page's Timeline tab with realistic data: multiple days,
/// mixed statuses, tags, and an expanded entry with change rows. Catches template/resource/binding
/// crashes in the new item templates that the empty-view smoke test cannot reach.
/// </summary>
[Collection("wpf application")]
public sealed class HistoryTimelineRenderTests
{
    [Fact]
    public void HistoryTimeline_LaysOutAndRenders_WithAnExpandedEntry()
    {
        Exception? failure = null;
        string? pngPath = null;

        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? CreateApp();

                var now = DateTimeOffset.Now;
                var success = new SyncRun
                {
                    JobName = "Nightly schema sync", RunKey = "20260707-020000", Status = RunStatus.Succeeded,
                    StartedAt = now.AddHours(-2), TriggeredBy = @"CONTOSO\svc-obsync", Databases = "SalesDB, CRM",
                    ObjectsAdded = 3, ObjectsModified = 12, ObjectsDeleted = 1,
                    CommitSha = new string('a', 40), Tags = ["prod"],
                };
                var failed = new SyncRun
                {
                    JobName = "Ad-hoc export", RunKey = "20260706-150000", Status = RunStatus.Failed,
                    StartedAt = now.AddDays(-1), TriggeredBy = @"CONTOSO\alice", Databases = "SalesDB",
                    ErrorMessage = "The GitHub push was rejected (non-fast-forward).",
                };

                var runs = Substitute.For<IRunRepository>();
                runs.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<SyncRun>>([success, failed]));
                runs.GetChangesAsync(success.Id, Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<ObjectChange>>(
                    [
                        NewChange(ChangeType.Added, "usp_NewOrder"),
                        NewChange(ChangeType.Modified, "vw_Orders"),
                        NewChange(ChangeType.Deleted, "vw_Legacy"),
                    ]));

                var clock = Substitute.For<IClock>();
                clock.UtcNow.Returns(now);

                var viewModel = new HistoryViewModel(
                    runs, Substitute.For<IJobRepository>(), Substitute.For<IRepositoryProfileRepository>(),
                    Substitute.For<IRunReportWriter>(), Substitute.For<IAppSettingsRepository>(), clock);

                viewModel.LoadAsync().GetAwaiter().GetResult();
                var entry = viewModel.TimelineDays[0].Entries[0];
                viewModel.ToggleEntryCommand.ExecuteAsync(entry).GetAwaiter().GetResult();

                var view = new HistoryView { DataContext = viewModel };
                view.Measure(new Size(1220, 900));
                view.Arrange(new Rect(0, 0, 1220, 900));
                view.UpdateLayout();

                // Switch to the Timeline tab so its templates actually realize.
                var tabs = FindTabControl(view) ?? throw new InvalidOperationException("TabControl not found.");
                tabs.SelectedIndex = 1;
                view.UpdateLayout();

                var bitmap = new RenderTargetBitmap(1220, 900, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var directory = Path.Combine(Path.GetTempPath(), "obsync-tests");
                Directory.CreateDirectory(directory);
                pngPath = Path.Combine(directory, "history-timeline.png");
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

        Assert.True(failure is null, $"Headless render of the History timeline failed: {failure}");
        Assert.True(File.Exists(pngPath), "The render probe did not produce a PNG.");
    }

    private static ObjectChange NewChange(ChangeType changeType, string name) => new()
    {
        ChangeType = changeType,
        ObjectType = SqlObjectType.View,
        Schema = "dbo",
        Name = name,
        RelativePath = $"views/dbo.{name}.sql",
    };

    private static System.Windows.Controls.TabControl? FindTabControl(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.TabControl tabs)
            {
                return tabs;
            }

            if (FindTabControl(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static Application CreateApp()
    {
        var app = new Obsync.App.App();
        app.InitializeComponent();
        return app;
    }
}

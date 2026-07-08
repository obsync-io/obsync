using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.App.Views;
using Obsync.Shared;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.App.Tests;

/// <summary>
/// Tests that construct the real WPF <see cref="Application"/> share this collection so two test
/// classes never race to create it (a process can only ever have one).
/// </summary>
[CollectionDefinition("wpf application")]
public sealed class WpfApplicationCollection;

/// <summary>
/// Headless render probe for the script &amp; diff viewer: builds the real app resource graph, loads
/// the window from fake run data, lays it out, and renders it to a bitmap. This surfaces template
/// and resource errors (broken StaticResource, bad bindings that throw, template crashes) that a
/// build cannot catch. Virtualized rows may not realize without a live scroll host — known and fine;
/// the probe is for resource/template crashes, not pixel assertions.
/// </summary>
[Collection("wpf application")]
public sealed class ScriptDiffWindowRenderTests
{
    [Fact]
    public void ScriptDiffWindow_LaysOutAndRenders_FromFakeRunData()
    {
        Exception? failure = null;
        string? pngPath = null;

        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? CreateApp();

                var service = Substitute.For<IScriptHistoryService>();
                service.GetVersionsAsync(
                        Arg.Any<GitRepositoryProfile>(), Arg.Any<string>(), Arg.Any<string>(),
                        Arg.Any<ChangeType>(), Arg.Any<CancellationToken>())
                    .Returns(ScriptVersionsResult.Available(
                        "CREATE VIEW dbo.vw_Orders AS\nSELECT Id, Total FROM dbo.Orders;",
                        "CREATE VIEW dbo.vw_Orders AS\nSELECT Id, Total, Status FROM dbo.Orders;"));
                service.GetFileHistoryAsync(
                        Arg.Any<GitRepositoryProfile>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(ScriptFileHistoryResult.Available(
                    [
                        new ScriptFileVersion(new string('a', 40), DateTimeOffset.Now, "alice", "Alter vw_Orders"),
                        new ScriptFileVersion(new string('b', 40), DateTimeOffset.Now.AddDays(-2), "svc-obsync", "Nightly sync"),
                        new ScriptFileVersion(new string('c', 40), DateTimeOffset.Now.AddDays(-9), "bob", "Create vw_Orders"),
                    ]));

                var viewModel = new ScriptDiffViewModel(service);
                var run = new SyncRun
                {
                    JobName = "Nightly schema sync",
                    RunKey = "20260705-020000",
                    CommitSha = new string('a', 40),
                    CommitUrl = "https://github.com/acme/sql-history/commit/" + new string('a', 40),
                    StartedAt = DateTimeOffset.Now,
                };
                var repository = new GitRepositoryProfile { Name = "sql", Owner = "acme", RepositoryName = "sql-history" };
                IReadOnlyList<ObjectChange> changes =
                [
                    NewChange(ChangeType.Modified, "vw_Orders", "views/dbo.vw_Orders.sql"),
                    NewChange(ChangeType.Added, "usp_NewProc", "procedures/dbo.usp_NewProc.sql"),
                    NewChange(ChangeType.Deleted, "vw_Legacy", "views/dbo.vw_Legacy.sql"),
                ];

                viewModel.LoadAsync(run, changes, repository, preselect: null).GetAwaiter().GetResult();
                viewModel.IsHistoryVisible = true; // render the object-history rail too

                var window = new ScriptDiffWindow { DataContext = viewModel };
                var content = (UIElement)window.Content;
                content.Measure(new Size(1220, 760));
                content.Arrange(new Rect(0, 0, 1220, 760));
                content.UpdateLayout();

                var bitmap = new RenderTargetBitmap(1220, 760, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var directory = Path.Combine(Path.GetTempPath(), "obsync-tests");
                Directory.CreateDirectory(directory);
                pngPath = Path.Combine(directory, "script-diff-window.png");
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

        Assert.True(failure is null, $"Headless render of ScriptDiffWindow failed: {failure}");
        Assert.True(File.Exists(pngPath), "The render probe did not produce a PNG.");
    }

    private static ObjectChange NewChange(ChangeType changeType, string name, string relativePath) => new()
    {
        ChangeType = changeType,
        ObjectType = SqlObjectType.View,
        Schema = "dbo",
        Name = name,
        RelativePath = relativePath,
    };

    private static Application CreateApp()
    {
        var app = new Obsync.App.App();
        app.InitializeComponent();
        return app;
    }
}

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NSubstitute;
using Obsync.App.Services;
using Obsync.App.ViewModels;
using Obsync.App.Views;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.Tests;

/// <summary>
/// Headless render probe for the Create Job wizard: builds the real app resource graph, loads the
/// window with a populated view model, and lays out + renders every step (including the new filter
/// row, preset expander, folder preview/collision, next-run preview, and preflight results) so
/// template and resource errors surface without a desktop.
/// </summary>
[Collection("wpf application")]
public sealed class CreateJobWindowRenderTests
{
    [Fact]
    public void CreateJobWindow_LaysOutAndRendersEveryStep()
    {
        Exception? failure = null;
        var pngPaths = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? DesignSystemTests.CreateApp();

                var viewModel = BuildViewModel();
                var window = new CreateJobWindow { DataContext = viewModel };
                var content = (UIElement)window.Content;
                var directory = Path.Combine(Path.GetTempPath(), "obsync-tests");
                Directory.CreateDirectory(directory);

                for (var step = 1; step <= 4; step++)
                {
                    viewModel.CurrentStep = step;
                    pngPaths.Add(RenderToPng(content, Path.Combine(directory, $"create-job-step{step}.png")));
                }

                // Step 5 via Next so the review summary is built, then add preflight rows so the
                // result template (icon + name + detail, one per status) renders too.
                viewModel.CurrentStep = 4;
                viewModel.NextCommand.Execute(null);
                viewModel.PreflightResults.Add(new DiagnosticResult("SQL connection", DiagnosticStatus.Pass, "Enterprise Edition (16.0)"));
                viewModel.PreflightResults.Add(new DiagnosticResult("Branch 'main'", DiagnosticStatus.Warning, "Not found on the remote."));
                viewModel.PreflightResults.Add(new DiagnosticResult("Credentials", DiagnosticStatus.Fail, "Missing: GitHub access token."));
                pngPaths.Add(RenderToPng(content, Path.Combine(directory, "create-job-step5.png")));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(failure is null, $"Headless render of CreateJobWindow failed: {failure}");
        Assert.Equal(5, pngPaths.Count);
        Assert.All(pngPaths, path => Assert.True(File.Exists(path), $"The render probe did not produce {path}."));
    }

    private static string RenderToPng(UIElement content, string pngPath)
    {
        content.Measure(new Size(760, 900));
        content.Arrange(new Rect(0, 0, 760, 900));
        content.UpdateLayout();

        var bitmap = new RenderTargetBitmap(760, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(pngPath);
        encoder.Save(stream);
        return pngPath;
    }

    private static CreateJobViewModel BuildViewModel()
    {
        var connectionId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();

        var connections = Substitute.For<IConnectionProfileRepository>();
        connections.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<SqlConnectionProfile>>(
                [new SqlConnectionProfile { Id = connectionId, Name = "Prod", ServerName = "SVR" }]));
        var repositories = Substitute.For<IRepositoryProfileRepository>();
        repositories.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<GitRepositoryProfile>>(
                [new GitRepositoryProfile { Id = repositoryId, Name = "R", Owner = "o", RepositoryName = "r", DefaultBranch = "main" }]));
        var jobs = Substitute.For<IJobRepository>();
        jobs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SyncJob>>(
            [new SyncJob { Name = "Colliding job", RepositoryProfileId = repositoryId, DestinationFolder = "environments/SVR/SalesDB" }]));
        var schedulerHealth = Substitute.For<ISchedulerHealthService>();
        schedulerHealth.GetAsync(Arg.Any<CancellationToken>()).Returns(
            new SchedulerHealth(SchedulerHealthStatus.NotRunning, "The Obsync service is stopped."));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var viewModel = new CreateJobViewModel(
            connections, repositories, jobs, Substitute.For<ISqlServerProbe>(),
            Substitute.For<ICredentialStore>(), clock, Substitute.For<IAuditWriter>(),
            schedulerHealth, Substitute.For<IJobPreflightService>());
        viewModel.LoadAsync().GetAwaiter().GetResult();

        // Populate every new surface: filter row + Windows-auth caption (step 1), preset expander
        // (step 2), folder preview + collision warning (step 3), next-run preview + banner (step 4).
        viewModel.Name = "Render probe";
        viewModel.SelectedConnection = viewModel.Connections[0];
        viewModel.Databases.Add(new SelectableDatabase("SalesDB") { IsSelected = true });
        viewModel.Databases.Add(new SelectableDatabase("Warehouse"));
        viewModel.ShowPresetContents = true;
        viewModel.SelectedRepository = viewModel.Repositories[0];
        viewModel.Branch = "main";
        viewModel.SelectedScheduleKind = ScheduleKind.Daily;
        return viewModel;
    }
}

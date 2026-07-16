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
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Results;

namespace Obsync.App.Tests;

/// <summary>
/// Headless render probe for the Job Workspace's Dependencies tab with realistic data — the tab's
/// templates only realize when selected and populated, which the empty-view smoke test never does.
/// </summary>
[Collection("wpf application")]
public sealed class DependencyExplorerRenderTests
{
    [Fact]
    public void DependenciesTab_LaysOutAndRenders_WithResults()
    {
        Exception? failure = null;
        string? pngPath = null;

        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? CreateApp();

                var job = new SyncJob { Name = "Sales sync", ConnectionProfileId = Guid.NewGuid() };
                var connection = new SqlConnectionProfile { Name = "prod", ServerName = "SQL01" };

                var states = Substitute.For<IObjectStateRepository>();
                states.GetDatabasesForJobAsync(job.Id, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<string>>(["SalesDB", "CRM"]));
                states.SearchAsync(job.Id, "SalesDB", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<TrackedObjectState>>(
                    [
                        NewState("dbo", "Customers", SqlObjectType.Table),
                        NewState("dbo", "usp_GetCustomer", SqlObjectType.StoredProcedure),
                        NewState("reporting", "vw_Customers", SqlObjectType.View),
                    ]));

                var probe = Substitute.For<ISqlServerProbe>();
                probe.GetDependenciesAsync(
                        connection, Arg.Any<string?>(), "SalesDB", "dbo", "Customers", Arg.Any<CancellationToken>())
                    .Returns(Result.Success(new SqlObjectDependencies
                    {
                        UsedBy =
                        [
                            new SqlDependencyItem { Schema = "reporting", Name = "vw_Customers", TypeLabel = "View" },
                            new SqlDependencyItem { Schema = "dbo", Name = "usp_GetCustomer", TypeLabel = "Stored procedure" },
                            new SqlDependencyItem { Schema = "dbo", Name = "Orders", TypeLabel = "Table (foreign key)" },
                            new SqlDependencyItem { Schema = "dbo", Name = "trg_CustomerAudit", TypeLabel = "Trigger" },
                        ],
                        Uses =
                        [
                            new SqlDependencyItem { Schema = "dbo", Name = "Regions", TypeLabel = "Table (foreign key)" },
                            new SqlDependencyItem
                            {
                                Name = "Archive.dbo.CustomersHistory", TypeLabel = "Cross-database reference", IsDrillable = false,
                            },
                        ],
                    }));

                var explorer = new DependencyExplorerViewModel(states, probe, Substitute.For<ICredentialStore>());
                explorer.InitializeAsync(job, connection).GetAwaiter().GetResult();
                explorer.SelectedObject = NewState("dbo", "Customers", SqlObjectType.Table);

                var viewModel = new JobDetailViewModel(
                    Substitute.For<IJobRepository>(), Substitute.For<IRunRepository>(),
                    Substitute.For<IConnectionProfileRepository>(), Substitute.For<IRepositoryProfileRepository>(),
                    Substitute.For<IJobRunCoordinator>(), Substitute.For<IShellNavigator>(),
                    Substitute.For<IRunReportWriter>(), Substitute.For<IAppSettingsRepository>(),
                    Substitute.For<IJobConfigPorter>(),
                    Substitute.For<Obsync.App.Services.ISchedulerHealthService>(),
                    Substitute.For<IAuditWriter>(), Substitute.For<IClock>(), explorer);

                var view = new JobDetailView { DataContext = viewModel };
                view.Measure(new Size(1220, 900));
                view.Arrange(new Rect(0, 0, 1220, 900));
                view.UpdateLayout();

                var tabs = FindTabControl(view) ?? throw new InvalidOperationException("TabControl not found.");
                tabs.SelectedIndex = 2; // Dependencies
                view.UpdateLayout();

                var bitmap = new RenderTargetBitmap(1220, 900, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var directory = Path.Combine(Path.GetTempPath(), "obsync-tests");
                Directory.CreateDirectory(directory);
                pngPath = Path.Combine(directory, "dependency-explorer.png");
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

        Assert.True(failure is null, $"Headless render of the Dependencies tab failed: {failure}");
        Assert.True(File.Exists(pngPath), "The render probe did not produce a PNG.");
    }

    private static TrackedObjectState NewState(string schema, string name, SqlObjectType type) => new()
    {
        DatabaseName = "SalesDB", SchemaName = schema, ObjectName = name, ObjectType = type,
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

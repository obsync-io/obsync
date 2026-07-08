using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.App.Views;
using Obsync.Data.Repositories;
using Obsync.GitHub;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.Tests;

/// <summary>
/// Headless render probes for the reworked Repositories page and its Add/Edit dialog. The dialog is
/// a Window, so the UserControl render smoke test never covers it — this is its template/resource
/// crash guard (the same reason ScriptDiffWindowRenderTests exists). DataGrid rows don't realize in
/// a detached probe (virtualization), so these catch resource/template errors, not row pixels.
/// </summary>
[Collection("wpf application")]
public sealed class RepositoriesPageRenderTests
{
    [Fact]
    public void RepositoriesView_AndAddRepositoryDialog_LayOutAndRender()
    {
        Exception? failure = null;
        string? pagePng = null;
        string? dialogPng = null;

        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? CreateApp();

                // Page, with rows present so the grid path (not just the empty state) is exercised.
                var pageVm = new RepositoriesViewModel(
                    Substitute.For<IRepositoryProfileRepository>(), Substitute.For<IGitHubService>(),
                    Substitute.For<ICredentialStore>(), Substitute.For<IAuditWriter>());
                pageVm.Repositories.Add(new GitRepositoryProfile
                {
                    Name = "SQLTest", Owner = "acme", RepositoryName = "sql-history", DefaultBranch = "main",
                });
                pageVm.StatusMessage = "SQLTest: All checks passed — authenticated as alice.";
                pagePng = Render(new RepositoriesView { DataContext = pageVm }, 1220, 760, "repositories-page.png");

                // Dialog, in edit mode with the permission checklist showing.
                var dialogVm = new RepositoryDialogViewModel(
                    Substitute.For<IRepositoryProfileRepository>(), Substitute.For<IGitHubService>(),
                    Substitute.For<ICredentialStore>(), Substitute.For<IClock>(), Substitute.For<IAuditWriter>());
                dialogVm.LoadForEdit(new GitRepositoryProfile
                {
                    Name = "SQLTest", Owner = "acme", RepositoryName = "sql-history", DefaultBranch = "main",
                });
                dialogVm.PermissionChecks.Add(new PermissionCheckLine("Token valid", true));
                dialogVm.PermissionChecks.Add(new PermissionCheckLine("Repository access — acme/sql-history", true));
                dialogVm.PermissionChecks.Add(new PermissionCheckLine("Read (pull)", true));
                dialogVm.PermissionChecks.Add(new PermissionCheckLine("Write / push — Contents", false));
                dialogVm.ValidationResult = "The token can read but NOT write. Pushes will fail — grant it Contents: write.";

                // A never-shown Window builds no visual tree — probe its Content instead.
                var window = new AddRepositoryWindow { DataContext = dialogVm };
                dialogPng = Render((UIElement)window.Content, 560, 720, "add-repository-dialog.png");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(failure is null, $"Headless render failed: {failure}");
        Assert.True(File.Exists(pagePng), "The page render probe did not produce a PNG.");
        Assert.True(File.Exists(dialogPng), "The dialog render probe did not produce a PNG.");
    }

    private static string Render(UIElement element, int width, int height, string fileName)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(Path.GetTempPath(), "obsync-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static Application CreateApp()
    {
        var app = new Obsync.App.App();
        app.InitializeComponent();
        return app;
    }
}

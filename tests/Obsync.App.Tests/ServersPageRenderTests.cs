using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.App.Views;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.Tests;

/// <summary>
/// Headless render probe for the reworked Servers page (SQL Server + Last checked columns, copy
/// affordance) with rows present, so the grid path — not just the empty state — is exercised.
/// Like RepositoriesPageRenderTests, this catches resource/template errors, not row pixels.
/// </summary>
[Collection("wpf application")]
public sealed class ServersPageRenderTests
{
    [Fact]
    public void ServersView_WithTestedAndUntestedRows_LaysOutAndRenders()
    {
        Exception? failure = null;
        string? pagePng = null;

        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? DesignSystemTests.CreateApp();

                var pageVm = new ServersViewModel(
                    Substitute.For<IConnectionProfileRepository>(), Substitute.For<ISqlServerProbe>(),
                    Substitute.For<ICredentialStore>(), Substitute.For<IClock>(), Substitute.For<IAuditWriter>());
                pageVm.Servers.Add(new SqlConnectionProfile
                {
                    Name = "Prod",
                    ServerName = @"PROD-SQL01\SQLAG15",
                    LastTestStatus = ConnectionTestStatus.Connected,
                    LastTestedAt = DateTimeOffset.UtcNow,
                    LastTestDetail = "SQL Server Enterprise Edition (16.0.4105.2)",
                    ServerEdition = "Enterprise Edition",
                    ServerVersion = "16.0.4105.2",
                });
                pageVm.Servers.Add(new SqlConnectionProfile { Name = "New", ServerName = "DEV-SQL02" }); // never tested
                pageVm.StatusMessage = "Copied “PROD-SQL01\\SQLAG15” to the clipboard.";
                pagePng = Render(new ServersView { DataContext = pageVm }, 1220, 760, "servers-page.png");
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
}

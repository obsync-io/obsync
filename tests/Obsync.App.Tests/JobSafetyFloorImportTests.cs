using Obsync.App.Services;
using Obsync.Shared;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.App.Tests;

/// <summary>
/// The rules that used to live only in the job wizard, which meant importing a job configuration
/// bypassed every one of them.
/// </summary>
/// <remarks>
/// Each case below could be saved as an ENABLED job that then simply never worked — and in two of
/// the three, reported success while doing nothing. The safety floor is the one place both the
/// importer and the duplicate-job path pass through, so the checks belong there rather than in the
/// wizard that happens to have had them.
/// </remarks>
public sealed class JobSafetyFloorImportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static SyncJob Job() => new()
    {
        Name = "imported",
        ConnectionProfileId = Guid.NewGuid(),
        RepositoryProfileId = Guid.NewGuid(),
        Databases = ["Sales"],
        Branch = "main",
    };

    [Fact]
    public void ACronScheduleWithNoExpression_IsRefused()
    {
        var job = Job();
        job.Schedule.Kind = ScheduleKind.Cron;
        job.Schedule.CronExpression = null;

        // It would appear in Jobs, show no next run, and never fire.
        Assert.Contains("cron", JobSafetyFloor.FirstProblem(job, Now), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnExportJobWithNoPath_IsRefused()
    {
        var job = Job();
        job.CommitMode = CommitMode.ExportOnly;
        job.ExportPath = null;

        Assert.Contains("export path", JobSafetyFloor.FirstProblem(job, Now), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnExplicitDatabaseListThatIsEmpty_IsRefused()
    {
        var job = Job();
        job.DatabaseScope = DatabaseScope.SelectedDatabases;
        job.Databases = [];

        // The worst of the three: it scripts nothing and reports SUCCESS, so nothing ever suggests
        // the job is misconfigured.
        Assert.NotNull(JobSafetyFloor.FirstProblem(job, Now));
    }

    [Theory]
    [InlineData(-1, 0, 1000)]
    [InlineData(120, -1, 1000)]
    [InlineData(120, 0, -1)]
    public void NegativeNumericSettings_AreRefused(int commandTimeout, int lockTimeout, int referenceRows)
    {
        // A negative command timeout throws from the SqlCommand setter rather than surfacing as a
        // SQL error, so it escapes the per-object containment and fails the whole run; a negative
        // reference-data cap skips every listed table and pins the run at Warning for ever.
        var job = Job();
        job.Advanced.SqlCommandTimeoutSeconds = commandTimeout;
        job.Advanced.SqlLockTimeoutSeconds = lockTimeout;
        job.Advanced.ReferenceDataMaxRows = referenceRows;

        Assert.NotNull(JobSafetyFloor.FirstProblem(job, Now));
    }

    [Fact]
    public void AnOrdinaryJob_StillPasses()
    {
        Assert.Null(JobSafetyFloor.FirstProblem(Job(), Now));
    }
}

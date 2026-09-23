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

    [Theory]
    [InlineData(65)]
    [InlineData(100_000)]
    [InlineData(int.MaxValue)]
    public void AnAbsurdWorkerCount_IsRefused(int workers)
    {
        // The wizard clamps this to 0..64; import assigned Advanced wholesale and the floor only
        // checked for negatives, so the ceiling was reachable through an imported file. The value
        // becomes the consumer-task count verbatim, and at int.MaxValue the channel's `workers * 2`
        // overflows and throws before anything runs.
        var job = Job();
        job.Advanced.MaxParallelWorkers = workers;

        Assert.NotNull(JobSafetyFloor.FirstProblem(job, Now));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(11, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 11)]
    [InlineData(1, int.MaxValue)]
    public void RetryCountsOutsideTheWizardsRange_AreRefused(int sqlRetries, int gitRetries)
    {
        // The worst of the imported-settings family, and the reason these ceilings matter at all:
        // the retry helpers loop on the count with a growing delay, so a huge value turns one
        // transient SQL or network blip into a retry storm that never fails and never ends, holding
        // the job's run lock the whole time. Unlike an absurd worker count it fails SILENTLY —
        // there is no exception, just a run that never finishes.
        var job = Job();
        job.Advanced.SqlRetryCount = sqlRetries;
        job.Advanced.GitRetryCount = gitRetries;

        Assert.NotNull(JobSafetyFloor.FirstProblem(job, Now));
    }

    [Fact]
    public void ALockTimeoutThatWouldSaturate_IsRefused()
    {
        // Above int.MaxValue milliseconds the conversion in SqlLockTimeout saturates, so the job
        // would silently run with a different timeout than it asked for. Refusing is clearer.
        var job = Job();
        job.Advanced.SqlLockTimeoutSeconds = 3_000_000;

        Assert.NotNull(JobSafetyFloor.FirstProblem(job, Now));
    }

    [Fact]
    public void TheWizardsOwnBounds_StillClearTheFloor()
    {
        // Guards against the ceilings above being set tighter than what the wizard itself produces,
        // which would make a normally-created job unsaveable through the import/duplicate paths.
        var job = Job();
        job.Advanced.MaxParallelWorkers = 0; // automatic
        job.Advanced.SqlRetryCount = 1;
        job.Advanced.GitRetryCount = 1;
        job.Advanced.SqlLockTimeoutSeconds = 0;

        Assert.Null(JobSafetyFloor.FirstProblem(job, Now));

        job.Advanced.MaxParallelWorkers = 64;
        job.Advanced.SqlRetryCount = 10;
        job.Advanced.GitRetryCount = 10;

        Assert.Null(JobSafetyFloor.FirstProblem(job, Now));
    }

    [Fact]
    public void AnOrdinaryJob_StillPasses()
    {
        Assert.Null(JobSafetyFloor.FirstProblem(Job(), Now));
    }
}

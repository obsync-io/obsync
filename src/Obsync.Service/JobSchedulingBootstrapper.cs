using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Obsync.Data;
using Obsync.Scheduler;

namespace Obsync.Service;

/// <summary>On startup, initializes the local database and schedules all enabled jobs.</summary>
public sealed class JobSchedulingBootstrapper : IHostedService
{
    private readonly IDatabaseInitializer _databaseInitializer;
    private readonly ISyncJobScheduler _scheduler;
    private readonly ILogger<JobSchedulingBootstrapper> _logger;

    public JobSchedulingBootstrapper(
        IDatabaseInitializer databaseInitializer, ISyncJobScheduler scheduler, ILogger<JobSchedulingBootstrapper> logger)
    {
        _databaseInitializer = databaseInitializer;
        _scheduler = scheduler;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _databaseInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _scheduler.ScheduleAllAsync(cancellationToken).ConfigureAwait(false);

        // Log the identity so credential-isolation problems are diagnosable: secrets in Windows
        // Credential Manager are per-user, so scheduled runs only work if the app saved them under
        // this same account (see the SQL/GitHub credential checks in SyncEngine).
        _logger.LogInformation(
            "Obsync service started and jobs scheduled. Running as {Domain}\\{User}.",
            Environment.UserDomainName, Environment.UserName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

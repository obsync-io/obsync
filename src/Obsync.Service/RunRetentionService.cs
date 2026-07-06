using Obsync.Data;
using Obsync.Data.Repositories;

namespace Obsync.Service;

/// <summary>
/// Applies the run-history retention setting once at service start and then daily, so history
/// stays pruned on machines where the desktop app is rarely opened.
/// </summary>
public sealed class RunRetentionService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IAppSettingsRepository _settings;
    private readonly IRunRepository _runs;
    private readonly ILogger<RunRetentionService> _logger;

    public RunRetentionService(IAppSettingsRepository settings, IRunRepository runs, ILogger<RunRetentionService> logger)
    {
        _settings = settings;
        _runs = runs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var deleted = await RunRetention.CleanupAsync(_settings, _runs, DateTimeOffset.UtcNow, stoppingToken)
                    .ConfigureAwait(false);
                if (deleted > 0)
                {
                    _logger.LogInformation("Run retention removed {Count} run(s) past the configured window.", deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Retention is housekeeping — never let it take the scheduler host down.
                _logger.LogWarning(ex, "Run retention cleanup failed; will retry on the next cycle.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

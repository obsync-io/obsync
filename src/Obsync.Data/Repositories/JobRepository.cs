using Dapper;
using Obsync.Shared;
using Obsync.Shared.Models;

namespace Obsync.Data.Repositories;

/// <summary>Persistence for sync jobs (the central product entity).</summary>
public interface IJobRepository
{
    Task<IReadOnlyList<SyncJob>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<SyncJob?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpsertAsync(SyncJob job, CancellationToken cancellationToken = default);
    Task UpdateRunSummaryAsync(Guid jobId, JobRunSummary summary, CancellationToken cancellationToken = default);

    /// <summary>Updates only the cached "next run" field of a job's run summary, without touching the rest.</summary>
    Task UpdateNextRunAtAsync(Guid jobId, DateTimeOffset? nextRunAt, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IJobRepository" />
public sealed class JobRepository : IJobRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public JobRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    private sealed class JobRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public long Enabled { get; set; }
        public string ConnectionProfileId { get; set; } = string.Empty;
        public string RepositoryProfileId { get; set; } = string.Empty;
        public string DatabasesJson { get; set; } = "[]";
        public string? Branch { get; set; }
        public string DestinationFolder { get; set; } = string.Empty;
        public long CommitMode { get; set; }
        public string? LocalExportPath { get; set; }
        public string SelectionJson { get; set; } = "{}";
        public string ScheduleJson { get; set; } = "{}";
        public string AdvancedJson { get; set; } = "{}";
        public string RunSummaryJson { get; set; } = "{}";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private const string SelectColumns = """
        SELECT id AS Id, name AS Name, description AS Description, enabled AS Enabled,
               connection_profile_id AS ConnectionProfileId, repository_profile_id AS RepositoryProfileId,
               databases_json AS DatabasesJson, branch AS Branch, destination_folder AS DestinationFolder,
               commit_mode AS CommitMode, local_export_path AS LocalExportPath, selection_json AS SelectionJson,
               schedule_json AS ScheduleJson, advanced_json AS AdvancedJson, run_summary_json AS RunSummaryJson,
               created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM jobs
        """;

    public async Task<IReadOnlyList<SyncJob>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<JobRow>(
            new CommandDefinition($"{SelectColumns} ORDER BY name;", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    public async Task<SyncJob?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<JobRow>(
            new CommandDefinition($"{SelectColumns} WHERE id = $id;", new { id = id.ToString() }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task UpsertAsync(SyncJob job, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO jobs
                (id, name, description, enabled, connection_profile_id, repository_profile_id, databases_json,
                 branch, destination_folder, commit_mode, local_export_path, selection_json, schedule_json,
                 advanced_json, run_summary_json, created_at, updated_at)
            VALUES
                ($id, $name, $desc, $enabled, $conn, $repo, $dbs, $branch, $folder, $commit, $local,
                 $selection, $schedule, $advanced, $summary, $created, $updated)
            ON CONFLICT (id) DO UPDATE SET
                name = excluded.name, description = excluded.description, enabled = excluded.enabled,
                connection_profile_id = excluded.connection_profile_id,
                repository_profile_id = excluded.repository_profile_id, databases_json = excluded.databases_json,
                branch = excluded.branch, destination_folder = excluded.destination_folder,
                commit_mode = excluded.commit_mode, local_export_path = excluded.local_export_path,
                selection_json = excluded.selection_json, schedule_json = excluded.schedule_json,
                advanced_json = excluded.advanced_json,
                -- run_summary_json is intentionally NOT overwritten here: it is owned by the engine
                -- (UpdateRunSummaryAsync / UpdateNextRunAtAsync). A config edit must not clobber the
                -- latest run status/commit/next-run written by a concurrent run.
                updated_at = excluded.updated_at;
            """,
            new
            {
                id = job.Id.ToString(),
                name = job.Name,
                desc = job.Description,
                enabled = job.Enabled ? 1 : 0,
                conn = job.ConnectionProfileId.ToString(),
                repo = job.RepositoryProfileId.ToString(),
                dbs = ObsyncJson.Serialize(job.Databases),
                branch = job.Branch,
                folder = job.DestinationFolder,
                commit = (int)job.CommitMode,
                local = job.LocalExportPath,
                selection = ObsyncJson.Serialize(job.Selection),
                schedule = ObsyncJson.Serialize(job.Schedule),
                advanced = ObsyncJson.Serialize(job.Advanced),
                summary = ObsyncJson.Serialize(job.RunSummary),
                created = job.CreatedAt,
                updated = job.UpdatedAt,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task UpdateRunSummaryAsync(Guid jobId, JobRunSummary summary, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE jobs SET run_summary_json = $summary WHERE id = $id;",
            new { summary = ObsyncJson.Serialize(summary), id = jobId.ToString() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task UpdateNextRunAtAsync(Guid jobId, DateTimeOffset? nextRunAt, CancellationToken cancellationToken = default)
    {
        // Patch only the NextRunAt field inside the JSON blob so we never clobber the last-run fields
        // written by a concurrent run. ISO-8601 ("O") matches what System.Text.Json reads back.
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE jobs SET run_summary_json = json_set(json(run_summary_json), '$.NextRunAt', $next) WHERE id = $id;",
            new { next = nextRunAt?.ToString("O"), id = jobId.ToString() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM jobs WHERE id = $id;", new { id = id.ToString() }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static SyncJob Map(JobRow row)
    {
        var selection = ObsyncJson.Deserialize<ObjectSelectionProfile>(row.SelectionJson);
        // System.Text.Json rebuilds the HashSet with the default (ordinal) comparer on read; restore
        // case-insensitivity so schema filtering behaves the same before and after persistence.
        selection.SchemaFilter = new HashSet<string>(selection.SchemaFilter, StringComparer.OrdinalIgnoreCase);

        return new SyncJob
        {
            Id = Guid.Parse(row.Id),
            Name = row.Name,
            Description = row.Description,
            Enabled = row.Enabled != 0,
            ConnectionProfileId = Guid.Parse(row.ConnectionProfileId),
            RepositoryProfileId = Guid.Parse(row.RepositoryProfileId),
            Databases = ObsyncJson.Deserialize<List<string>>(row.DatabasesJson),
            Branch = row.Branch,
            DestinationFolder = row.DestinationFolder,
            CommitMode = (CommitMode)row.CommitMode,
            LocalExportPath = row.LocalExportPath,
            Selection = selection,
            Schedule = ObsyncJson.Deserialize<ScheduleProfile>(row.ScheduleJson),
            Advanced = ObsyncJson.Deserialize<JobAdvancedOptions>(row.AdvancedJson),
            RunSummary = ObsyncJson.Deserialize<JobRunSummary>(row.RunSummaryJson),
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
        };
    }
}

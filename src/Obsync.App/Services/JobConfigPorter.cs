using System.Text.Json;
using System.Text.Json.Serialization;
using Obsync.Data.Repositories;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;

namespace Obsync.App.Services;

/// <summary>
/// The portable, secret-free job definition written by "Export configuration". Server and GitHub
/// repository profiles are referenced by NAME only — passwords/tokens stay in Windows Credential
/// Manager, and the profiles themselves must already exist on the importing machine.
/// </summary>
public sealed class JobConfigFile
{
    public int FormatVersion { get; set; } = 1;
    public string? ExportedFrom { get; set; }

    // References used to re-attach the job to profiles on the importing machine.
    public string? ConnectionName { get; set; }
    public string? ServerName { get; set; }
    public string? RepositoryOwner { get; set; }
    public string? RepositoryName { get; set; }
    public string? RepositoryProfileName { get; set; }

    // The job's configuration (no Id, no run summary, no secrets).
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public DatabaseScope DatabaseScope { get; set; }
    public List<string> Databases { get; set; } = [];
    public List<string> ExcludedDatabases { get; set; } = [];
    public string? Branch { get; set; }
    public string DestinationFolder { get; set; } = string.Empty;
    public CommitMode CommitMode { get; set; }
    public string? LocalExportPath { get; set; }
    public string? ExportPath { get; set; }
    public List<string> Reviewers { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public ObjectSelectionProfile Selection { get; set; } = new();
    public ScheduleProfile Schedule { get; set; } = new();
    public JobAdvancedOptions Advanced { get; set; } = new();
}

/// <summary>The outcome of an import: the created job, or a human-readable reason it was refused.</summary>
public sealed record JobImportResult(SyncJob? Job, string? Error)
{
    public bool IsSuccess => Job is not null;

    public static JobImportResult Success(SyncJob job) => new(job, null);
    public static JobImportResult Failure(string error) => new(null, error);
}

/// <summary>
/// Exports a job's configuration to portable JSON and imports it on another machine, re-attaching
/// the referenced server/repository profiles by name.
/// </summary>
public interface IJobConfigPorter
{
    Task<string> ExportAsync(SyncJob job, CancellationToken cancellationToken = default);

    /// <summary>Validates, resolves profile references, persists the imported job, and audits it.</summary>
    Task<JobImportResult> ImportAsync(string json, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IJobConfigPorter" />
public sealed class JobConfigPorter : IJobConfigPorter
{
    // The export is a user-facing file: indented, camelCase, enums by name — readable and diffable.
    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IJobRepository _jobs;
    private readonly IConnectionProfileRepository _connections;
    private readonly IRepositoryProfileRepository _repositories;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;

    public JobConfigPorter(
        IJobRepository jobs,
        IConnectionProfileRepository connections,
        IRepositoryProfileRepository repositories,
        IClock clock,
        IAuditWriter audit)
    {
        _jobs = jobs;
        _connections = connections;
        _repositories = repositories;
        _clock = clock;
        _audit = audit;
    }

    public async Task<string> ExportAsync(SyncJob job, CancellationToken cancellationToken = default)
    {
        var connection = await _connections.GetAsync(job.ConnectionProfileId, cancellationToken).ConfigureAwait(false);
        GitRepositoryProfile? repository = null;
        if (job.RepositoryProfileId is { } repositoryId)
        {
            repository = await _repositories.GetAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        }

        var file = new JobConfigFile
        {
            ExportedFrom = $"Obsync {VersionInfo.Of(typeof(JobConfigPorter).Assembly)}",
            ConnectionName = connection?.Name,
            ServerName = connection?.ServerName,
            RepositoryOwner = repository?.Owner,
            RepositoryName = repository?.RepositoryName,
            RepositoryProfileName = repository?.Name,
            Name = job.Name,
            Description = job.Description,
            Enabled = job.Enabled,
            DatabaseScope = job.DatabaseScope,
            Databases = [.. job.Databases],
            ExcludedDatabases = [.. job.ExcludedDatabases],
            Branch = job.Branch,
            DestinationFolder = job.DestinationFolder,
            CommitMode = job.CommitMode,
            LocalExportPath = job.LocalExportPath,
            ExportPath = job.ExportPath,
            Reviewers = [.. job.Reviewers],
            Tags = [.. job.Tags],
            Selection = job.Selection,
            Schedule = job.Schedule,
            Advanced = job.Advanced,
        };

        return JsonSerializer.Serialize(file, FileOptions);
    }

    public async Task<JobImportResult> ImportAsync(string json, CancellationToken cancellationToken = default)
    {
        JobConfigFile? file;
        try
        {
            file = JsonSerializer.Deserialize<JobConfigFile>(json, FileOptions);
        }
        catch (JsonException)
        {
            return JobImportResult.Failure("This file is not a valid Obsync job export.");
        }

        if (file is null)
        {
            return JobImportResult.Failure("This file is not a valid Obsync job export.");
        }

        if (file.FormatVersion is < 1 or > 1)
        {
            return JobImportResult.Failure(
                $"This export uses format version {file.FormatVersion}, which this version of Obsync doesn't understand. Update Obsync and try again.");
        }

        if (string.IsNullOrWhiteSpace(file.Name))
        {
            return JobImportResult.Failure("This file is not a valid Obsync job export (it has no job name).");
        }

        // Re-attach the server profile: by profile name first, then by server name.
        var connections = await _connections.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var connection =
            connections.FirstOrDefault(c => Matches(c.Name, file.ConnectionName))
            ?? connections.FirstOrDefault(c => Matches(c.ServerName, file.ServerName));
        if (connection is null)
        {
            return JobImportResult.Failure(
                $"This job uses a server named \"{file.ConnectionName ?? file.ServerName}\" ({file.ServerName}) that isn't set up here. " +
                "Add it under Servers (with its credentials) first, then import again.");
        }

        // Re-attach the GitHub repository profile (git modes only): by owner/name, then profile name.
        GitRepositoryProfile? repository = null;
        if (file.CommitMode != CommitMode.ExportOnly)
        {
            var repositories = await _repositories.GetAllAsync(cancellationToken).ConfigureAwait(false);
            repository =
                repositories.FirstOrDefault(r => Matches(r.Owner, file.RepositoryOwner) && Matches(r.RepositoryName, file.RepositoryName))
                ?? repositories.FirstOrDefault(r => Matches(r.Name, file.RepositoryProfileName));
            if (repository is null)
            {
                return JobImportResult.Failure(
                    $"This job pushes to {file.RepositoryOwner}/{file.RepositoryName}, which isn't set up here. " +
                    "Add it under Repositories (with its access token) first, then import again.");
            }
        }

        var existingNames = (await _jobs.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Select(j => j.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var job = new SyncJob
        {
            Name = UniqueName(file.Name.Trim(), existingNames),
            Description = file.Description,
            Enabled = file.Enabled,
            ConnectionProfileId = connection.Id,
            RepositoryProfileId = repository?.Id,
            DatabaseScope = file.DatabaseScope,
            Databases = file.Databases,
            ExcludedDatabases = file.ExcludedDatabases,
            Branch = file.CommitMode == CommitMode.ExportOnly ? null : file.Branch,
            DestinationFolder = file.DestinationFolder,
            CommitMode = file.CommitMode,
            LocalExportPath = file.LocalExportPath,
            ExportPath = file.ExportPath,
            Reviewers = file.Reviewers,
            Tags = file.Tags,
            Selection = file.Selection,
            Schedule = file.Schedule,
            Advanced = file.Advanced,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        };

        await _jobs.UpsertAsync(job, cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(AuditAction.JobCreated, "Job", job.Id.ToString(), job.Name, "Imported from file", cancellationToken)
            .ConfigureAwait(false);
        return JobImportResult.Success(job);
    }

    private static bool Matches(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    // "Sales Sync" → "Sales Sync (imported)" → "Sales Sync (imported 2)" …
    private static string UniqueName(string name, IReadOnlySet<string> existing)
    {
        if (!existing.Contains(name))
        {
            return name;
        }

        var candidate = $"{name} (imported)";
        for (var i = 2; existing.Contains(candidate); i++)
        {
            candidate = $"{name} (imported {i})";
        }

        return candidate;
    }
}

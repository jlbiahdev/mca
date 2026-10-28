using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FeteadApi.Infrastructure.Services.Monitoring;

/// <summary>
/// Lectures Dapper sur les bases Styx (public.data_job / public.data_task).
/// Lecture seule : le module ne modifie aucune donnée.
/// </summary>
public sealed class MonitoringRepository : ITaskJobResolver
{
    // Date d'activité d'un job : aucune colonne updated_at n'existe sur data_job.
    private const string ActivitySql =
        "COALESCE(j.change_date_time, j.start_date_time, j.submit_date_time, j.create_date_time)";

    private const string StateCountsSql = $"""
        SELECT j.state AS State, COUNT(*)::int AS Count
        FROM public.data_job j
        WHERE {ActivitySql} BETWEEN @From AND @To
        GROUP BY j.state
        """;

    // Graphique (décision D6) : flux par intervalle reconstitué depuis les dates de data_job.
    private const string BucketsSql = """
        SELECT e.state AS State,
               FLOOR(EXTRACT(EPOCH FROM (e.at - @From))::double precision / @BucketSeconds)::int AS BucketIndex,
               COUNT(*)::int AS Count
        FROM (
            SELECT 'Submitted' AS state, j.submit_date_time AS at
            FROM public.data_job j
            WHERE j.submit_date_time BETWEEN @From AND @To
            UNION ALL
            SELECT 'Running', j.start_date_time
            FROM public.data_job j
            WHERE j.start_date_time BETWEEN @From AND @To
            UNION ALL
            SELECT j.state, j.change_date_time
            FROM public.data_job j
            WHERE j.state IN ('Finished', 'Failed', 'Canceled')
              AND j.change_date_time BETWEEN @From AND @To
        ) e
        GROUP BY e.state, BucketIndex
        """;

    private const string LatestJobsSql = $"""
        SELECT j.id AS Id,
               j.name AS Name,
               j.state AS State,
               j.progress AS Progress,
               {ActivitySql} AS UpdatedAt,
               (SELECT COUNT(*) FROM public.data_task t WHERE t.job_id = j.id) AS TaskCount
        FROM public.data_job j
        WHERE {ActivitySql} BETWEEN @From AND @To
        ORDER BY UpdatedAt DESC, j.id DESC
        LIMIT @Limit
        """;

    // Si plusieurs data_task partagent un external_id (cf. point ouvert du cadrage), on prend la plus récente.
    private const string ResolveJobIdSql = """
        SELECT t.job_id
        FROM public.data_task t
        WHERE t.external_id = @ExternalId
        ORDER BY t.id DESC
        LIMIT 1
        """;

    private readonly ConcurrentDictionary<(string Env, long ExternalId), long> _jobIdCache = new();
    private readonly int _cacheSize;

    public MonitoringRepository(IOptions<MonitoringOptions> options)
    {
        _cacheSize = Math.Max(0, options.Value.TaskJobCacheSize);
    }

    public async Task<EnvironmentSnapshot> GetSnapshotAsync(
        MonitoringEnvironment environment,
        DateTimeOffset from,
        DateTimeOffset to,
        int bucketSeconds,
        int latestJobsLimit,
        CancellationToken ct)
    {
        var range = new
        {
            From = from.UtcDateTime,
            To = to.UtcDateTime,
            BucketSeconds = (double)bucketSeconds,
            Limit = latestJobsLimit
        };

        await using var connection = await OpenAsync(environment, ct);

        var stateCounts = await connection.QueryAsync<StateCountRow>(
            new CommandDefinition(StateCountsSql, range, cancellationToken: ct));
        var buckets = await connection.QueryAsync<BucketRow>(
            new CommandDefinition(BucketsSql, range, cancellationToken: ct));
        var jobs = await connection.QueryAsync<JobRow>(
            new CommandDefinition(LatestJobsSql, range, cancellationToken: ct));

        return new EnvironmentSnapshot(
            environment.Code,
            stateCounts.ToDictionary(row => row.State, row => row.Count, StringComparer.Ordinal),
            buckets.Select(row => new BucketCount(row.State, row.BucketIndex, row.Count)).ToList(),
            jobs.Select(row => row.ToDto(environment.Code)).ToList());
    }

    public async Task<long?> ResolveJobIdAsync(
        MonitoringEnvironment environment,
        long externalId,
        CancellationToken ct)
    {
        var key = (environment.Code, externalId);
        if (_jobIdCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await using var connection = await OpenAsync(environment, ct);
        var jobId = await connection.QuerySingleOrDefaultAsync<long?>(
            new CommandDefinition(ResolveJobIdSql, new { ExternalId = externalId }, cancellationToken: ct));

        if (jobId is not null)
        {
            Remember(key, jobId.Value);
        }

        return jobId;
    }

    private void Remember((string Env, long ExternalId) key, long jobId)
    {
        if (_cacheSize == 0)
        {
            return;
        }

        // Éviction volontairement simple : on vide le cache quand il est plein.
        if (_jobIdCache.Count >= _cacheSize)
        {
            _jobIdCache.Clear();
        }

        _jobIdCache[key] = jobId;
    }

    private static async Task<NpgsqlConnection> OpenAsync(MonitoringEnvironment environment, CancellationToken ct)
    {
        if (!environment.IsConfigured)
        {
            throw new InvalidOperationException($"Environnement {environment.Code} non configuré.");
        }

        var connection = new NpgsqlConnection(environment.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    // ── Lignes Dapper ────────────────────────────────────────────────────────

    private sealed class StateCountRow
    {
        public string State { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    private sealed class BucketRow
    {
        public string State { get; set; } = string.Empty;
        public int BucketIndex { get; set; }
        public int Count { get; set; }
    }

    private sealed class JobRow
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string State { get; set; } = string.Empty;
        public int Progress { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public long TaskCount { get; set; }

        public JobRowDto ToDto(string env) => new(
            env,
            Id,
            Name,
            State,
            Progress,
            TaskCount,
            UpdatedAt is null
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt.Value, DateTimeKind.Utc)));
    }
}

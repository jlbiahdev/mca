using Dapper;
using FeteadApi.Domain.Interfaces.Monitoring;
using FeteadApi.Domain.Models.Monitoring;
using Npgsql;

namespace FeteadApi.Infrastructure.Repositories.Monitoring;

/// <summary>
/// Requêtes SQL du module, en lecture seule, sur public.data_job et public.data_task.
/// Chaque méthode reçoit la chaîne de connexion de l'environnement à interroger :
/// SqlConnectionFactory ne peut pas servir ici, elle ne connaît que DefaultConnection.
/// </summary>
public class MonitoringRepository : IMonitoringRepository
{
    // data_job n'a pas de colonne "updated_at" : la date d'activité d'un job
    // est la plus récente des dates connues.
    private const string ActivityDate =
        "COALESCE(j.change_date_time, j.start_date_time, j.submit_date_time, j.create_date_time)";

    /// <summary>Nombre de jobs par état, pour les KPI.</summary>
    private const string StateCountsSql = $@"
        SELECT j.state AS State, COUNT(*)::int AS Count
        FROM public.data_job j
        WHERE {ActivityDate} BETWEEN @From AND @To
        GROUP BY j.state";

    /// <summary>
    /// Graphique : pour chaque intervalle, combien de jobs ont été soumis, démarrés,
    /// ou terminés (Finished / Failed / Canceled). Reconstitué à partir des dates de data_job.
    /// BucketIndex = numéro de l'intervalle (0 = premier intervalle de la plage).
    /// </summary>
    private const string BucketsSql = @"
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
        GROUP BY e.state, BucketIndex";

    /// <summary>Derniers jobs modifiés, avec leur nombre de tâches.</summary>
    private const string LatestJobsSql = $@"
        SELECT j.id AS Id,
               j.name AS Name,
               j.state AS State,
               j.progress AS Progress,
               {ActivityDate} AS UpdatedAt,
               (SELECT COUNT(*) FROM public.data_task t WHERE t.job_id = j.id) AS TaskCount
        FROM public.data_job j
        WHERE {ActivityDate} BETWEEN @From AND @To
        ORDER BY UpdatedAt DESC, j.id DESC
        LIMIT @Limit";

    /// <summary>
    /// Retrouve le job d'une tâche TaskFlow/Scheduler grâce à data_task.external_id.
    /// Si plusieurs data_task ont le même external_id, on prend la plus récente.
    /// </summary>
    private const string FindJobIdSql = @"
        SELECT t.job_id
        FROM public.data_task t
        WHERE t.external_id = @ExternalId
        ORDER BY t.id DESC
        LIMIT 1";

    public async Task<EnvironmentSnapshot> GetSnapshotAsync(
        string connectionString, DateTime from, DateTime to, int bucketSeconds, int jobsLimit)
    {
        var parameters = new
        {
            From = from,
            To = to,
            BucketSeconds = (double)bucketSeconds,
            Limit = jobsLimit
        };

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var snapshot = new EnvironmentSnapshot
        {
            StateCounts = (await connection.QueryAsync<StateCount>(StateCountsSql, parameters)).ToList(),
            Buckets = (await connection.QueryAsync<BucketCount>(BucketsSql, parameters)).ToList(),
            Jobs = (await connection.QueryAsync<JobRow>(LatestJobsSql, parameters)).ToList()
        };

        return snapshot;
    }

    public async Task<long?> FindJobIdByExternalIdAsync(string connectionString, long externalId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<long?>(FindJobIdSql, new { ExternalId = externalId });
    }
}

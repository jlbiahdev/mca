using FeteadApi.Domain.Interfaces.Monitoring;
using FeteadApi.Domain.Models.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FeteadApi.Infrastructure.Services.Monitoring;

/// <summary>
/// Construit l'état affiché par la page (KPI, graphique, tableau) pour une période
/// et une liste d'environnements. Un environnement en erreur n'empêche pas les autres.
/// </summary>
public class MonitoringSnapshotService : IMonitoringSnapshotService
{
    /// <summary>États suivis par les KPI et le graphique (spec Module 08).</summary>
    public static readonly string[] TrackedStates = { "Submitted", "Running", "Finished", "Failed", "Canceled" };

    private readonly MonitoringEnvironments _environments;
    private readonly IMonitoringRepository _repository;
    private readonly MonitoringOptions _options;
    private readonly ILogger<MonitoringSnapshotService> _logger;

    public MonitoringSnapshotService(
        MonitoringEnvironments environments,
        IMonitoringRepository repository,
        IOptions<MonitoringOptions> options,
        ILogger<MonitoringSnapshotService> logger)
    {
        _environments = environments;
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<MonitoringSnapshot> GetSnapshotAsync(List<string> environmentCodes, DateTime from, DateTime to, JobFilter filter)
    {
        // Heure prise AVANT les requêtes : le navigateur rejouera les événements reçus après.
        var generatedAt = DateTime.UtcNow;
        var bucketSeconds = ChooseBucketSeconds(from, to);

        var results = new List<EnvironmentSnapshot>();
        var errors = new List<EnvironmentError>();

        foreach (var code in environmentCodes)
        {
            var environment = _environments.Find(code);
            if (environment == null || !environment.IsConfigured)
            {
                errors.Add(new EnvironmentError { Env = code, Message = "Environnement indisponible" });
                continue;
            }

            try
            {
                var result = await _repository.GetSnapshotAsync(
                    environment.ConnectionString!, from, to, bucketSeconds, _options.LatestJobsLimit, filter);

                // Chaque ligne de job doit savoir de quel environnement elle vient.
                foreach (var job in result.Jobs)
                    job.Env = environment.Code;

                results.Add(result);
            }
            catch (Exception ex)
            {
                // Le détail reste dans les logs : il peut contenir des informations de connexion.
                _logger.LogError(ex, "[Monitoring:{Env}] Snapshot impossible", environment.Code);
                errors.Add(new EnvironmentError { Env = environment.Code, Message = "Environnement indisponible" });
            }
        }

        var snapshot = Merge(results, from, to, bucketSeconds, _options.LatestJobsLimit);
        snapshot.GeneratedAt = generatedAt;
        snapshot.Errors = errors;
        return snapshot;
    }

    public async Task<List<TaskRow>> GetJobTasksAsync(string environmentCode, long jobId)
    {
        var environment = _environments.Find(environmentCode);
        if (environment == null || !environment.IsConfigured)
            throw new InvalidOperationException($"Environnement {environmentCode} indisponible.");

        return await _repository.GetJobTasksAsync(environment.ConnectionString!, jobId, _options.TaskListLimit);
    }

    /// <summary>
    /// Taille d'un intervalle du graphique, selon la longueur de la période.
    /// Objectif : environ 50 points sur la courbe, quelle que soit la période.
    /// </summary>
    public static int ChooseBucketSeconds(DateTime from, DateTime to)
    {
        var duration = to - from;

        if (duration <= TimeSpan.FromHours(1)) return 60;            // 1 minute
        if (duration <= TimeSpan.FromHours(6)) return 5 * 60;        // 5 minutes
        if (duration <= TimeSpan.FromHours(24)) return 30 * 60;      // 30 minutes
        if (duration <= TimeSpan.FromDays(7)) return 3 * 3600;       // 3 heures

        // Au-delà de 7 jours : 50 intervalles, arrondis à l'heure supérieure.
        var hours = (int)Math.Ceiling(duration.TotalHours / 50);
        return hours * 3600;
    }

    /// <summary>Regroupe les résultats de plusieurs environnements.</summary>
    public static MonitoringSnapshot Merge(
        List<EnvironmentSnapshot> results, DateTime from, DateTime to, int bucketSeconds, int jobsLimit)
    {
        var bucketCount = (int)Math.Ceiling((to - from).TotalSeconds / bucketSeconds);
        if (bucketCount < 1)
            bucketCount = 1;

        var snapshot = new MonitoringSnapshot { From = from, To = to, BucketSeconds = bucketSeconds };

        // On prépare un compteur à 0 et une série vide pour chaque état suivi.
        foreach (var state in TrackedStates)
        {
            snapshot.Kpi[state] = 0;
            snapshot.Series.Add(new StateSeries { State = state, Counts = new int[bucketCount] });
        }

        foreach (var result in results)
        {
            // KPI : les états non suivis (Queued, Validating…) sont ignorés.
            foreach (var stateCount in result.StateCounts)
            {
                if (snapshot.Kpi.ContainsKey(stateCount.State))
                    snapshot.Kpi[stateCount.State] += stateCount.Count;
            }

            // Graphique
            foreach (var bucket in result.Buckets)
            {
                var series = snapshot.Series.FirstOrDefault(s => s.State == bucket.State);
                if (series == null)
                    continue;

                // Un événement exactement à la fin de la période tombe dans le dernier intervalle.
                var index = Math.Min(Math.Max(bucket.BucketIndex, 0), bucketCount - 1);
                series.Counts[index] += bucket.Count;
            }

            snapshot.Jobs.AddRange(result.Jobs);
        }

        // Tableau : les jobs les plus récemment modifiés d'abord, tous environnements confondus.
        snapshot.Jobs = snapshot.Jobs
            .OrderByDescending(job => job.UpdatedAt)
            .Take(jobsLimit)
            .ToList();

        return snapshot;
    }
}

using FeteadApi.Domain.Models.Monitoring;

namespace FeteadApi.Domain.Interfaces.Monitoring;

/// <summary>Requêtes SQL du module STYX Monitoring (lecture seule).</summary>
public interface IMonitoringRepository
{
    /// <summary>KPI, graphique et derniers jobs d'UN environnement, sur une période.</summary>
    Task<EnvironmentSnapshot> GetSnapshotAsync(string connectionString, DateTime from, DateTime to, int bucketSeconds, int jobsLimit);

    /// <summary>Job d'une tâche TaskFlow/Scheduler, retrouvé via data_task.external_id. Null si introuvable.</summary>
    Task<long?> FindJobIdByExternalIdAsync(string connectionString, long externalId);
}

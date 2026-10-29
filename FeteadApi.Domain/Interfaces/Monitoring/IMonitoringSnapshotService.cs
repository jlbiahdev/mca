using FeteadApi.Domain.Models.Monitoring;

namespace FeteadApi.Domain.Interfaces.Monitoring;

/// <summary>Construit l'état affiché par la page STYX Monitoring.</summary>
public interface IMonitoringSnapshotService
{
    /// <summary>
    /// État de la période pour les environnements demandés (codes : "LOCAL", "POC"…).
    /// Les codes doivent avoir été vérifiés avant l'appel.
    /// </summary>
    Task<MonitoringSnapshot> GetSnapshotAsync(List<string> environmentCodes, DateTime from, DateTime to, JobFilter filter);

    /// <summary>
    /// Tâches d'un job. Lève une exception si l'environnement est indisponible
    /// ou si la base ne répond pas.
    /// </summary>
    Task<List<TaskRow>> GetJobTasksAsync(string environmentCode, long jobId);
}

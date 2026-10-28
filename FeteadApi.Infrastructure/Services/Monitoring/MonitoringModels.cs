namespace FeteadApi.Infrastructure.Services.Monitoring;

// ═════════════════════════════════════════════════════════════════════════
// Classes de données du module STYX Monitoring (aucune logique ici).
// SignalR et les contrôleurs les envoient en JSON en camelCase :
// la propriété JobId devient "jobId" côté navigateur.
// ═════════════════════════════════════════════════════════════════════════

/// <summary>
/// Contenu d'un NOTIFY PostgreSQL. Une seule classe pour les deux canaux :
/// - scheduler_events : Kind = "job" ou "task"
/// - taskflow_events  : pas de Kind (ce sont toujours des tâches)
/// Les champs absents du message restent à null.
/// </summary>
public class MonitoringNotification
{
    public string? Kind { get; set; }
    public long? JobId { get; set; }
    public string? Name { get; set; }
    public string? State { get; set; }
    public string? PreviousState { get; set; }
    public int? Progress { get; set; }
    public long? TaskId { get; set; }
    public long? ExternalId { get; set; }
    public string? Reason { get; set; }
    public string? At { get; set; }
}

/// <summary>Message SignalR "JobChanged" : un data_job a changé d'état ou de progression.</summary>
public class JobChangedMessage
{
    public string Env { get; set; } = "";
    public long JobId { get; set; }
    public string? Name { get; set; }
    public string State { get; set; } = "";
    public string? PreviousState { get; set; }
    public int? Progress { get; set; }
    public string? At { get; set; }
    public DateTime ReceivedAt { get; set; }
}

/// <summary>Message SignalR "TaskChanged" : une tâche TaskFlow ou Scheduler a changé d'état.</summary>
public class TaskChangedMessage
{
    public string Env { get; set; } = "";
    public string Source { get; set; } = "";   // "taskflow" ou "scheduler"
    public long TaskId { get; set; }
    public long? ExternalId { get; set; }
    public long? JobId { get; set; }           // null si la tâche n'a pas été retrouvée
    public string State { get; set; } = "";
    public string? Reason { get; set; }
    public string? At { get; set; }
    public DateTime ReceivedAt { get; set; }
}

/// <summary>Message SignalR "EnvironmentStatusChanged", aussi renvoyé par GET /monitoring/environments.</summary>
public class EnvironmentStatusMessage
{
    public string Env { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime Since { get; set; }
}

/// <summary>Réponse de GET /monitoring/snapshot.</summary>
public class MonitoringSnapshot
{
    public DateTime GeneratedAt { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int BucketSeconds { get; set; }

    /// <summary>Nombre de jobs par état suivi, ex. { "Running": 5, "Failed": 2 }.</summary>
    public Dictionary<string, int> Kpi { get; set; } = new();

    /// <summary>Une série par état suivi, pour le graphique.</summary>
    public List<StateSeries> Series { get; set; } = new();

    public List<JobRow> Jobs { get; set; } = new();

    /// <summary>Environnements qui n'ont pas pu répondre.</summary>
    public List<EnvironmentError> Errors { get; set; } = new();
}

public class StateSeries
{
    public string State { get; set; } = "";

    /// <summary>Un nombre par intervalle de temps.</summary>
    public int[] Counts { get; set; } = Array.Empty<int>();
}

/// <summary>Une ligne du tableau DataJobs.</summary>
public class JobRow
{
    public string Env { get; set; } = "";
    public long Id { get; set; }
    public string? Name { get; set; }
    public string State { get; set; } = "";
    public int Progress { get; set; }
    public long TaskCount { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class EnvironmentError
{
    public string Env { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>Résultat brut d'UN environnement, avant regroupement.</summary>
public class EnvironmentSnapshot
{
    public List<StateCount> StateCounts { get; set; } = new();
    public List<BucketCount> Buckets { get; set; } = new();
    public List<JobRow> Jobs { get; set; } = new();
}

public class StateCount
{
    public string State { get; set; } = "";
    public int Count { get; set; }
}

public class BucketCount
{
    public string State { get; set; } = "";
    public int BucketIndex { get; set; }
    public int Count { get; set; }
}

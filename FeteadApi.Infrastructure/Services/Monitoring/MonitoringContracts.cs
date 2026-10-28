using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FeteadApi.Infrastructure.Services.Monitoring;

/// <summary>États de data_job suivis par les KPI et le graphique (spec Module 08).</summary>
public static class MonitoringStates
{
    public const string Submitted = "Submitted";
    public const string Running = "Running";
    public const string Finished = "Finished";
    public const string Failed = "Failed";
    public const string Canceled = "Canceled";

    public static readonly IReadOnlyList<string> Tracked =
        new[] { Submitted, Running, Finished, Failed, Canceled };

    public static bool IsTracked(string? state) =>
        state is not null && Tracked.Contains(state, StringComparer.Ordinal);
}

/// <summary>Statut de la connexion LISTEN d'un environnement.</summary>
public enum EnvironmentStatus
{
    Connecting,
    Listening,
    Reconnecting,
    Unavailable
}

// ── Événements diffusés au navigateur (SignalR) ──────────────────────────────
// "At" est conservé tel que reçu de PostgreSQL : les tâches utilisent un TIMESTAMP
// sans fuseau, on ne devine donc pas d'offset. L'affichage utilise ReceivedAt.

public sealed record JobChangedEvent(
    string Env,
    long JobId,
    string? Name,
    string State,
    string? PreviousState,
    int? Progress,
    string? At,
    DateTimeOffset ReceivedAt);

public sealed record TaskChangedEvent(
    string Env,
    string Source,
    long TaskId,
    long? ExternalId,
    long? JobId,
    string State,
    string? Reason,
    string? At,
    DateTimeOffset ReceivedAt);

public sealed record EnvironmentStatusChangedEvent(
    string Env,
    string Status,
    DateTimeOffset Since);

// ── Snapshot (REST) ──────────────────────────────────────────────────────────

public sealed record MonitoringSnapshot(
    DateTimeOffset GeneratedAt,
    DateTimeOffset From,
    DateTimeOffset To,
    int BucketSeconds,
    IReadOnlyDictionary<string, int> Kpi,
    IReadOnlyList<SeriesDto> Series,
    IReadOnlyList<JobRowDto> Jobs,
    IReadOnlyList<EnvironmentErrorDto> Errors);

public sealed record SeriesDto(string State, IReadOnlyList<int> Counts);

public sealed record JobRowDto(
    string Env,
    long Id,
    string? Name,
    string State,
    int Progress,
    long TaskCount,
    DateTimeOffset? UpdatedAt);

public sealed record EnvironmentErrorDto(string Env, string Message);

/// <summary>Résultat brut d'un environnement, avant fusion.</summary>
public sealed record EnvironmentSnapshot(
    string Env,
    IReadOnlyDictionary<string, int> StateCounts,
    IReadOnlyList<BucketCount> Buckets,
    IReadOnlyList<JobRowDto> Jobs);

public sealed record BucketCount(string State, int BucketIndex, int Count);

// ── Ports ────────────────────────────────────────────────────────────────────

/// <summary>Diffusion des événements vers les navigateurs (implémenté côté Presentation).</summary>
public interface IMonitoringPublisher
{
    Task PublishJobChangedAsync(JobChangedEvent evt, CancellationToken ct);
    Task PublishTaskChangedAsync(TaskChangedEvent evt, CancellationToken ct);
    Task PublishEnvironmentStatusChangedAsync(EnvironmentStatusChangedEvent evt, CancellationToken ct);
}

/// <summary>Retrouve le data_job d'une tâche TaskFlow/Scheduler via data_task.external_id.</summary>
public interface ITaskJobResolver
{
    Task<long?> ResolveJobIdAsync(MonitoringEnvironment environment, long externalId, CancellationToken ct);
}

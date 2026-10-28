using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FeteadApi.Infrastructure.Services.Monitoring;

/// <summary>
/// Reconstitue la vision métier à partir des notifications PostgreSQL :
/// associe les tâches TaskFlow/Scheduler à leur data_job et diffuse les événements.
/// </summary>
public sealed class MonitoringAggregator
{
    private readonly ITaskJobResolver _taskJobResolver;
    private readonly IMonitoringPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MonitoringAggregator> _logger;

    public MonitoringAggregator(
        ITaskJobResolver taskJobResolver,
        IMonitoringPublisher publisher,
        TimeProvider timeProvider,
        ILogger<MonitoringAggregator> logger)
    {
        _taskJobResolver = taskJobResolver;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(
        MonitoringEnvironment environment,
        string channel,
        string? payload,
        CancellationToken ct)
    {
        var receivedAt = _timeProvider.GetUtcNow();

        switch (MonitoringNotificationParser.Parse(channel, payload))
        {
            case JobNotification job:
                await _publisher.PublishJobChangedAsync(ToEvent(environment, job, receivedAt), ct);
                break;

            case TaskNotification task:
                var jobId = await ResolveJobIdAsync(environment, task, ct);
                await _publisher.PublishTaskChangedAsync(ToEvent(environment, task, jobId, receivedAt), ct);
                break;

            default:
                _logger.LogWarning(
                    "[Monitoring:{Env}] Notification ignorée sur {Channel} : payload illisible ou non suivi",
                    environment.Code,
                    channel);
                break;
        }
    }

    private async Task<long?> ResolveJobIdAsync(
        MonitoringEnvironment environment,
        TaskNotification task,
        CancellationToken ct)
    {
        if (task.ExternalId is null)
        {
            return null;
        }

        try
        {
            var jobId = await _taskJobResolver.ResolveJobIdAsync(environment, task.ExternalId.Value, ct);
            if (jobId is null)
            {
                _logger.LogWarning(
                    "[Monitoring:{Env}] Aucun data_task pour external_id={ExternalId} ({Source} task {TaskId})",
                    environment.Code,
                    task.ExternalId,
                    task.Source,
                    task.TaskId);
            }

            return jobId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // L'événement reste diffusé, sans job associé, plutôt que perdu.
            _logger.LogWarning(
                ex,
                "[Monitoring:{Env}] Résolution du job impossible pour external_id={ExternalId}",
                environment.Code,
                task.ExternalId);
            return null;
        }
    }

    private static JobChangedEvent ToEvent(
        MonitoringEnvironment environment,
        JobNotification job,
        DateTimeOffset receivedAt) =>
        new(environment.Code, job.JobId, job.Name, job.State, job.PreviousState, job.Progress, job.At, receivedAt);

    private static TaskChangedEvent ToEvent(
        MonitoringEnvironment environment,
        TaskNotification task,
        long? jobId,
        DateTimeOffset receivedAt) =>
        new(environment.Code, task.Source, task.TaskId, task.ExternalId, jobId, task.State, task.Reason, task.At, receivedAt);
}

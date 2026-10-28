using System.Text.Json;
using FeteadApi.Infrastructure.Services.Monitoring;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace FeteadApi.Presentation.Monitoring;

/// <summary>
/// Service de fond : écoute PostgreSQL (LISTEN) sur chaque environnement
/// et renvoie chaque notification aux navigateurs via SignalR.
/// Même principe que TaskFlowNotificationListener : si la connexion tombe,
/// on attend quelques secondes et on se reconnecte.
/// </summary>
public class MonitoringListenerService : BackgroundService
{
    private const string SchedulerChannel = "scheduler_events";
    private const string TaskFlowChannel = "taskflow_events";
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly MonitoringEnvironments _environments;
    private readonly MonitoringRepository _repository;
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly ILogger<MonitoringListenerService> _logger;

    public MonitoringListenerService(
        MonitoringEnvironments environments,
        MonitoringRepository repository,
        IHubContext<MonitoringHub> hub,
        ILogger<MonitoringListenerService> logger)
    {
        _environments = environments;
        _repository = repository;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Une boucle d'écoute par environnement, toutes en parallèle.
        var loops = new List<Task>();

        foreach (var environment in _environments.All)
        {
            if (!environment.IsConfigured)
            {
                _logger.LogWarning("[Monitoring:{Env}] Variable de connexion absente : environnement non supervisé", environment.Code);
                continue;
            }

            loops.Add(ListenForeverAsync(environment, stoppingToken));
        }

        await Task.WhenAll(loops);
    }

    private async Task ListenForeverAsync(MonitoringEnvironment environment, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(environment, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // arrêt normal de l'application
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Monitoring:{Env}] Écoute perdue, nouvelle tentative dans 5 s", environment.Code);
            }

            await ChangeStatusAsync(environment, EnvironmentStatus.Reconnecting);

            try
            {
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ListenAsync(MonitoringEnvironment environment, CancellationToken stoppingToken)
    {
        await using var connection = new NpgsqlConnection(environment.ConnectionString);
        await connection.OpenAsync(stoppingToken);

        // Appelé par Npgsql à chaque NOTIFY reçu.
        connection.Notification += (sender, e) =>
        {
            _ = HandleNotificationAsync(environment, e.Channel, e.Payload);
        };

        await using (var command = new NpgsqlCommand($"LISTEN {SchedulerChannel}; LISTEN {TaskFlowChannel};", connection))
        {
            await command.ExecuteNonQueryAsync(stoppingToken);
        }

        _logger.LogInformation("[Monitoring:{Env}] Écoute active", environment.Code);

        // Le navigateur recharge ses données quand il reçoit "Listening" :
        // il récupère ainsi ce qui s'est passé pendant une éventuelle coupure.
        await ChangeStatusAsync(environment, EnvironmentStatus.Listening);

        // WaitAsync rend la main à chaque notification ; on boucle jusqu'à l'arrêt.
        while (!stoppingToken.IsCancellationRequested)
        {
            await connection.WaitAsync(stoppingToken);
        }
    }

    private async Task HandleNotificationAsync(MonitoringEnvironment environment, string channel, string payload)
    {
        try
        {
            var notification = JsonSerializer.Deserialize<MonitoringNotification>(payload, JsonOptions);
            if (notification == null || string.IsNullOrEmpty(notification.State))
            {
                _logger.LogWarning("[Monitoring:{Env}] Notification ignorée sur {Channel} : {Payload}", environment.Code, channel, payload);
                return;
            }

            if (channel == SchedulerChannel && notification.Kind == "job")
            {
                await SendJobChangedAsync(environment, notification);
            }
            else if (channel == TaskFlowChannel)
            {
                await SendTaskChangedAsync(environment, notification, "taskflow");
            }
            else if (channel == SchedulerChannel && notification.Kind == "task")
            {
                await SendTaskChangedAsync(environment, notification, "scheduler");
            }
        }
        catch (Exception ex)
        {
            // Une notification en erreur ne doit jamais arrêter l'écoute.
            _logger.LogError(ex, "[Monitoring:{Env}] Erreur sur une notification {Channel}", environment.Code, channel);
        }
    }

    private async Task SendJobChangedAsync(MonitoringEnvironment environment, MonitoringNotification notification)
    {
        if (notification.JobId == null)
            return;

        var message = new JobChangedMessage
        {
            Env = environment.Code,
            JobId = notification.JobId.Value,
            Name = notification.Name,
            State = notification.State!,
            PreviousState = notification.PreviousState,
            Progress = notification.Progress,
            At = notification.At,
            ReceivedAt = DateTime.UtcNow
        };

        await _hub.Clients.All.SendAsync("JobChanged", message);
    }

    private async Task SendTaskChangedAsync(MonitoringEnvironment environment, MonitoringNotification notification, string source)
    {
        if (notification.TaskId == null)
            return;

        // La notification ne contient pas le job : on le retrouve via data_task.external_id.
        long? jobId = null;
        if (notification.ExternalId != null)
        {
            jobId = await _repository.FindJobIdByExternalIdAsync(environment.ConnectionString!, notification.ExternalId.Value);
        }

        var message = new TaskChangedMessage
        {
            Env = environment.Code,
            Source = source,
            TaskId = notification.TaskId.Value,
            ExternalId = notification.ExternalId,
            JobId = jobId,
            State = notification.State!,
            Reason = notification.Reason,
            At = notification.At,
            ReceivedAt = DateTime.UtcNow
        };

        await _hub.Clients.All.SendAsync("TaskChanged", message);
    }

    private async Task ChangeStatusAsync(MonitoringEnvironment environment, string status)
    {
        var changed = _environments.SetStatus(environment, status);
        if (!changed)
            return;

        try
        {
            await _hub.Clients.All.SendAsync("EnvironmentStatusChanged", MonitoringEnvironments.ToStatusMessage(environment));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Monitoring:{Env}] Envoi du statut impossible", environment.Code);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FeteadApi.Infrastructure.Services.Monitoring;

/// <summary>
/// Maintient une connexion LISTEN par environnement configuré
/// (scheduler_events, taskflow_events) et transmet les notifications à l'agrégateur.
/// Reprend le principe de TaskFlowNotificationListener : reconnexion après délai, jamais d'arrêt.
/// </summary>
public sealed class MonitoringBackgroundService : BackgroundService
{
    private readonly MonitoringEnvironmentRegistry _registry;
    private readonly MonitoringAggregator _aggregator;
    private readonly IMonitoringPublisher _publisher;
    private readonly TimeSpan _reconnectDelay;
    private readonly ILogger<MonitoringBackgroundService> _logger;

    // Npgsql lève l'événement Notification de façon synchrone : on met en file
    // pour traiter dans l'ordre d'arrivée, sans tâche lancée et oubliée.
    private readonly Channel<PendingNotification> _queue =
        Channel.CreateUnbounded<PendingNotification>(new UnboundedChannelOptions { SingleReader = true });

    public MonitoringBackgroundService(
        MonitoringEnvironmentRegistry registry,
        MonitoringAggregator aggregator,
        IMonitoringPublisher publisher,
        IOptions<MonitoringOptions> options,
        ILogger<MonitoringBackgroundService> logger)
    {
        _registry = registry;
        _aggregator = aggregator;
        _publisher = publisher;
        _reconnectDelay = TimeSpan.FromSeconds(Math.Max(1, options.Value.ReconnectDelaySeconds));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listeners = new List<Task> { ProcessQueueAsync(stoppingToken) };

        foreach (var environment in _registry.Environments)
        {
            if (!environment.IsConfigured)
            {
                _logger.LogWarning(
                    "[Monitoring:{Env}] Variable de connexion absente : environnement non supervisé",
                    environment.Code);
                await SetStatusAsync(environment, EnvironmentStatus.Unavailable, stoppingToken);
                continue;
            }

            listeners.Add(ListenWithRetryAsync(environment, stoppingToken));
        }

        await Task.WhenAll(listeners);
    }

    private async Task ListenWithRetryAsync(MonitoringEnvironment environment, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(environment, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[Monitoring:{Env}] Connexion LISTEN perdue, nouvelle tentative dans {Delay}s",
                    environment.Code,
                    _reconnectDelay.TotalSeconds);
            }

            await SetStatusAsync(environment, EnvironmentStatus.Reconnecting, ct);

            try
            {
                await Task.Delay(_reconnectDelay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ListenAsync(MonitoringEnvironment environment, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(environment.ConnectionString);
        await connection.OpenAsync(ct);

        connection.Notification += (_, args) =>
            _queue.Writer.TryWrite(new PendingNotification(environment, args.Channel, args.Payload));

        foreach (var channel in MonitoringNotificationParser.Channels)
        {
            await using var command = new NpgsqlCommand($"LISTEN {channel}", connection);
            await command.ExecuteNonQueryAsync(ct);
        }

        _logger.LogInformation("[Monitoring:{Env}] LISTEN actif", environment.Code);

        // Le client recharge son snapshot à chaque passage à Listening :
        // les NOTIFY émis pendant une coupure sont ainsi rattrapés.
        await SetStatusAsync(environment, EnvironmentStatus.Listening, ct);

        while (!ct.IsCancellationRequested)
        {
            await connection.WaitAsync(ct);
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var notification in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await _aggregator.HandleAsync(
                        notification.Environment, notification.Channel, notification.Payload, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "[Monitoring:{Env}] Échec du traitement d'une notification {Channel}",
                        notification.Environment.Code,
                        notification.Channel);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Arrêt normal du service.
        }
    }

    private async Task SetStatusAsync(MonitoringEnvironment environment, EnvironmentStatus status, CancellationToken ct)
    {
        var change = _registry.SetStatus(environment.Code, status);
        if (change is null)
        {
            return;
        }

        try
        {
            await _publisher.PublishEnvironmentStatusChangedAsync(change, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[Monitoring:{Env}] Diffusion du statut impossible", environment.Code);
        }
    }

    private sealed record PendingNotification(MonitoringEnvironment Environment, string Channel, string Payload);
}

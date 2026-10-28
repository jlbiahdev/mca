using FeteadApi.Infrastructure.Services.Monitoring;
using Microsoft.AspNetCore.SignalR;

namespace FeteadApi.Presentation.Monitoring;

/// <summary>Implémentation SignalR du port de diffusion défini dans Infrastructure.</summary>
public sealed class SignalRMonitoringPublisher : IMonitoringPublisher
{
    private readonly IHubContext<MonitoringHub> _hub;

    public SignalRMonitoringPublisher(IHubContext<MonitoringHub> hub)
    {
        _hub = hub;
    }

    public Task PublishJobChangedAsync(JobChangedEvent evt, CancellationToken ct) =>
        _hub.Clients.All.SendAsync(MonitoringHub.JobChanged, evt, ct);

    public Task PublishTaskChangedAsync(TaskChangedEvent evt, CancellationToken ct) =>
        _hub.Clients.All.SendAsync(MonitoringHub.TaskChanged, evt, ct);

    public Task PublishEnvironmentStatusChangedAsync(EnvironmentStatusChangedEvent evt, CancellationToken ct) =>
        _hub.Clients.All.SendAsync(MonitoringHub.EnvironmentStatusChanged, evt, ct);
}

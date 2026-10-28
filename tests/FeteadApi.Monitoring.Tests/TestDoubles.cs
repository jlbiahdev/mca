using FeteadApi.Infrastructure.Services.Monitoring;

namespace FeteadApi.Monitoring.Tests;

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class RecordingPublisher : IMonitoringPublisher
{
    public List<JobChangedEvent> Jobs { get; } = new();
    public List<TaskChangedEvent> Tasks { get; } = new();
    public List<EnvironmentStatusChangedEvent> Statuses { get; } = new();

    public Task PublishJobChangedAsync(JobChangedEvent evt, CancellationToken ct)
    {
        Jobs.Add(evt);
        return Task.CompletedTask;
    }

    public Task PublishTaskChangedAsync(TaskChangedEvent evt, CancellationToken ct)
    {
        Tasks.Add(evt);
        return Task.CompletedTask;
    }

    public Task PublishEnvironmentStatusChangedAsync(EnvironmentStatusChangedEvent evt, CancellationToken ct)
    {
        Statuses.Add(evt);
        return Task.CompletedTask;
    }
}

internal sealed class StubTaskJobResolver : ITaskJobResolver
{
    private readonly Func<long, long?> _resolve;

    public StubTaskJobResolver(Func<long, long?> resolve) => _resolve = resolve;

    public List<long> Requested { get; } = new();

    public Task<long?> ResolveJobIdAsync(MonitoringEnvironment environment, long externalId, CancellationToken ct)
    {
        Requested.Add(externalId);
        return Task.FromResult(_resolve(externalId));
    }
}

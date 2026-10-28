using FeteadApi.Infrastructure.Services.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeteadApi.Monitoring.Tests;

public class MonitoringAggregatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 15, 52, 14, TimeSpan.Zero);
    private static readonly MonitoringEnvironment Poc = new("POC", "Host=unused");

    private readonly RecordingPublisher _publisher = new();

    private MonitoringAggregator CreateAggregator(ITaskJobResolver resolver) =>
        new(resolver, _publisher, new FixedTimeProvider(Now), NullLogger<MonitoringAggregator>.Instance);

    [Fact]
    public async Task Job_notification_is_published_with_environment_and_reception_time()
    {
        var aggregator = CreateAggregator(new StubTaskJobResolver(_ => null));

        await aggregator.HandleAsync(Poc, "scheduler_events",
            """{"kind":"job","jobId":987,"state":"Failed","progress":42}""", CancellationToken.None);

        var evt = Assert.Single(_publisher.Jobs);
        Assert.Equal("POC", evt.Env);
        Assert.Equal(987, evt.JobId);
        Assert.Equal("Failed", evt.State);
        Assert.Equal(Now, evt.ReceivedAt);
    }

    [Fact]
    public async Task Task_notification_is_linked_to_its_data_job()
    {
        var resolver = new StubTaskJobResolver(externalId => externalId == 77 ? 987 : null);
        var aggregator = CreateAggregator(resolver);

        await aggregator.HandleAsync(Poc, "taskflow_events",
            """{"taskId":5,"externalId":77,"state":"Failed","reason":"exit 1"}""", CancellationToken.None);

        var evt = Assert.Single(_publisher.Tasks);
        Assert.Equal(987, evt.JobId);
        Assert.Equal("taskflow", evt.Source);
    }

    [Fact]
    public async Task Task_without_external_id_is_published_without_lookup()
    {
        var resolver = new StubTaskJobResolver(_ => 1);
        var aggregator = CreateAggregator(resolver);

        await aggregator.HandleAsync(Poc, "taskflow_events",
            """{"taskId":5,"state":"Running"}""", CancellationToken.None);

        Assert.Empty(resolver.Requested);
        Assert.Null(Assert.Single(_publisher.Tasks).JobId);
    }

    [Fact]
    public async Task Resolver_failure_still_publishes_the_task_without_job()
    {
        var aggregator = CreateAggregator(new StubTaskJobResolver(_ => throw new InvalidOperationException("db down")));

        await aggregator.HandleAsync(Poc, "scheduler_events",
            """{"kind":"task","taskId":5,"externalId":77,"state":"Failed"}""", CancellationToken.None);

        Assert.Null(Assert.Single(_publisher.Tasks).JobId);
    }

    [Fact]
    public async Task Unreadable_notification_publishes_nothing()
    {
        var aggregator = CreateAggregator(new StubTaskJobResolver(_ => null));

        await aggregator.HandleAsync(Poc, "scheduler_events", "{oops", CancellationToken.None);

        Assert.Empty(_publisher.Jobs);
        Assert.Empty(_publisher.Tasks);
    }
}

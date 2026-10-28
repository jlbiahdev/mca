using FeteadApi.Infrastructure.Services.Monitoring;

namespace FeteadApi.Monitoring.Tests;

public class MonitoringNotificationParserTests
{
    [Fact]
    public void Job_event_from_current_trigger_is_parsed_without_optional_fields()
    {
        const string payload = """{"kind":"job","jobId":1024,"state":"Running","progress":65,"at":"2026-10-28T16:52:14.1+01:00"}""";

        var result = MonitoringNotificationParser.Parse("scheduler_events", payload);

        var job = Assert.IsType<JobNotification>(result);
        Assert.Equal(1024, job.JobId);
        Assert.Equal("Running", job.State);
        Assert.Equal(65, job.Progress);
        Assert.Null(job.Name);
        Assert.Null(job.PreviousState);
    }

    [Fact]
    public void Job_event_from_extended_trigger_exposes_name_and_previous_state()
    {
        const string payload = """{"kind":"job","jobId":7,"name":"ALM_RUN","state":"Finished","previousState":"Running","progress":100}""";

        var job = Assert.IsType<JobNotification>(MonitoringNotificationParser.Parse("scheduler_events", payload));

        Assert.Equal("ALM_RUN", job.Name);
        Assert.Equal("Running", job.PreviousState);
    }

    [Fact]
    public void Scheduler_task_event_is_tagged_with_scheduler_source()
    {
        const string payload = """{"kind":"task","taskId":5,"externalId":77,"state":"Failed","runnerId":3,"reason":"exit 1","at":"2026-10-28T16:52:14"}""";

        var task = Assert.IsType<TaskNotification>(MonitoringNotificationParser.Parse("scheduler_events", payload));

        Assert.Equal("scheduler", task.Source);
        Assert.Equal(77, task.ExternalId);
        Assert.Equal("exit 1", task.Reason);
        Assert.Equal("2026-10-28T16:52:14", task.At);
    }

    [Fact]
    public void Taskflow_event_is_tagged_with_taskflow_source()
    {
        const string payload = """{"taskId":9,"externalId":null,"state":"Running","serverId":"srv-1","reason":null}""";

        var task = Assert.IsType<TaskNotification>(MonitoringNotificationParser.Parse("taskflow_events", payload));

        Assert.Equal("taskflow", task.Source);
        Assert.Null(task.ExternalId);
    }

    [Theory]
    [InlineData("scheduler_events", "not json")]
    [InlineData("scheduler_events", "[]")]
    [InlineData("scheduler_events", """{"kind":"runner","id":1}""")]
    [InlineData("scheduler_events", """{"kind":"job","state":"Running"}""")]
    [InlineData("taskflow_events", """{"taskId":1}""")]
    [InlineData("unknown_channel", """{"taskId":1,"state":"Running"}""")]
    [InlineData("taskflow_events", "")]
    public void Unreadable_or_untracked_payload_returns_null(string channel, string payload)
    {
        Assert.Null(MonitoringNotificationParser.Parse(channel, payload));
    }
}

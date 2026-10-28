using System.Text.Json;
using FeteadApi.Domain.Models.Monitoring;

namespace FeteadApi.Monitoring.Tests;

/// <summary>
/// Vérifie que les messages réellement envoyés par les triggers PostgreSQL
/// se lisent correctement (mêmes options que MonitoringListenerService).
/// </summary>
public class MonitoringNotificationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static MonitoringNotification Read(string payload)
    {
        return JsonSerializer.Deserialize<MonitoringNotification>(payload, JsonOptions)!;
    }

    [Fact]
    public void Job_message_from_current_trigger_is_read()
    {
        var notification = Read("""{"kind":"job","jobId":1024,"state":"Running","progress":65,"at":"2026-10-28T16:52:14+01:00"}""");

        Assert.Equal("job", notification.Kind);
        Assert.Equal(1024, notification.JobId);
        Assert.Equal(65, notification.Progress);
        Assert.Null(notification.PreviousState);
    }

    [Fact]
    public void Job_message_from_extended_trigger_has_name_and_previous_state()
    {
        var notification = Read("""{"kind":"job","jobId":7,"name":"ALM_RUN","state":"Finished","previousState":"Running","progress":100}""");

        Assert.Equal("ALM_RUN", notification.Name);
        Assert.Equal("Running", notification.PreviousState);
    }

    [Fact]
    public void Scheduler_task_message_is_read()
    {
        var notification = Read("""{"kind":"task","taskId":5,"externalId":77,"state":"Failed","runnerId":3,"reason":"exit 1","at":"2026-10-28T16:52:14"}""");

        Assert.Equal("task", notification.Kind);
        Assert.Equal(77, notification.ExternalId);
        Assert.Equal("exit 1", notification.Reason);
    }

    [Fact]
    public void Taskflow_message_without_kind_is_read()
    {
        var notification = Read("""{"taskId":9,"externalId":null,"state":"Running","serverId":"srv-1","reason":null}""");

        Assert.Null(notification.Kind);
        Assert.Equal(9, notification.TaskId);
        Assert.Null(notification.ExternalId);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FeteadApi.Infrastructure.Services.Monitoring;

public abstract record MonitoringNotification;

public sealed record JobNotification(
    long JobId,
    string? Name,
    string State,
    string? PreviousState,
    int? Progress,
    string? At) : MonitoringNotification;

public sealed record TaskNotification(
    string Source,
    long TaskId,
    long? ExternalId,
    string State,
    string? Reason,
    string? At) : MonitoringNotification;

/// <summary>
/// Traduit les payloads NOTIFY en notifications typées. Tolérant aux champs
/// absents : "name" et "previousState" n'existent qu'après extension du trigger.
/// </summary>
public static class MonitoringNotificationParser
{
    public const string SchedulerChannel = "scheduler_events";
    public const string TaskFlowChannel = "taskflow_events";

    public const string SchedulerSource = "scheduler";
    public const string TaskFlowSource = "taskflow";

    public static readonly IReadOnlyList<string> Channels = new[] { SchedulerChannel, TaskFlowChannel };

    /// <summary>Renvoie null si le payload est illisible ou d'un type non suivi.</summary>
    public static MonitoringNotification? Parse(string channel, string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return channel switch
            {
                SchedulerChannel => ParseSchedulerEvent(root),
                TaskFlowChannel => ParseTask(root, TaskFlowSource),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MonitoringNotification? ParseSchedulerEvent(JsonElement root) =>
        GetString(root, "kind") switch
        {
            "job" => ParseJob(root),
            "task" => ParseTask(root, SchedulerSource),
            _ => null
        };

    private static JobNotification? ParseJob(JsonElement root)
    {
        var jobId = GetInt64(root, "jobId");
        var state = GetString(root, "state");
        if (jobId is null || string.IsNullOrEmpty(state))
        {
            return null;
        }

        var progress = GetInt64(root, "progress");
        return new JobNotification(
            jobId.Value,
            GetString(root, "name"),
            state,
            GetString(root, "previousState"),
            progress is null ? null : (int)progress.Value,
            GetString(root, "at"));
    }

    private static TaskNotification? ParseTask(JsonElement root, string source)
    {
        var taskId = GetInt64(root, "taskId");
        var state = GetString(root, "state");
        if (taskId is null || string.IsNullOrEmpty(state))
        {
            return null;
        }

        return new TaskNotification(
            source,
            taskId.Value,
            GetInt64(root, "externalId"),
            state,
            GetString(root, "reason"),
            GetString(root, "at"));
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static long? GetInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }
}

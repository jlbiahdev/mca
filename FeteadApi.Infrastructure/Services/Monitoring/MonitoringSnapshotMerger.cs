using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FeteadApi.Infrastructure.Services.Monitoring;

/// <summary>Fusionne les résultats de plusieurs environnements en un seul snapshot.</summary>
public static class MonitoringSnapshotMerger
{
    public static MonitoringSnapshot Merge(
        IReadOnlyCollection<EnvironmentSnapshot> environments,
        IReadOnlyList<EnvironmentErrorDto> errors,
        DateTimeOffset generatedAt,
        DateTimeOffset from,
        DateTimeOffset to,
        int bucketSeconds,
        int latestJobsLimit)
    {
        var bucketCount = MonitoringTimeBuckets.BucketCount(from, to, bucketSeconds);

        return new MonitoringSnapshot(
            generatedAt,
            from,
            to,
            bucketSeconds,
            MergeKpi(environments),
            MergeSeries(environments, bucketCount),
            MergeJobs(environments, latestJobsLimit),
            errors);
    }

    private static IReadOnlyDictionary<string, int> MergeKpi(IEnumerable<EnvironmentSnapshot> environments)
    {
        var kpi = MonitoringStates.Tracked.ToDictionary(state => state, _ => 0, StringComparer.Ordinal);

        foreach (var (state, count) in environments.SelectMany(e => e.StateCounts))
        {
            if (kpi.ContainsKey(state))
            {
                kpi[state] += count;
            }
        }

        return kpi;
    }

    private static IReadOnlyList<SeriesDto> MergeSeries(IEnumerable<EnvironmentSnapshot> environments, int bucketCount)
    {
        var series = MonitoringStates.Tracked.ToDictionary(
            state => state,
            _ => new int[bucketCount],
            StringComparer.Ordinal);

        foreach (var bucket in environments.SelectMany(e => e.Buckets))
        {
            // Bornes inclusives côté SQL : un événement exactement à "to" tombe dans le dernier intervalle.
            var index = Math.Clamp(bucket.BucketIndex, 0, bucketCount - 1);
            if (series.TryGetValue(bucket.State, out var counts))
            {
                counts[index] += bucket.Count;
            }
        }

        return MonitoringStates.Tracked
            .Select(state => new SeriesDto(state, series[state]))
            .ToList();
    }

    private static IReadOnlyList<JobRowDto> MergeJobs(IEnumerable<EnvironmentSnapshot> environments, int limit) =>
        environments
            .SelectMany(e => e.Jobs)
            .OrderByDescending(job => job.UpdatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(job => job.Env, StringComparer.Ordinal)
            .ThenByDescending(job => job.Id)
            .Take(Math.Max(0, limit))
            .ToList();
}

using FeteadApi.Infrastructure.Services.Monitoring;

namespace FeteadApi.Monitoring.Tests;

public class MonitoringTimeBucketsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 60)]          // 1 h  → 1 min (60 points)
    [InlineData(24, 1_800)]      // 24 h → 30 min (48 points)
    [InlineData(168, 10_800)]    // 7 j  → 3 h (56 points)
    [InlineData(24 * 90, 172_800)] // 90 j → 2 jours
    public void Bucket_is_the_smallest_step_giving_at_most_60_points(int hours, int expectedSeconds)
    {
        var bucket = MonitoringTimeBuckets.ChooseBucketSeconds(Start, Start.AddHours(hours));

        Assert.Equal(expectedSeconds, bucket);
        Assert.True(MonitoringTimeBuckets.BucketCount(Start, Start.AddHours(hours), bucket) <= MonitoringTimeBuckets.MaxPoints);
    }
}

public class MonitoringSnapshotMergerTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddMinutes(3);

    private static JobRowDto Job(string env, long id, int minute) =>
        new(env, id, $"JOB_{id}", "Running", 10, 3, From.AddMinutes(minute));

    private static EnvironmentSnapshot Env(string code, Dictionary<string, int> counts, BucketCount[] buckets, params JobRowDto[] jobs) =>
        new(code, counts, buckets, jobs);

    [Fact]
    public void Kpi_are_summed_across_environments_and_untracked_states_ignored()
    {
        var local = Env("LOCAL", new() { ["Running"] = 2, ["Queued"] = 5 }, Array.Empty<BucketCount>());
        var poc = Env("POC", new() { ["Running"] = 1, ["Failed"] = 4 }, Array.Empty<BucketCount>());

        var snapshot = MonitoringSnapshotMerger.Merge(new[] { local, poc }, Array.Empty<EnvironmentErrorDto>(), To, From, To, 60, 50);

        Assert.Equal(3, snapshot.Kpi["Running"]);
        Assert.Equal(4, snapshot.Kpi["Failed"]);
        Assert.Equal(0, snapshot.Kpi["Submitted"]);
        Assert.False(snapshot.Kpi.ContainsKey("Queued"));
    }

    [Fact]
    public void Series_have_one_value_per_bucket_and_clamp_the_upper_bound()
    {
        var local = Env("LOCAL", new(), new[]
        {
            new BucketCount("Submitted", 0, 2),
            new BucketCount("Submitted", 3, 1), // événement exactement à "to"
        });
        var poc = Env("POC", new(), new[] { new BucketCount("Submitted", 0, 1) });

        var snapshot = MonitoringSnapshotMerger.Merge(new[] { local, poc }, Array.Empty<EnvironmentErrorDto>(), To, From, To, 60, 50);

        var submitted = snapshot.Series.Single(s => s.State == "Submitted").Counts;
        Assert.Equal(new[] { 3, 0, 1 }, submitted);
        Assert.Equal(5, snapshot.Series.Count);
    }

    [Fact]
    public void Jobs_are_merged_by_most_recent_activity_and_limited()
    {
        var local = Env("LOCAL", new(), Array.Empty<BucketCount>(), Job("LOCAL", 1, 1), Job("LOCAL", 2, 3));
        var poc = Env("POC", new(), Array.Empty<BucketCount>(), Job("POC", 3, 2));

        var snapshot = MonitoringSnapshotMerger.Merge(new[] { local, poc }, Array.Empty<EnvironmentErrorDto>(), To, From, To, 60, 2);

        Assert.Equal(new long[] { 2, 3 }, snapshot.Jobs.Select(j => j.Id));
    }

    [Fact]
    public void Environment_errors_are_passed_through()
    {
        var errors = new[] { new EnvironmentErrorDto("INT", "Environnement indisponible") };

        var snapshot = MonitoringSnapshotMerger.Merge(Array.Empty<EnvironmentSnapshot>(), errors, To, From, To, 60, 50);

        Assert.Equal("INT", Assert.Single(snapshot.Errors).Env);
        Assert.Empty(snapshot.Jobs);
    }
}

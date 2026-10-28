using FeteadApi.Infrastructure.Services.Monitoring;

namespace FeteadApi.Monitoring.Tests;

public class MonitoringSnapshotServiceTests
{
    private static readonly DateTime Start = new(2026, 10, 28, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(1, 60)]          // 1 heure   → intervalles de 1 minute
    [InlineData(6, 300)]         // 6 heures  → 5 minutes
    [InlineData(24, 1800)]       // 24 heures → 30 minutes
    [InlineData(168, 10800)]     // 7 jours   → 3 heures
    [InlineData(720, 54000)]     // 30 jours  → 15 heures (50 intervalles)
    public void Bucket_size_depends_on_period_length(int hours, int expectedSeconds)
    {
        var bucketSeconds = MonitoringSnapshotService.ChooseBucketSeconds(Start, Start.AddHours(hours));

        Assert.Equal(expectedSeconds, bucketSeconds);
    }

    [Fact]
    public void Kpi_are_added_up_across_environments()
    {
        var local = new EnvironmentSnapshot { StateCounts = { new StateCount { State = "Running", Count = 2 } } };
        var poc = new EnvironmentSnapshot { StateCounts = { new StateCount { State = "Running", Count = 1 } } };

        var snapshot = MonitoringSnapshotService.Merge(new() { local, poc }, Start, Start.AddMinutes(3), 60, 50);

        Assert.Equal(3, snapshot.Kpi["Running"]);
        Assert.Equal(0, snapshot.Kpi["Failed"]);
    }

    [Fact]
    public void Untracked_states_are_not_counted()
    {
        var local = new EnvironmentSnapshot { StateCounts = { new StateCount { State = "Queued", Count = 5 } } };

        var snapshot = MonitoringSnapshotService.Merge(new() { local }, Start, Start.AddMinutes(3), 60, 50);

        Assert.False(snapshot.Kpi.ContainsKey("Queued"));
    }

    [Fact]
    public void Chart_has_one_value_per_interval()
    {
        var local = new EnvironmentSnapshot
        {
            Buckets =
            {
                new BucketCount { State = "Submitted", BucketIndex = 0, Count = 2 },
                new BucketCount { State = "Submitted", BucketIndex = 2, Count = 1 }
            }
        };

        var snapshot = MonitoringSnapshotService.Merge(new() { local }, Start, Start.AddMinutes(3), 60, 50);

        var submitted = snapshot.Series.Single(s => s.State == "Submitted");
        Assert.Equal(new[] { 2, 0, 1 }, submitted.Counts);
    }

    [Fact]
    public void Event_at_the_exact_end_of_the_period_goes_in_the_last_interval()
    {
        var local = new EnvironmentSnapshot
        {
            Buckets = { new BucketCount { State = "Failed", BucketIndex = 3, Count = 1 } }
        };

        var snapshot = MonitoringSnapshotService.Merge(new() { local }, Start, Start.AddMinutes(3), 60, 50);

        var failed = snapshot.Series.Single(s => s.State == "Failed");
        Assert.Equal(new[] { 0, 0, 1 }, failed.Counts);
    }

    [Fact]
    public void Most_recent_jobs_come_first_and_list_is_limited()
    {
        var local = new EnvironmentSnapshot
        {
            Jobs =
            {
                new JobRow { Env = "LOCAL", Id = 1, UpdatedAt = Start.AddMinutes(1) },
                new JobRow { Env = "LOCAL", Id = 2, UpdatedAt = Start.AddMinutes(3) }
            }
        };
        var poc = new EnvironmentSnapshot
        {
            Jobs = { new JobRow { Env = "POC", Id = 3, UpdatedAt = Start.AddMinutes(2) } }
        };

        var snapshot = MonitoringSnapshotService.Merge(new() { local, poc }, Start, Start.AddMinutes(3), 60, 2);

        Assert.Equal(new long[] { 2, 3 }, snapshot.Jobs.Select(j => j.Id));
    }
}

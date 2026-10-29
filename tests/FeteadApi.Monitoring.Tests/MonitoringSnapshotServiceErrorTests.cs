using FeteadApi.Domain.Interfaces.Monitoring;
using FeteadApi.Domain.Models.Monitoring;
using FeteadApi.Infrastructure.Services.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FeteadApi.Monitoring.Tests;

/// <summary>Un environnement en panne ne doit pas empêcher les autres de s'afficher.</summary>
public class MonitoringSnapshotServiceErrorTests
{
    /// <summary>Faux repository : répond pour "Host=ok", plante pour toute autre chaîne.</summary>
    private class FakeRepository : IMonitoringRepository
    {
        public Task<EnvironmentSnapshot> GetSnapshotAsync(string connectionString, DateTime from, DateTime to, int bucketSeconds, int jobsLimit, JobFilter filter)
        {
            if (connectionString != "Host=ok")
                throw new InvalidOperationException("base injoignable");

            var snapshot = new EnvironmentSnapshot
            {
                StateCounts = { new StateCount { State = "Running", Count = 4 } },
                Jobs = { new JobRow { Id = 1, State = "Running", UpdatedAt = from } }
            };
            return Task.FromResult(snapshot);
        }

        public Task<List<TaskRow>> GetJobTasksAsync(string connectionString, long jobId, int limit)
        {
            return Task.FromResult(new List<TaskRow> { new TaskRow { TaskNumber = 1, CommandLine = "run.exe --job " + jobId } });
        }

        public Task<long?> FindJobIdByExternalIdAsync(string connectionString, long externalId)
        {
            return Task.FromResult<long?>(null);
        }
    }

    private static MonitoringSnapshotService CreateService()
    {
        Environment.SetEnvironmentVariable("MonitoringTest_Ok", "Host=ok");
        Environment.SetEnvironmentVariable("MonitoringTest_Broken", "Host=broken");

        var options = Options.Create(new MonitoringOptions
        {
            Environments =
            {
                new MonitoringEnvironmentOptions { Code = "LOCAL", ConnectionStringVariable = "MonitoringTest_Ok" },
                new MonitoringEnvironmentOptions { Code = "POC", ConnectionStringVariable = "MonitoringTest_Broken" },
                new MonitoringEnvironmentOptions { Code = "INT", ConnectionStringVariable = "MonitoringTest_Missing" }
            }
        });

        return new MonitoringSnapshotService(
            new MonitoringEnvironments(options),
            new FakeRepository(),
            options,
            NullLogger<MonitoringSnapshotService>.Instance);
    }

    [Fact]
    public async Task Working_environment_is_shown_and_others_are_reported_as_errors()
    {
        var service = CreateService();
        var from = new DateTime(2026, 10, 28, 10, 0, 0, DateTimeKind.Utc);

        var snapshot = await service.GetSnapshotAsync(new List<string> { "LOCAL", "POC", "INT" }, from, from.AddHours(1), new JobFilter());

        Assert.Equal(4, snapshot.Kpi["Running"]);
        Assert.Equal("LOCAL", Assert.Single(snapshot.Jobs).Env);
        Assert.Equal(new[] { "POC", "INT" }, snapshot.Errors.Select(e => e.Env));
    }

    [Fact]
    public async Task Tasks_of_a_job_are_read_on_its_environment()
    {
        var service = CreateService();

        var tasks = await service.GetJobTasksAsync("LOCAL", 1024);

        Assert.Equal("run.exe --job 1024", Assert.Single(tasks).CommandLine);
    }

    [Fact]
    public async Task Tasks_of_an_unconfigured_environment_are_refused()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetJobTasksAsync("INT", 1));
    }
}

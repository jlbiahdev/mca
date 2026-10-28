using FeteadApi.Infrastructure.Services.Monitoring;
using Microsoft.Extensions.Options;

namespace FeteadApi.Monitoring.Tests;

public class MonitoringEnvironmentsTests
{
    private static IOptions<MonitoringOptions> OptionsWith(params (string Code, string Variable)[] environments)
    {
        var options = new MonitoringOptions();
        foreach (var (code, variable) in environments)
        {
            options.Environments.Add(new MonitoringEnvironmentOptions { Code = code, ConnectionStringVariable = variable });
        }
        return Options.Create(options);
    }

    [Fact]
    public void Connection_string_is_read_from_the_environment_variable()
    {
        Environment.SetEnvironmentVariable("MonitoringTest_Local", "Host=localhost");

        var environments = new MonitoringEnvironments(OptionsWith(("local", "MonitoringTest_Local")));

        var local = environments.Find("LOCAL");
        Assert.NotNull(local);
        Assert.True(local!.IsConfigured);
        Assert.Equal(EnvironmentStatus.Connecting, local.Status);
    }

    [Fact]
    public void Missing_variable_makes_the_environment_unavailable()
    {
        var environments = new MonitoringEnvironments(OptionsWith(("INT", "MonitoringTest_DoesNotExist")));

        Assert.Equal(EnvironmentStatus.Unavailable, environments.Find("INT")!.Status);
    }

    [Fact]
    public void Same_code_declared_twice_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new MonitoringEnvironments(OptionsWith(("POC", "A"), ("poc", "B"))));
    }

    [Fact]
    public void Setting_the_same_status_twice_is_reported_only_once()
    {
        var environments = new MonitoringEnvironments(OptionsWith(("POC", "MonitoringTest_DoesNotExist")));
        var poc = environments.Find("POC")!;

        Assert.True(environments.SetStatus(poc, EnvironmentStatus.Listening));
        Assert.False(environments.SetStatus(poc, EnvironmentStatus.Listening));
    }
}

using FeteadApi.Infrastructure.Services.Monitoring;

namespace FeteadApi.Monitoring.Tests;

public class MonitoringEnvironmentRegistryTests
{
    private static readonly TimeProvider Clock = new FixedTimeProvider(DateTimeOffset.UnixEpoch);

    private static MonitoringOptions Options(params (string Code, string Variable)[] environments) => new()
    {
        Environments = environments
            .Select(e => new MonitoringEnvironmentOptions { Code = e.Code, ConnectionStringVariable = e.Variable })
            .ToList()
    };

    [Fact]
    public void Connection_string_is_read_from_the_referenced_variable()
    {
        var registry = new MonitoringEnvironmentRegistry(
            Options(("local", "StyxConnectionLocal")),
            name => name == "StyxConnectionLocal" ? "Host=localhost" : null,
            Clock);

        var environment = Assert.Single(registry.Environments);
        Assert.Equal("LOCAL", environment.Code);
        Assert.True(environment.IsConfigured);
        Assert.Equal("Connecting", registry.GetStatuses()[0].Status);
    }

    [Fact]
    public void Missing_variable_marks_environment_unavailable_without_failing()
    {
        var registry = new MonitoringEnvironmentRegistry(
            Options(("INT", "StyxConnectionInt")), _ => null, Clock);

        Assert.False(registry.Environments[0].IsConfigured);
        Assert.Equal("Unavailable", registry.GetStatuses()[0].Status);
    }

    [Fact]
    public void Duplicate_environment_codes_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new MonitoringEnvironmentRegistry(
            Options(("POC", "A"), ("poc", "B")), _ => "Host=x", Clock));
    }

    [Fact]
    public void Status_change_is_reported_once()
    {
        var registry = new MonitoringEnvironmentRegistry(
            Options(("POC", "StyxConnectionPoc")), _ => "Host=x", Clock);

        var first = registry.SetStatus("poc", EnvironmentStatus.Listening);
        var second = registry.SetStatus("POC", EnvironmentStatus.Listening);

        Assert.NotNull(first);
        Assert.Equal("Listening", first!.Status);
        Assert.Null(second);
    }
}

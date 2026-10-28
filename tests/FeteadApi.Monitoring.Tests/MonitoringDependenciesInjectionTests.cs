using FeteadApi.Infrastructure.Services.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FeteadApi.Monitoring.Tests;

/// <summary>Vérifie que tous les services du module peuvent être créés par l'injection.</summary>
public class MonitoringDependenciesInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:LatestJobsLimit"] = "20",
                ["Monitoring:Environments:0:Code"] = "LOCAL",
                ["Monitoring:Environments:0:ConnectionStringVariable"] = "MonitoringTest_InjectionLocal"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        MonitoringDependenciesInjection.Init(services, configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void All_monitoring_services_can_be_created()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<MonitoringEnvironments>());
        Assert.NotNull(provider.GetRequiredService<MonitoringRepository>());
        Assert.NotNull(provider.GetRequiredService<MonitoringSnapshotService>());
    }

    [Fact]
    public void Environments_are_shared_by_the_whole_application()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<MonitoringEnvironments>();
        var second = provider.GetRequiredService<MonitoringEnvironments>();

        Assert.Same(first, second);
    }

    [Fact]
    public void Configuration_section_is_read()
    {
        using var provider = BuildProvider();

        var environments = provider.GetRequiredService<MonitoringEnvironments>();

        Assert.NotNull(environments.Find("LOCAL"));
    }
}

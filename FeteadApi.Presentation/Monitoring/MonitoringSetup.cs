using FeteadApi.Infrastructure.Services.Monitoring;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FeteadApi.Presentation.Monitoring;

/// <summary>
/// Branchement du module STYX Monitoring. Program.cs n'a besoin que de deux lignes :
///   builder.Services.AddStyxMonitoring(builder.Configuration);
///   app.MapStyxMonitoring();            // après app.MapControllers()
/// </summary>
public static class MonitoringSetup
{
    public const string HubPath = "/monitoringHub";

    public static IServiceCollection AddStyxMonitoring(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MonitoringOptions>(configuration.GetSection(MonitoringOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<MonitoringEnvironmentRegistry>();
        services.AddSingleton<MonitoringRepository>();
        services.AddSingleton<ITaskJobResolver>(sp => sp.GetRequiredService<MonitoringRepository>());
        services.AddSingleton<MonitoringSnapshotService>();
        services.AddSingleton<IMonitoringPublisher, SignalRMonitoringPublisher>();
        services.AddSingleton<MonitoringAggregator>();
        services.AddHostedService<MonitoringBackgroundService>();

        services.AddSignalR();
        return services;
    }

    public static IEndpointRouteBuilder MapStyxMonitoring(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<MonitoringHub>(HubPath);
        return endpoints;
    }
}

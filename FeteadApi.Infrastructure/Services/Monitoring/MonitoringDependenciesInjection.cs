using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FeteadApi.Infrastructure.Services.Monitoring;

/// <summary>
/// Enregistrement des services Infrastructure du module STYX Monitoring.
/// Même forme que FeteadApi.Infrastructure.DependenciesInjection.Init :
/// à appeler depuis cette méthode avec une seule ligne :
///     MonitoringDependenciesInjection.Init(services, configuration);
/// </summary>
public static class MonitoringDependenciesInjection
{
    public static void Init(IServiceCollection services, IConfiguration configuration)
    {
        // Section "Monitoring" de appsettings.json → MonitoringOptions
        services.Configure<MonitoringOptions>(configuration.GetSection("Monitoring"));

        // Singletons : une seule instance pour toute l'application.
        // MonitoringEnvironments doit être unique, car le service d'écoute
        // et l'API partagent le statut de chaque environnement.
        services.AddSingleton<MonitoringEnvironments>();
        services.AddSingleton<MonitoringRepository>();
        services.AddSingleton<MonitoringSnapshotService>();
    }
}

namespace FeteadApi.Presentation.Monitoring;

/// <summary>
/// Enregistrement de la partie Presentation du module STYX Monitoring
/// (SignalR et service d'écoute PostgreSQL). Utilisation dans Program.cs :
///
///     // avec les autres services, avant builder.Build()
///     MonitoringPresentationInjection.Init(builder.Services);
///
///     // après app.MapControllers()
///     MonitoringPresentationInjection.MapEndpoints(app);
///
/// Le contrôleur MonitoringController n'a pas besoin d'être enregistré :
/// AddControllers() le trouve tout seul.
/// </summary>
public static class MonitoringPresentationInjection
{
    public const string HubPath = "/monitoringHub";

    public static void Init(IServiceCollection services)
    {
        services.AddSignalR();

        // Service de fond : démarre avec l'application et écoute PostgreSQL en continu.
        services.AddHostedService<MonitoringListenerService>();
    }

    public static void MapEndpoints(WebApplication app)
    {
        app.MapHub<MonitoringHub>(HubPath);
    }
}

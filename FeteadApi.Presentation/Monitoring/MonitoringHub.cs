using Microsoft.AspNetCore.SignalR;

namespace FeteadApi.Presentation.Monitoring;

/// <summary>
/// Hub SignalR du module STYX Monitoring (/monitoringHub).
/// Diffusion uniquement, aucune méthode appelable par le client.
/// Événements : "JobChanged", "TaskChanged", "EnvironmentStatusChanged".
/// Tous les clients reçoivent tous les environnements ; le filtrage est fait côté navigateur.
/// </summary>
public sealed class MonitoringHub : Hub
{
    public const string JobChanged = "JobChanged";
    public const string TaskChanged = "TaskChanged";
    public const string EnvironmentStatusChanged = "EnvironmentStatusChanged";
}

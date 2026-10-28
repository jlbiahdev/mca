using Microsoft.AspNetCore.SignalR;

namespace FeteadApi.Presentation.Monitoring;

/// <summary>
/// Hub SignalR du module STYX Monitoring, exposé sur /monitoringHub.
/// Le navigateur ne fait qu'écouter : c'est MonitoringListenerService qui envoie
/// les messages "JobChanged", "TaskChanged" et "EnvironmentStatusChanged".
/// Tous les navigateurs reçoivent tous les environnements ; chacun filtre l'affichage.
/// </summary>
public class MonitoringHub : Hub
{
}

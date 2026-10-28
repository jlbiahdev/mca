using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FeteadApi.Infrastructure.Services.Monitoring;

/// <summary>
/// Configuration du module STYX Monitoring (section "Monitoring" de appsettings.json).
/// Les chaînes de connexion ne sont jamais stockées ici : chaque environnement
/// référence le nom de la variable d'environnement qui la contient.
/// </summary>
public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    public List<MonitoringEnvironmentOptions> Environments { get; set; } = new();

    /// <summary>Nombre maximal de jobs renvoyés dans le tableau, tous environnements confondus.</summary>
    public int LatestJobsLimit { get; set; } = 50;

    /// <summary>Délai avant nouvelle tentative après la perte d'une connexion LISTEN.</summary>
    public int ReconnectDelaySeconds { get; set; } = 5;

    /// <summary>Plage maximale autorisée pour un snapshot, afin de protéger les bases.</summary>
    public int MaxRangeDays { get; set; } = 92;

    /// <summary>Taille maximale du cache external_id → job_id (par instance).</summary>
    public int TaskJobCacheSize { get; set; } = 10_000;
}

public sealed class MonitoringEnvironmentOptions
{
    /// <summary>Code affiché (ex. LOCAL, POC, INT).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Nom de la variable d'environnement contenant la chaîne de connexion.</summary>
    public string ConnectionStringVariable { get; set; } = string.Empty;
}

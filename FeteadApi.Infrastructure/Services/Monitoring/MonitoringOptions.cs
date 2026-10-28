namespace FeteadApi.Infrastructure.Services.Monitoring;

/// <summary>
/// Section "Monitoring" de appsettings.json.
/// Les chaînes de connexion ne sont jamais écrites ici : chaque environnement
/// donne le NOM de la variable d'environnement qui contient sa chaîne.
/// </summary>
public class MonitoringOptions
{
    public List<MonitoringEnvironmentOptions> Environments { get; set; } = new();

    /// <summary>Nombre maximal de jobs affichés dans le tableau.</summary>
    public int LatestJobsLimit { get; set; } = 50;

    /// <summary>Plage maximale d'un snapshot, pour ne pas surcharger les bases.</summary>
    public int MaxRangeDays { get; set; } = 92;
}

public class MonitoringEnvironmentOptions
{
    /// <summary>Code affiché à l'écran : LOCAL, POC, INT…</summary>
    public string Code { get; set; } = "";

    /// <summary>Nom de la variable d'environnement, ex. "StyxConnectionLocal".</summary>
    public string ConnectionStringVariable { get; set; } = "";
}

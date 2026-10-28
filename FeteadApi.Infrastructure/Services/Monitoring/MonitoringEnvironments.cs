using Microsoft.Extensions.Options;

namespace FeteadApi.Infrastructure.Services.Monitoring;

/// <summary>Statuts possibles de l'écoute d'un environnement.</summary>
public static class EnvironmentStatus
{
    public const string Connecting = "Connecting";
    public const string Listening = "Listening";
    public const string Reconnecting = "Reconnecting";
    public const string Unavailable = "Unavailable";
}

/// <summary>Un environnement supervisé (LOCAL, POC, INT…).</summary>
public class MonitoringEnvironment
{
    public string Code { get; set; } = "";

    /// <summary>Ne jamais écrire cette valeur dans les logs ni la renvoyer au navigateur.</summary>
    public string? ConnectionString { get; set; }

    public string Status { get; set; } = EnvironmentStatus.Connecting;
    public DateTime StatusSince { get; set; } = DateTime.UtcNow;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}

/// <summary>
/// Liste des environnements supervisés, partagée entre le service d'écoute et l'API.
/// Enregistrée en singleton.
/// </summary>
public class MonitoringEnvironments
{
    private readonly List<MonitoringEnvironment> _environments = new();
    private readonly object _lock = new();

    public MonitoringEnvironments(IOptions<MonitoringOptions> options)
    {
        foreach (var entry in options.Value.Environments)
        {
            var code = entry.Code.Trim().ToUpperInvariant();

            if (code == "")
                throw new InvalidOperationException("Monitoring : un environnement n'a pas de Code.");

            if (Find(code) != null)
                throw new InvalidOperationException($"Monitoring : l'environnement {code} est déclaré deux fois.");

            // On lit la variable directement, et pas via IConfiguration :
            // dans Program.cs, appsettings.json est ajouté après les variables
            // d'environnement et passerait devant elles.
            string? connectionString = null;
            if (!string.IsNullOrWhiteSpace(entry.ConnectionStringVariable))
                connectionString = Environment.GetEnvironmentVariable(entry.ConnectionStringVariable);

            var environment = new MonitoringEnvironment { Code = code, ConnectionString = connectionString };
            if (!environment.IsConfigured)
                environment.Status = EnvironmentStatus.Unavailable;

            _environments.Add(environment);
        }
    }

    public List<MonitoringEnvironment> All => _environments;

    public MonitoringEnvironment? Find(string code)
    {
        return _environments.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Change le statut. Renvoie false si le statut était déjà celui-là.</summary>
    public bool SetStatus(MonitoringEnvironment environment, string status)
    {
        lock (_lock)
        {
            if (environment.Status == status)
                return false;

            environment.Status = status;
            environment.StatusSince = DateTime.UtcNow;
            return true;
        }
    }

    public List<EnvironmentStatusMessage> GetStatuses()
    {
        lock (_lock)
        {
            return _environments.Select(ToStatusMessage).ToList();
        }
    }

    public static EnvironmentStatusMessage ToStatusMessage(MonitoringEnvironment environment)
    {
        return new EnvironmentStatusMessage
        {
            Env = environment.Code,
            Status = environment.Status,
            Since = environment.StatusSince
        };
    }
}

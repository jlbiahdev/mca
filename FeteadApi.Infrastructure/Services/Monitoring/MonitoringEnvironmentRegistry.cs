using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace FeteadApi.Infrastructure.Services.Monitoring;

/// <summary>Environnement supervisé, avec sa chaîne de connexion résolue.</summary>
public sealed class MonitoringEnvironment
{
    public MonitoringEnvironment(string code, string? connectionString)
    {
        Code = code;
        ConnectionString = connectionString;
    }

    public string Code { get; }

    /// <summary>Ne jamais journaliser ni renvoyer cette valeur.</summary>
    public string? ConnectionString { get; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);

    public override string ToString() => Code;
}

/// <summary>
/// Liste des environnements supervisés et statut courant de leur connexion LISTEN.
/// Les chaînes sont lues directement dans les variables d'environnement :
/// dans Program.cs, appsettings.json est ré-ajouté après les sources par défaut
/// et passerait donc devant une variable lue via IConfiguration.
/// </summary>
public sealed class MonitoringEnvironmentRegistry
{
    private readonly IReadOnlyList<MonitoringEnvironment> _environments;
    private readonly ConcurrentDictionary<string, EnvironmentStatusChangedEvent> _statuses =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;

    public MonitoringEnvironmentRegistry(IOptions<MonitoringOptions> options, TimeProvider timeProvider)
        : this(options.Value, Environment.GetEnvironmentVariable, timeProvider)
    {
    }

    public MonitoringEnvironmentRegistry(
        MonitoringOptions options,
        Func<string, string?> readVariable,
        TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _environments = BuildEnvironments(options, readVariable);

        foreach (var environment in _environments)
        {
            var initialStatus = environment.IsConfigured
                ? EnvironmentStatus.Connecting
                : EnvironmentStatus.Unavailable;
            _statuses[environment.Code] = NewStatus(environment.Code, initialStatus);
        }
    }

    public IReadOnlyList<MonitoringEnvironment> Environments => _environments;

    public MonitoringEnvironment? Find(string code) =>
        _environments.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<EnvironmentStatusChangedEvent> GetStatuses() =>
        _environments.Select(e => _statuses[e.Code]).ToList();

    /// <summary>
    /// Enregistre un nouveau statut. Renvoie l'événement à diffuser,
    /// ou null si le statut est inchangé.
    /// </summary>
    public EnvironmentStatusChangedEvent? SetStatus(string code, EnvironmentStatus status)
    {
        var environment = Find(code)
            ?? throw new ArgumentException($"Environnement inconnu : {code}", nameof(code));

        var statusName = status.ToString();
        if (_statuses.TryGetValue(environment.Code, out var current) && current.Status == statusName)
        {
            return null;
        }

        var updated = NewStatus(environment.Code, status);
        _statuses[environment.Code] = updated;
        return updated;
    }

    private EnvironmentStatusChangedEvent NewStatus(string code, EnvironmentStatus status) =>
        new(code, status.ToString(), _timeProvider.GetUtcNow());

    private static IReadOnlyList<MonitoringEnvironment> BuildEnvironments(
        MonitoringOptions options,
        Func<string, string?> readVariable)
    {
        var environments = new List<MonitoringEnvironment>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in options.Environments)
        {
            var code = entry.Code?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code))
            {
                throw new InvalidOperationException("Monitoring : un environnement n'a pas de Code.");
            }

            if (!seenCodes.Add(code))
            {
                throw new InvalidOperationException($"Monitoring : environnement déclaré deux fois ({code}).");
            }

            var connectionString = string.IsNullOrWhiteSpace(entry.ConnectionStringVariable)
                ? null
                : readVariable(entry.ConnectionStringVariable);

            environments.Add(new MonitoringEnvironment(code, connectionString));
        }

        return environments;
    }
}

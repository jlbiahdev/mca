using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FeteadApi.Infrastructure.Services.Monitoring;

/// <summary>Raison d'un refus de snapshot, renvoyée au client en 400.</summary>
public sealed class MonitoringRequestException : Exception
{
    public MonitoringRequestException(string message) : base(message)
    {
    }
}

/// <summary>
/// Construit le snapshot d'une plage sur les environnements demandés.
/// Un environnement en échec n'empêche pas les autres de répondre.
/// </summary>
public sealed class MonitoringSnapshotService
{
    private const string UnavailableMessage = "Environnement indisponible";

    private readonly MonitoringEnvironmentRegistry _registry;
    private readonly MonitoringRepository _repository;
    private readonly MonitoringOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MonitoringSnapshotService> _logger;

    public MonitoringSnapshotService(
        MonitoringEnvironmentRegistry registry,
        MonitoringRepository repository,
        IOptions<MonitoringOptions> options,
        TimeProvider timeProvider,
        ILogger<MonitoringSnapshotService> logger)
    {
        _registry = registry;
        _repository = repository;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<MonitoringSnapshot> GetSnapshotAsync(
        IReadOnlyCollection<string> environmentCodes,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        ValidateRange(from, to);
        var environments = ResolveEnvironments(environmentCodes);

        // Horodaté avant les requêtes : le client rejoue les événements reçus après cet instant.
        var generatedAt = _timeProvider.GetUtcNow();
        var bucketSeconds = MonitoringTimeBuckets.ChooseBucketSeconds(from, to);

        var results = await Task.WhenAll(environments.Select(environment =>
            LoadEnvironmentAsync(environment, from, to, bucketSeconds, ct)));

        var snapshots = results.Where(r => r.Snapshot is not null).Select(r => r.Snapshot!).ToList();
        var errors = results.Where(r => r.Error is not null).Select(r => r.Error!).ToList();

        return MonitoringSnapshotMerger.Merge(
            snapshots, errors, generatedAt, from, to, bucketSeconds, _options.LatestJobsLimit);
    }

    private void ValidateRange(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
        {
            throw new MonitoringRequestException("La fin de la plage doit être postérieure au début.");
        }

        if (to - from > TimeSpan.FromDays(_options.MaxRangeDays))
        {
            throw new MonitoringRequestException(
                $"La plage ne peut pas dépasser {_options.MaxRangeDays} jours.");
        }
    }

    private IReadOnlyList<MonitoringEnvironment> ResolveEnvironments(IReadOnlyCollection<string> codes)
    {
        var environments = new List<MonitoringEnvironment>();

        foreach (var code in codes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var environment = _registry.Find(code)
                ?? throw new MonitoringRequestException($"Environnement inconnu : {code}");
            environments.Add(environment);
        }

        return environments;
    }

    private async Task<(EnvironmentSnapshot? Snapshot, EnvironmentErrorDto? Error)> LoadEnvironmentAsync(
        MonitoringEnvironment environment,
        DateTimeOffset from,
        DateTimeOffset to,
        int bucketSeconds,
        CancellationToken ct)
    {
        if (!environment.IsConfigured)
        {
            return (null, new EnvironmentErrorDto(environment.Code, UnavailableMessage));
        }

        try
        {
            var snapshot = await _repository.GetSnapshotAsync(
                environment, from, to, bucketSeconds, _options.LatestJobsLimit, ct);
            return (snapshot, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Le détail reste dans les logs : il peut contenir des informations de connexion.
            _logger.LogError(ex, "[Monitoring:{Env}] Snapshot impossible", environment.Code);
            return (null, new EnvironmentErrorDto(environment.Code, UnavailableMessage));
        }
    }
}

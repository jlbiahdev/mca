using FeteadApi.Domain.Interfaces.Monitoring;
using FeteadApi.Infrastructure.Services.Monitoring;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FeteadApi.Presentation.Monitoring;

/// <summary>API du module STYX Monitoring (lecture seule).</summary>
[ApiController]
[Route("monitoring")]
public class MonitoringController : ControllerBase
{
    private readonly MonitoringEnvironments _environments;
    private readonly IMonitoringSnapshotService _snapshotService;
    private readonly MonitoringOptions _options;

    public MonitoringController(
        MonitoringEnvironments environments,
        IMonitoringSnapshotService snapshotService,
        IOptions<MonitoringOptions> options)
    {
        _environments = environments;
        _snapshotService = snapshotService;
        _options = options.Value;
    }

    /// <summary>GET /monitoring/environments : environnements configurés et état de leur écoute.</summary>
    [HttpGet("environments")]
    public IActionResult GetEnvironments()
    {
        return Ok(_environments.GetStatuses());
    }

    /// <summary>
    /// GET /monitoring/snapshot?envs=LOCAL,POC&amp;from=2026-10-28T00:00:00Z&amp;to=2026-10-28T12:00:00Z
    /// Sans "from" ni "to" : les dernières 24 heures.
    /// </summary>
    [HttpGet("snapshot")]
    public async Task<IActionResult> GetSnapshot(string? envs, DateTimeOffset? from, DateTimeOffset? to)
    {
        // 1. Période (toujours en UTC, comme en base)
        var end = to?.UtcDateTime ?? DateTime.UtcNow;
        var start = from?.UtcDateTime ?? end.AddHours(-24);

        if (end <= start)
            return BadRequest(new { message = "La fin de la période doit être après le début." });

        if (end - start > TimeSpan.FromDays(_options.MaxRangeDays))
            return BadRequest(new { message = $"La période ne peut pas dépasser {_options.MaxRangeDays} jours." });

        // 2. Environnements demandés
        var selected = new List<string>();
        var codes = (envs ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var code in codes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var environment = _environments.Find(code);
            if (environment == null)
                return BadRequest(new { message = $"Environnement inconnu : {code}" });

            selected.Add(environment.Code);
        }

        // 3. Données
        var snapshot = await _snapshotService.GetSnapshotAsync(selected, start, end);
        return Ok(snapshot);
    }
}

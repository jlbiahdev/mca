using FeteadApi.Infrastructure.Services.Monitoring;
using Microsoft.AspNetCore.Mvc;

namespace FeteadApi.Presentation.Monitoring;

/// <summary>Snapshot REST du module STYX Monitoring (lecture seule).</summary>
[ApiController]
[Route("monitoring")]
public sealed class MonitoringController : ControllerBase
{
    private static readonly TimeSpan DefaultRange = TimeSpan.FromHours(24);

    private readonly MonitoringEnvironmentRegistry _registry;
    private readonly MonitoringSnapshotService _snapshotService;
    private readonly TimeProvider _timeProvider;

    public MonitoringController(
        MonitoringEnvironmentRegistry registry,
        MonitoringSnapshotService snapshotService,
        TimeProvider timeProvider)
    {
        _registry = registry;
        _snapshotService = snapshotService;
        _timeProvider = timeProvider;
    }

    /// <summary>Environnements configurés et statut de leur connexion LISTEN.</summary>
    [HttpGet("environments")]
    public ActionResult<IReadOnlyList<EnvironmentStatusChangedEvent>> GetEnvironments() =>
        Ok(_registry.GetStatuses());

    /// <summary>
    /// État sur une plage. Exemple :
    /// GET /monitoring/snapshot?envs=LOCAL,POC&amp;from=2026-10-28T00:00:00Z&amp;to=2026-10-28T12:00:00Z
    /// Sans plage : les dernières 24 h.
    /// </summary>
    [HttpGet("snapshot")]
    public async Task<ActionResult<MonitoringSnapshot>> GetSnapshot(
        [FromQuery] string? envs,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var rangeEnd = to ?? _timeProvider.GetUtcNow();
        var rangeStart = from ?? rangeEnd - DefaultRange;
        var codes = ParseCodes(envs);

        try
        {
            return Ok(await _snapshotService.GetSnapshotAsync(codes, rangeStart, rangeEnd, ct));
        }
        catch (MonitoringRequestException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private static IReadOnlyCollection<string> ParseCodes(string? envs) =>
        string.IsNullOrWhiteSpace(envs)
            ? Array.Empty<string>()
            : envs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

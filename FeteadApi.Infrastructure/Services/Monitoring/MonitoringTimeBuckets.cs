using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FeteadApi.Infrastructure.Services.Monitoring;

/// <summary>
/// Découpage d'une plage en intervalles pour le graphique d'évolution.
/// Règle : le plus petit pas "rond" donnant au plus <see cref="MaxPoints"/> points.
/// </summary>
public static class MonitoringTimeBuckets
{
    public const int MaxPoints = 60;

    private const int Day = 86_400;

    private static readonly int[] CandidateSeconds =
    {
        60, 300, 900, 1_800, 3_600, 10_800, 21_600, 43_200, Day
    };

    public static int ChooseBucketSeconds(DateTimeOffset from, DateTimeOffset to)
    {
        var totalSeconds = Math.Max(1d, (to - from).TotalSeconds);

        foreach (var candidate in CandidateSeconds)
        {
            if (totalSeconds / candidate <= MaxPoints)
            {
                return candidate;
            }
        }

        // Au-delà de 60 jours : multiple de jours.
        var days = (int)Math.Ceiling(totalSeconds / Day / MaxPoints);
        return days * Day;
    }

    public static int BucketCount(DateTimeOffset from, DateTimeOffset to, int bucketSeconds)
    {
        var totalSeconds = Math.Max(1d, (to - from).TotalSeconds);
        return Math.Max(1, (int)Math.Ceiling(totalSeconds / bucketSeconds));
    }
}

using System.IO;

namespace YinggeVideoLibrary;

internal static class ScrapeSourcePolicyDiagnostics
{
    public static IReadOnlyDictionary<string, bool> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var defaults = ScrapeSourcePolicy.Normalize(null, 5);
        var invalid = ScrapeSourcePolicy.Normalize(new ScrapeSourcePolicy
        {
            IntervalSeconds = double.NaN, ConsecutiveFailureLimit = 99
        }, 5);

        var first = ScrapeQueueStore.ReserveSourceRequest(directory, "TMDB", TimeSpan.FromSeconds(5));
        var second = ScrapeQueueStore.ReserveSourceRequest(directory, "TMDB", TimeSpan.FromSeconds(5));
        var ordinarySpacingCooldown = ScrapeQueueStore.GetSourceCooldownAt(directory, "TMDB");
        var otherSource = ScrapeQueueStore.ReserveSourceRequest(directory, "Bangumi", TimeSpan.FromSeconds(1));
        ScrapeQueueStore.ReportSourceSuccess(directory, "TMDB");
        var afterSuccess = ScrapeQueueStore.GetSourceReadyAt(directory, "TMDB");
        var saved = ScrapeQueueStore.ValidateSourceBackoffSnapshot(
            Path.Combine(directory, "scrape-source-backoff.json"));

        var firstFailure = ScrapeQueueStore.RecordSourceFailure(directory, "Bangumi", "temporary", false,
            consecutiveFailureLimit: 2);
        var cappedFailure = ScrapeQueueStore.RecordSourceFailure(directory, "Bangumi", "temporary", false,
            consecutiveFailureLimit: 2);
        return new Dictionary<string, bool>
        {
            ["sourcePolicyLegacyIntervalAndBounds"] = defaults.IntervalSeconds == 5 && defaults.ConsecutiveFailureLimit == 4
                && invalid.IntervalSeconds == 2.5 && invalid.ConsecutiveFailureLimit == 10,
            ["sourceSpacingDurableAndIndependent"] = first == DateTimeOffset.MinValue
                && second > DateTimeOffset.UtcNow.AddSeconds(3)
                && otherSource == DateTimeOffset.MinValue && afterSuccess >= second && saved,
            ["sourceSpacingDoesNotBlockSameItemDetail"] = ordinarySpacingCooldown <= DateTimeOffset.UtcNow,
            ["sourceFailureLimitOpensLongCooldown"] = firstFailure > DateTimeOffset.UtcNow
                && cappedFailure > DateTimeOffset.UtcNow.AddMinutes(55)
        };
    }
}

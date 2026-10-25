namespace YinggeVideoLibrary;

// Source-specific network pacing for the single-worker background queue.
// An old config without policies keeps the existing global interval.
public sealed class ScrapeSourcePolicy
{
    public static double DefaultIntervalFor(string source, double legacyIntervalSeconds)
        => source.Equals("Kitsu", StringComparison.OrdinalIgnoreCase) ||
           source.Equals("Tenrai", StringComparison.OrdinalIgnoreCase) ||
           source.Equals("Mikan", StringComparison.OrdinalIgnoreCase) ||
           source.Equals("FankuHub", StringComparison.OrdinalIgnoreCase) ||
           source.Equals("DMHY", StringComparison.OrdinalIgnoreCase)
            ? (double.IsFinite(legacyIntervalSeconds) ? Math.Max(legacyIntervalSeconds, 5) : 5)
            : legacyIntervalSeconds;

    public double IntervalSeconds { get; set; } = 2.5;
    // After this many consecutive temporary failures, pause the source for
    // one hour. Queued videos are kept for a later retry or manual review.
    public int ConsecutiveFailureLimit { get; set; } = 4;

    public ScrapeSourcePolicy Clone() => new()
    {
        IntervalSeconds = IntervalSeconds,
        ConsecutiveFailureLimit = ConsecutiveFailureLimit
    };

    public static ScrapeSourcePolicy Normalize(ScrapeSourcePolicy? source, double legacyIntervalSeconds)
    {
        var interval = source?.IntervalSeconds ?? legacyIntervalSeconds;
        return new ScrapeSourcePolicy
        {
            IntervalSeconds = double.IsFinite(interval) ? Math.Clamp(interval, 1, 60) : 2.5,
            ConsecutiveFailureLimit = Math.Clamp(source?.ConsecutiveFailureLimit ?? 4, 1, 10)
        };
    }
}

namespace YinggeVideoLibrary;

internal static class AiScrapePolicy
{
    // An AI call cannot repair a missing or contradictory identity. Keep the
    // candidate for human review without spending a request on that decision.
    internal static bool ShouldAutoAssess(VideoItem video, MovieMetadata candidate, bool forcePending) =>
        !forcePending &&
        ScrapeIdentityPolicy.Apply(video.Title, candidate, 100, "approve", "本地预检").Verdict == "approve";

    internal const int ReviewOutputTokenLimit = 256;
}

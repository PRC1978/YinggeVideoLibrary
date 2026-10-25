using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class RecommendationPreferenceDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Verify()
    {
        var videos = new[]
        {
            new RecommendationSnapshot("liked", true, "未看", DateTime.Today, ["电影"], ["悬疑"]),
            new RecommendationSnapshot("same-kind", false, "未看", DateTime.Today, ["电影"], ["悬疑"]),
            new RecommendationSnapshot("other-kind", false, "未看", DateTime.Today, ["纪录片"], ["自然"])
        };
        var baseline = RecommendationEngine.Rank(videos, videos.Length);
        var reduced = RecommendationEngine.Rank(videos, videos.Length,
            new RecommendationOptions(LessLikeVideoIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "liked" }));
        var disabled = RecommendationEngine.Rank(videos, videos.Length,
            new RecommendationOptions(UseFavorites: false, UseWatchStatus: false,
                UseCategories: false, UseTags: false, EnableOnlineTopics: false));
        var config = JsonSerializer.Deserialize<LibraryConfig>(JsonSerializer.Serialize(new LibraryConfig
        {
            RecommendationUseTags = false,
            RecommendationOnlineTopics = false,
            RecommendationLessLikeVideoIds = ["liked"]
        }));
        var onlineVideos = new[]
        {
            new RecommendationSnapshot("movie", true, "未看", DateTime.Today, ["电影"], []),
            new RecommendationSnapshot("documentary", true, "未看", DateTime.Today, ["纪录片"], []),
            new RecommendationSnapshot("reduced-movie", false, "未看", DateTime.Today, ["电影"], [])
        };
        var onlineBefore = RecommendationEngine.BuildBilibiliTopics(onlineVideos, 2);
        var onlineAfter = RecommendationEngine.BuildBilibiliTopics(onlineVideos, 2,
            new RecommendationOptions(LessLikeVideoIds: new HashSet<string> { "reduced-movie" }));
        return new Dictionary<string, bool>
        {
            ["recommendationFeedbackExcludesAndPenalizes"] =
                baseline.Count == 3 && reduced.Count == 2
                && reduced.All(hit => hit.VideoId != "liked")
                && reduced.Single(hit => hit.VideoId == "same-kind").Score
                    < reduced.Single(hit => hit.VideoId == "other-kind").Score,
            ["recommendationSignalSwitchesChangeScores"] = disabled.Count == 3
                && baseline.Single(hit => hit.VideoId == "liked").Score
                    != disabled.Single(hit => hit.VideoId == "liked").Score
                && disabled.All(hit => hit.Reason is not null
                    && !hit.Reason.Contains("标签", StringComparison.Ordinal)
                    && !hit.Reason.Contains("分类", StringComparison.Ordinal)),
            ["recommendationOnlineOffStopsTopics"] = RecommendationEngine.BuildBilibiliTopics(videos, 6,
                new RecommendationOptions(EnableOnlineTopics: false)).Count == 0,
            ["recommendationFeedbackChangesOnlineTopics"] = onlineBefore.FirstOrDefault()?.Label == "电影"
                && onlineAfter.FirstOrDefault()?.Label == "纪录片",
            ["recommendationPreferencesPersist"] = config is
                { RecommendationUseTags: false, RecommendationOnlineTopics: false }
                && config.RecommendationLessLikeVideoIds.SequenceEqual(["liked"])
        };
    }
}

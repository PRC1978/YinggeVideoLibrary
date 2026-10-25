namespace YinggeVideoLibrary;

internal static class AnimeSourceIntegrationDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Verify()
    {
        var anime = new VideoItem
        {
            Id = "anime", Title = "葬送的芙莉莲", Path = @"C:\Library\anime.mkv",
            Folder = @"C:\Library", Size = 1, Modified = DateTime.UnixEpoch,
            Category = "动漫 / 番剧"
        };
        var other = new VideoItem
        {
            Id = "other", Title = "普通电影", Path = @"C:\Library\movie.mkv",
            Folder = @"C:\Library", Size = 1, Modified = DateTime.UnixEpoch,
            Category = "电影"
        };
        var verified = new MovieMetadata
        {
            Title = "葬送的芙莉莲", Source = "Mikan",
            SourceUrl = "https://mikanani.me/Home/Bangumi/3141",
            DetailFetched = true, DetailIdentityVerified = true
        };
        var mikanGate = ScrapeIdentityPolicy.Apply(anime.Title, verified, 99, "approve", "标题一致");
        verified.Source = "FankuHub";
        verified.SourceUrl = "https://fankuhub.com/anime/cmpaxwdmq0amnmhlhtwpagp5l";
        var fankuGate = ScrapeIdentityPolicy.Apply(anime.Title, verified, 99, "approve", "标题一致");
        var defaultBulk = MainWindow.PlanCandidateSources(3, bulk: true);
        var optedBulk = MainWindow.PlanCandidateSources(3, bulk: true, animeQueue: true);
        var defaultQueue = MainWindow.PlanQueueTitleSources(false, false, false, false, false, false);
        var optedQueue = MainWindow.PlanQueueTitleSources(false, false, false, false, false, false,
            mikan: true, fankuHub: true);
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["animeSourcesManualSearchAndBulkOptIn"] =
                MainWindow.PlanCandidateSources(14, bulk: false).SequenceEqual([14]) &&
                MainWindow.PlanCandidateSources(15, bulk: false).SequenceEqual([15]) &&
                MainWindow.PlanCandidateSources(3, bulk: false).TakeLast(2).SequenceEqual([14, 15]) &&
                !defaultBulk.Contains(14) && !defaultBulk.Contains(15) &&
                optedBulk.TakeLast(2).SequenceEqual([14, 15]),
            ["animeSourcesQueueOnlyExplicitAnimeCategory"] =
                MainWindow.IsAnimeCategory(anime) && !MainWindow.IsAnimeCategory(other) &&
                defaultQueue.Length == 0 && optedQueue.SequenceEqual(["Mikan", "FankuHub"]),
            ["animeSourcesNeverAutoApproveByExactTitle"] =
                mikanGate.Verdict == "review" && fankuGate.Verdict == "review" &&
                mikanGate.Confidence < 80 && fankuGate.Confidence < 80,
            ["animeSourcesHaveAtLeastFiveSecondQueueSpacing"] =
                ScrapeSourcePolicy.DefaultIntervalFor("Mikan", 2.5) >= 5 &&
                ScrapeSourcePolicy.DefaultIntervalFor("FankuHub", 2.5) >= 5 &&
                ScrapeSourcePolicy.DefaultIntervalFor("DMHY", 2.5) >= 5
        };
    }
}

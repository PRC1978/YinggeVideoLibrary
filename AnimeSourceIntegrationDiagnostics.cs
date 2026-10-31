namespace YinggeVideoLibrary;

using System.IO;
using System.Net;

internal static class AnimeSourceIntegrationDiagnostics
{
    // End-to-end check for the anime access-verification path: the refusal must
    // be persisted in scrape-source-backoff.json and be reported as "冷却中" by
    // the health report, so the pause survives a restart and the video keeps its
    // retry budget.
    internal static bool VerifyAccessVerificationCooldown(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var now = DateTimeOffset.UtcNow;
        foreach (var source in new[] { "FankuHub", "Mikan", "DMHY" })
        {
            var refusal = source switch
            {
                "FankuHub" => FankuHubScraper.AccessVerificationFailure(HttpStatusCode.Forbidden, now),
                "Mikan" => MikanScraper.AccessVerificationFailure(HttpStatusCode.Forbidden, now),
                _ => DmhyAnimeReleaseScraper.AccessVerificationFailure(HttpStatusCode.Forbidden, now)
            };
            var until = ScrapeQueueStore.RecordSourceFailure(dataDir, source, refusal.Message, false,
                refusal.RetryAt - now, 4);
            if (until <= now) return false;
        }
        var config = new LibraryConfig { EnableFankuHub = true, EnableMikan = true, EnableDmhy = true };
        var snapshot = SourceHealthDiagnostics.Capture(dataDir, config, now);
        return File.Exists(Path.Combine(dataDir, "scrape-source-backoff.json"))
            && snapshot.Sources.Where(item => item.Name is "FankuHub" or "Mikan" or "DMHY")
                .All(item => item.Status == "冷却中" && item.CooldownUntilUtc > now
                    && item.LastFailureCategory == "来源要求访问验证");
    }

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
                ScrapeSourcePolicy.DefaultIntervalFor("DMHY", 2.5) >= 5,
            // Queue semantics: an access-verification refusal keeps the task and
            // does not consume the video's retry attempts.
            ["animeAccessVerificationKeepsRetryBudget"] = MainWindow.VerifySourceRetryPolicyForDiagnostics(),
            ["animeFailureCategoriesDistinguishVerification"] =
                SourceHealthDiagnostics.ClassifyFailure("番库要求访问验证或拒绝访问（人机校验页，HTTP 403），已停止读取该页面；稍后自动重试。")
                    == "来源要求访问验证" &&
                SourceHealthDiagnostics.ClassifyFailure("番库返回 HTTP 403") == "凭据或访问权限错误" &&
                SourceHealthDiagnostics.ClassifyFailure("番库返回 HTTP 429") == "请求受限（429）"
        };
    }
}

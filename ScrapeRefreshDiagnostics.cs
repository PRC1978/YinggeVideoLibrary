using System.IO;

namespace YinggeVideoLibrary;

internal static class ScrapeRefreshDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Verify(string directory)
    {
        var complete = new MovieMetadata
        {
            Code = "ABP-123", Title = "已核对作品", Source = "JavDB",
            DetailFetched = true, DetailIdentityVerified = true,
            ReleaseDate = "2025-01-02", RuntimeMinutes = 120,
            Studio = "制作社", Actors = ["演员甲"], Genres = ["类别甲"],
            PosterUrl = "https://c0.jdbstatic.com/cover.jpg"
        };
        var incomplete = complete.Clone();
        incomplete.Studio = null;
        var legacy = complete.Clone();
        legacy.DetailFetched = false;
        var video = new VideoItem
        {
            Id = "refresh-diagnostic", Title = "1", Folder = @"C:\Library",
            Path = @"C:\Library\1.mp4", Size = 1, Modified = DateTime.UnixEpoch
        };
        var planned = MainWindow.PlanScrapeQueueItem(video, true, false, [],
            refreshExisting: true, acceptedCode: "ABP-123", acceptedTitle: "已核对作品");
        var conflictVideo = new VideoItem
        {
            Id = "refresh-conflict", Title = "ABC-999", Folder = @"C:\Library",
            Path = @"C:\Library\ABC-999.mp4", Size = 1, Modified = DateTime.UnixEpoch
        };
        var conflict = MainWindow.PlanScrapeQueueItem(conflictVideo, true, false, [],
            refreshExisting: true, acceptedCode: "ABP-123", acceptedTitle: "已核对作品");
        var queueDirectory = Path.Combine(directory, "refresh-queue");
        Directory.CreateDirectory(queueDirectory);
        ScrapeQueueStore.Add(queueDirectory, [planned.Item!]);
        var reloaded = ScrapeQueueStore.Load(queueDirectory).SingleOrDefault();
        var allManualSources = MainWindow.PlanCandidateSources(3, bulk: false);
        var codeSources = MainWindow.PlanQueueCodeSources(true, true);
        var titleSources = MainWindow.PlanQueueTitleSources(true, true, true, true, true, true);
        var titleSourcesWithWikidata = MainWindow.PlanQueueTitleSources(true, true, true, true, true, true, true);
        var paidEvidence = JavinfoSearchEvidence.Create("ABC-123", "作品の題名");
        var paidRequiresEvidence = RejectsPaidWithoutEvidence();
        var paidRejectsBulk = RejectsPaidBulk(paidEvidence);
        MainWindow.RequireManualJavinfoEvidence(false, true, paidEvidence, "jvi_diagnostic");

        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["scrapeRefreshDefaultKeepsAcceptedOut"] = !MainWindow.CanRefreshSavedScrape(incomplete,
                new AiReviewRecord { Status = "approved" }, false, false),
            ["scrapeRefreshExplicitIncludesMissing"] = MainWindow.CanRefreshSavedScrape(incomplete,
                new AiReviewRecord { Status = "approved" }, true, false),
            ["scrapeRefreshExcludesPendingAndRejected"] = !MainWindow.CanRefreshSavedScrape(incomplete,
                new AiReviewRecord { Status = "pending" }, true, false)
                && !MainWindow.CanRefreshSavedScrape(incomplete,
                    new AiReviewRecord { Status = "rejected" }, true, false),
            ["scrapeRefreshSkipsComplete"] = !MainWindow.CanRefreshSavedScrape(complete,
                new AiReviewRecord { Status = "approved" }, true, false),
            ["scrapeRefreshLegacySettingUnchanged"] = MainWindow.CanRefreshSavedScrape(legacy,
                new AiReviewRecord { Status = "approved" }, false, true)
                && !MainWindow.CanRefreshSavedScrape(legacy,
                    new AiReviewRecord { Status = "approved" }, false, false),
            ["scrapeRefreshUsesAcceptedCode"] = planned.Item is
                { Kind: "code", Code: "ABP-123", RefreshExisting: true },
            ["scrapeRefreshConflictingCodesNeedReview"] = conflict.Item is
                { Kind: "title", SearchTitle: "已核对作品", RefreshExisting: true }
                && conflict.NeedsManualTitle,
            ["scrapeRefreshIntentSurvivesRestart"] = reloaded is
                { Kind: "code", Code: "ABP-123", RefreshExisting: true },
            ["scrapeNativeQueueNeverPlansJavinfo"] = codeSources.SequenceEqual(["JavDB", "JAVDatabase"])
                && titleSources.SequenceEqual(["TMDB", "Pornhub", "TVmaze", "Bangumi", "JavDB", "JAVDatabase"])
                && !codeSources.Concat(titleSources).Contains("Javinfo", StringComparer.OrdinalIgnoreCase),
            ["scrapeAllManualSourcesExcludeJavinfo"] = allManualSources.SequenceEqual([0, 1, 2, 4, 5, 6, 7, 9, 11, 12, 14, 15])
                && !allManualSources.Contains(8),
            ["wikidataOptInTitleQueueWithoutPaidSource"] = titleSourcesWithWikidata.SequenceEqual(
                ["TMDB", "Pornhub", "TVmaze", "Bangumi", "JavDB", "JAVDatabase", "Wikidata"])
                && !titleSourcesWithWikidata.Contains("Javinfo", StringComparer.OrdinalIgnoreCase),
            ["scrapePaidSourceRequiresExplicitEvidence"] = paidRequiresEvidence && paidRejectsBulk
                && MainWindow.PlanCandidateSources(8, bulk: false).SequenceEqual([8])
        };
    }

    static bool RejectsPaidWithoutEvidence()
    {
        try { MainWindow.RequireManualJavinfoEvidence(false, true, null, "jvi_diagnostic"); return false; }
        catch (InvalidOperationException) { return true; }
    }

    static bool RejectsPaidBulk(JavinfoSearchEvidence evidence)
    {
        try { MainWindow.RequireManualJavinfoEvidence(true, true, evidence, "jvi_diagnostic"); return false; }
        catch (InvalidOperationException) { }
        try { MainWindow.PlanCandidateSources(8, bulk: true); return false; }
        catch (InvalidOperationException) { return true; }
    }
}

using System.Text.Json;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    // The baseline is a deep copy: UI actions mutate the lists and dictionaries
    // on config in place, so keeping a second reference would hide every edit.
    static LibraryConfig CloneLibraryConfig(LibraryConfig source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        Folders = [.. source.Folders],
        FoldersClearedByUser = source.FoldersClearedByUser,
        Favorites = [.. source.Favorites],
        Player = source.Player,
        Categories = new Dictionary<string, string>(source.Categories, StringComparer.OrdinalIgnoreCase),
        MultiCategories = source.MultiCategories.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        Tags = source.Tags.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        CategoryNames = [.. source.CategoryNames],
        WatchStates = new Dictionary<string, string>(source.WatchStates, StringComparer.OrdinalIgnoreCase),
        AutoCategorize = source.AutoCategorize,
        CategoryRenames = new Dictionary<string, string>(source.CategoryRenames, StringComparer.OrdinalIgnoreCase),
        CustomPosters = new Dictionary<string, string>(source.CustomPosters, StringComparer.OrdinalIgnoreCase),
        DisplayTitleOverrides = new Dictionary<string, string>(source.DisplayTitleOverrides, StringComparer.OrdinalIgnoreCase),
        RecommendationUseFavorites = source.RecommendationUseFavorites,
        RecommendationUseWatchStatus = source.RecommendationUseWatchStatus,
        RecommendationUseCategories = source.RecommendationUseCategories,
        RecommendationUseTags = source.RecommendationUseTags,
        RecommendationOnlineTopics = source.RecommendationOnlineTopics,
        RecommendationLessLikeVideoIds = [.. source.RecommendationLessLikeVideoIds],
        ShowAll = source.ShowAll,
        TmdbToken = source.TmdbToken,
        EnableExtension = source.EnableExtension,
        AutoStart = source.AutoStart,
        MinimizeToTray = source.MinimizeToTray,
        StartMinimized = source.StartMinimized,
        StartPage = source.StartPage,
        GpuThumbnails = source.GpuThumbnails,
        AiEndpoint = source.AiEndpoint,
        AiModel = source.AiModel,
        AiKey = source.AiKey,
        AiDailyBudgetUsd = source.AiDailyBudgetUsd,
        AiInputUsdPerMillion = source.AiInputUsdPerMillion,
        AiOutputUsdPerMillion = source.AiOutputUsdPerMillion,
        AiMaxOutputTokens = source.AiMaxOutputTokens,
        AiRequestIntervalMs = source.AiRequestIntervalMs,
        AiLocalRequestIntervalMs = source.AiLocalRequestIntervalMs,
        AiCloudRequestIntervalMs = source.AiCloudRequestIntervalMs,
        AiLocalConcurrency = source.AiLocalConcurrency,
        AiCloudConcurrency = source.AiCloudConcurrency,
        AiLocalRetryCount = source.AiLocalRetryCount,
        AiCloudRetryCount = source.AiCloudRetryCount,
        AiReviews = source.AiReviews.ToDictionary(pair => pair.Key, pair => CloneReview(pair.Value), StringComparer.OrdinalIgnoreCase),
        AiSuggestions = source.AiSuggestions.ToDictionary(pair => pair.Key,
            pair => pair.Value is null ? null! : AiSuggestionAudit.Clone(pair.Value), StringComparer.Ordinal),
        AiSuggestionActions = source.AiSuggestionActions.ToDictionary(pair => pair.Key,
            pair => pair.Value is null ? null! : AiSuggestionAudit.Clone(pair.Value), StringComparer.Ordinal),
        SiteListLocalActions = source.SiteListLocalActions.ToDictionary(pair => pair.Key,
            pair => pair.Value is null ? null! : SiteListLocalActionStore.Clone(pair.Value), StringComparer.Ordinal),
        SearchHistory = [.. source.SearchHistory]
        ,SourceTrustWeights = new Dictionary<string, int>(source.SourceTrustWeights, StringComparer.OrdinalIgnoreCase)
        ,EnableJavDb = source.EnableJavDb
        ,EnableJavDatabase = source.EnableJavDatabase
        ,EnableJavDbTitle = source.EnableJavDbTitle
        ,EnableBangumi = source.EnableBangumi
        ,EnableAniList = source.EnableAniList
        ,EnableJavinfo = source.EnableJavinfo
        ,JavinfoKey = source.JavinfoKey
        ,JavinfoDailyRequestLimit = source.JavinfoDailyRequestLimit
        ,EnablePornhub = source.EnablePornhub
        ,EnableTmdb = source.EnableTmdb
        ,EnableTvMaze = source.EnableTvMaze
        ,EnableWikidata = source.EnableWikidata
        ,EnableOmdb = source.EnableOmdb
        ,EnableKitsu = source.EnableKitsu
        ,EnableTenrai = source.EnableTenrai
        ,EnableMikan = source.EnableMikan
        ,EnableFankuHub = source.EnableFankuHub
        ,EnableDmhy = source.EnableDmhy
        ,AnimeSourcesInQueue = source.AnimeSourcesInQueue
        ,EnableTrakt = source.EnableTrakt
        ,UseFolderPersonHint = source.UseFolderPersonHint
        ,SmartFilters = new Dictionary<string, string>(source.SmartFilters, StringComparer.CurrentCultureIgnoreCase)
        ,AutoAmateurTags = [.. source.AutoAmateurTags]
        ,ScrapeExcludedFolders = [.. source.ScrapeExcludedFolders]
        ,ScrapeFilenameContains = source.ScrapeFilenameContains
        ,ScrapeSkipTags = source.ScrapeSkipTags
        ,ScrapeSkipWatched = source.ScrapeSkipWatched
        ,ScrapeMinSizeMb = source.ScrapeMinSizeMb
        ,UpdateAutoCheck = source.UpdateAutoCheck
        ,UpdateCheckIntervalHours = source.UpdateCheckIntervalHours
        ,ScrapeRequestIntervalSeconds = source.ScrapeRequestIntervalSeconds
        ,ScrapeSourcePolicies = source.ScrapeSourcePolicies.ToDictionary(pair => pair.Key,
            pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase)
        ,ScrapeRefreshIncompleteMetadata = source.ScrapeRefreshIncompleteMetadata
        ,CardPageSize = source.CardPageSize
        ,PosterCacheLimitMb = source.PosterCacheLimitMb
    };

    static AiReviewRecord CloneReview(AiReviewRecord review) => new()
    {
        VideoId = review.VideoId,
        CandidateCode = review.CandidateCode,
        CandidateTitle = review.CandidateTitle,
        CandidateSource = review.CandidateSource,
        CandidateSourceUrl = review.CandidateSourceUrl,
        CandidatePoster = review.CandidatePoster,
        CandidatePosterUrl = review.CandidatePosterUrl,
        CandidateDetails = review.CandidateDetails?.Clone(),
        Confidence = review.Confidence,
        Status = review.Status,
        DecisionSource = review.DecisionSource,
        Reason = review.Reason,
        Model = review.Model,
        CreatedAt = review.CreatedAt,
        ReviewedAt = review.ReviewedAt
    };

    static LibraryConfig MergeLibraryConfig(LibraryConfig baseline, LibraryConfig current, LibraryConfig latest)
    {
        // A legacy or concurrent writer may replace the shared config with an
        // unmarked empty folder list. Do not let an unrelated setting save
        // silently erase this process's working library roots.
        var latestFolders = latest.Folders.Count == 0 && !latest.FoldersClearedByUser
            && baseline.Folders.Count > 0 ? baseline.Folders : latest.Folders;
        var merged = new LibraryConfig
        {
            SchemaVersion = Math.Max(baseline.SchemaVersion, Math.Max(current.SchemaVersion, latest.SchemaVersion)),
            Folders = MergeList(baseline.Folders, current.Folders, latestFolders, StringComparer.OrdinalIgnoreCase),
            FoldersClearedByUser = MergeScalar(baseline.FoldersClearedByUser, current.FoldersClearedByUser, latest.FoldersClearedByUser),
            Favorites = MergeList(baseline.Favorites, current.Favorites, latest.Favorites, StringComparer.OrdinalIgnoreCase),
            CategoryNames = MergeList(baseline.CategoryNames, current.CategoryNames, latest.CategoryNames, StringComparer.OrdinalIgnoreCase),
            SearchHistory = MergeList(baseline.SearchHistory, current.SearchHistory, latest.SearchHistory,
                StringComparer.CurrentCultureIgnoreCase, prioritizeRecent: true, limit: 12),
            Categories = MergeMap(baseline.Categories, current.Categories, latest.Categories, StringComparer.OrdinalIgnoreCase),
            MultiCategories = MergeListMap(baseline.MultiCategories, current.MultiCategories, latest.MultiCategories,
                StringComparer.OrdinalIgnoreCase, StringComparer.OrdinalIgnoreCase),
            Tags = MergeListMap(baseline.Tags, current.Tags, latest.Tags,
                StringComparer.OrdinalIgnoreCase, StringComparer.CurrentCultureIgnoreCase, limit: 50),
            WatchStates = MergeMap(baseline.WatchStates, current.WatchStates, latest.WatchStates, StringComparer.OrdinalIgnoreCase),
            CategoryRenames = MergeMap(baseline.CategoryRenames, current.CategoryRenames, latest.CategoryRenames, StringComparer.OrdinalIgnoreCase),
            CustomPosters = MergeMap(baseline.CustomPosters, current.CustomPosters, latest.CustomPosters, StringComparer.OrdinalIgnoreCase),
            DisplayTitleOverrides = MergeMap(baseline.DisplayTitleOverrides, current.DisplayTitleOverrides, latest.DisplayTitleOverrides ?? [], StringComparer.OrdinalIgnoreCase),
            RecommendationUseFavorites = MergeScalar(baseline.RecommendationUseFavorites, current.RecommendationUseFavorites, latest.RecommendationUseFavorites),
            RecommendationUseWatchStatus = MergeScalar(baseline.RecommendationUseWatchStatus, current.RecommendationUseWatchStatus, latest.RecommendationUseWatchStatus),
            RecommendationUseCategories = MergeScalar(baseline.RecommendationUseCategories, current.RecommendationUseCategories, latest.RecommendationUseCategories),
            RecommendationUseTags = MergeScalar(baseline.RecommendationUseTags, current.RecommendationUseTags, latest.RecommendationUseTags),
            RecommendationOnlineTopics = MergeScalar(baseline.RecommendationOnlineTopics, current.RecommendationOnlineTopics, latest.RecommendationOnlineTopics),
            RecommendationLessLikeVideoIds = MergeList(baseline.RecommendationLessLikeVideoIds, current.RecommendationLessLikeVideoIds,
                latest.RecommendationLessLikeVideoIds, StringComparer.OrdinalIgnoreCase),
            AiReviews = MergeMap(baseline.AiReviews, current.AiReviews, latest.AiReviews, StringComparer.OrdinalIgnoreCase),
            AiSuggestions = MergeMap(baseline.AiSuggestions, current.AiSuggestions, latest.AiSuggestions, StringComparer.OrdinalIgnoreCase),
            AiSuggestionActions = MergeMap(baseline.AiSuggestionActions, current.AiSuggestionActions, latest.AiSuggestionActions, StringComparer.OrdinalIgnoreCase),
            SiteListLocalActions = MergeMap(baseline.SiteListLocalActions, current.SiteListLocalActions,
                latest.SiteListLocalActions, StringComparer.Ordinal),
            SourceTrustWeights = MergeMap(baseline.SourceTrustWeights, current.SourceTrustWeights, latest.SourceTrustWeights, StringComparer.OrdinalIgnoreCase),
            EnableJavDb = MergeScalar(baseline.EnableJavDb, current.EnableJavDb, latest.EnableJavDb),
            EnableJavDatabase = MergeScalar(baseline.EnableJavDatabase, current.EnableJavDatabase, latest.EnableJavDatabase),
            EnableJavDbTitle = MergeScalar(baseline.EnableJavDbTitle, current.EnableJavDbTitle, latest.EnableJavDbTitle),
            EnableBangumi = MergeScalar(baseline.EnableBangumi, current.EnableBangumi, latest.EnableBangumi),
            EnableAniList = MergeScalar(baseline.EnableAniList, current.EnableAniList, latest.EnableAniList),
            EnableJavinfo = MergeScalar(baseline.EnableJavinfo, current.EnableJavinfo, latest.EnableJavinfo),
            JavinfoKey = MergeScalar(baseline.JavinfoKey, current.JavinfoKey, latest.JavinfoKey) ?? "",
            JavinfoDailyRequestLimit = MergeScalar(baseline.JavinfoDailyRequestLimit, current.JavinfoDailyRequestLimit, latest.JavinfoDailyRequestLimit),
            EnablePornhub = MergeScalar(baseline.EnablePornhub, current.EnablePornhub, latest.EnablePornhub),
            EnableTmdb = MergeScalar(baseline.EnableTmdb, current.EnableTmdb, latest.EnableTmdb),
            EnableTvMaze = MergeScalar(baseline.EnableTvMaze, current.EnableTvMaze, latest.EnableTvMaze),
            EnableWikidata = MergeScalar(baseline.EnableWikidata, current.EnableWikidata, latest.EnableWikidata),
            EnableOmdb = MergeScalar(baseline.EnableOmdb, current.EnableOmdb, latest.EnableOmdb),
            EnableKitsu = MergeScalar(baseline.EnableKitsu, current.EnableKitsu, latest.EnableKitsu),
            EnableTenrai = MergeScalar(baseline.EnableTenrai, current.EnableTenrai, latest.EnableTenrai),
            EnableMikan = MergeScalar(baseline.EnableMikan, current.EnableMikan, latest.EnableMikan),
            EnableFankuHub = MergeScalar(baseline.EnableFankuHub, current.EnableFankuHub, latest.EnableFankuHub),
            EnableDmhy = MergeScalar(baseline.EnableDmhy, current.EnableDmhy, latest.EnableDmhy),
            AnimeSourcesInQueue = MergeScalar(baseline.AnimeSourcesInQueue, current.AnimeSourcesInQueue, latest.AnimeSourcesInQueue),
            EnableTrakt = MergeScalar(baseline.EnableTrakt, current.EnableTrakt, latest.EnableTrakt),
            UseFolderPersonHint = MergeScalar(baseline.UseFolderPersonHint, current.UseFolderPersonHint, latest.UseFolderPersonHint),
            SmartFilters = MergeMap(baseline.SmartFilters, current.SmartFilters, latest.SmartFilters, StringComparer.CurrentCultureIgnoreCase),
            AutoAmateurTags = MergeList(baseline.AutoAmateurTags, current.AutoAmateurTags, latest.AutoAmateurTags, StringComparer.OrdinalIgnoreCase),
            ScrapeExcludedFolders = MergeList(baseline.ScrapeExcludedFolders, current.ScrapeExcludedFolders, latest.ScrapeExcludedFolders, StringComparer.OrdinalIgnoreCase),
            ScrapeFilenameContains = MergeScalar(baseline.ScrapeFilenameContains, current.ScrapeFilenameContains, latest.ScrapeFilenameContains) ?? "",
            ScrapeSkipTags = MergeScalar(baseline.ScrapeSkipTags, current.ScrapeSkipTags, latest.ScrapeSkipTags) ?? "",
            ScrapeSkipWatched = MergeScalar(baseline.ScrapeSkipWatched, current.ScrapeSkipWatched, latest.ScrapeSkipWatched),
            ScrapeMinSizeMb = MergeScalar(baseline.ScrapeMinSizeMb, current.ScrapeMinSizeMb, latest.ScrapeMinSizeMb),
            UpdateAutoCheck = MergeScalar(baseline.UpdateAutoCheck, current.UpdateAutoCheck, latest.UpdateAutoCheck),
            UpdateCheckIntervalHours = MergeScalar(baseline.UpdateCheckIntervalHours, current.UpdateCheckIntervalHours, latest.UpdateCheckIntervalHours),
            ScrapeRequestIntervalSeconds = MergeScalar(baseline.ScrapeRequestIntervalSeconds, current.ScrapeRequestIntervalSeconds, latest.ScrapeRequestIntervalSeconds),
            ScrapeSourcePolicies = MergeMap(baseline.ScrapeSourcePolicies, current.ScrapeSourcePolicies,
                latest.ScrapeSourcePolicies, StringComparer.OrdinalIgnoreCase),
            ScrapeRefreshIncompleteMetadata = MergeScalar(baseline.ScrapeRefreshIncompleteMetadata, current.ScrapeRefreshIncompleteMetadata, latest.ScrapeRefreshIncompleteMetadata),
            CardPageSize = MergeScalar(baseline.CardPageSize, current.CardPageSize, latest.CardPageSize),
            PosterCacheLimitMb = VideoItem.NormalizePosterCacheLimitMiB(
                MergeScalar(baseline.PosterCacheLimitMb, current.PosterCacheLimitMb, latest.PosterCacheLimitMb)),
            Player = MergeScalar(baseline.Player, current.Player, latest.Player) ?? "",
            AutoCategorize = MergeScalar(baseline.AutoCategorize, current.AutoCategorize, latest.AutoCategorize),
            ShowAll = MergeScalar(baseline.ShowAll, current.ShowAll, latest.ShowAll),
            TmdbToken = MergeScalar(baseline.TmdbToken, current.TmdbToken, latest.TmdbToken) ?? "",
            EnableExtension = MergeScalar(baseline.EnableExtension, current.EnableExtension, latest.EnableExtension),
            AutoStart = MergeScalar(baseline.AutoStart, current.AutoStart, latest.AutoStart),
            MinimizeToTray = MergeScalar(baseline.MinimizeToTray, current.MinimizeToTray, latest.MinimizeToTray),
            StartMinimized = MergeScalar(baseline.StartMinimized, current.StartMinimized, latest.StartMinimized),
            StartPage = NormalizeStartupPage(MergeScalar(baseline.StartPage, current.StartPage, latest.StartPage)),
            GpuThumbnails = MergeScalar(baseline.GpuThumbnails, current.GpuThumbnails, latest.GpuThumbnails),
            AiEndpoint = MergeScalar(baseline.AiEndpoint, current.AiEndpoint, latest.AiEndpoint) ?? "",
            AiModel = MergeScalar(baseline.AiModel, current.AiModel, latest.AiModel) ?? "",
            AiKey = MergeScalar(baseline.AiKey, current.AiKey, latest.AiKey) ?? "",
            AiDailyBudgetUsd = MergeScalar(baseline.AiDailyBudgetUsd, current.AiDailyBudgetUsd, latest.AiDailyBudgetUsd),
            AiInputUsdPerMillion = MergeScalar(baseline.AiInputUsdPerMillion, current.AiInputUsdPerMillion, latest.AiInputUsdPerMillion),
            AiOutputUsdPerMillion = MergeScalar(baseline.AiOutputUsdPerMillion, current.AiOutputUsdPerMillion, latest.AiOutputUsdPerMillion),
            AiMaxOutputTokens = MergeScalar(baseline.AiMaxOutputTokens, current.AiMaxOutputTokens, latest.AiMaxOutputTokens),
            AiRequestIntervalMs = MergeScalar(baseline.AiRequestIntervalMs, current.AiRequestIntervalMs, latest.AiRequestIntervalMs),
            AiLocalRequestIntervalMs = MergeScalar(baseline.AiLocalRequestIntervalMs, current.AiLocalRequestIntervalMs, latest.AiLocalRequestIntervalMs),
            AiCloudRequestIntervalMs = MergeScalar(baseline.AiCloudRequestIntervalMs, current.AiCloudRequestIntervalMs, latest.AiCloudRequestIntervalMs),
            AiLocalConcurrency = MergeScalar(baseline.AiLocalConcurrency, current.AiLocalConcurrency, latest.AiLocalConcurrency),
            AiCloudConcurrency = MergeScalar(baseline.AiCloudConcurrency, current.AiCloudConcurrency, latest.AiCloudConcurrency),
            AiLocalRetryCount = MergeScalar(baseline.AiLocalRetryCount, current.AiLocalRetryCount, latest.AiLocalRetryCount),
            AiCloudRetryCount = MergeScalar(baseline.AiCloudRetryCount, current.AiCloudRetryCount, latest.AiCloudRetryCount)
        };
        if (merged.Folders.Count > 0) merged.FoldersClearedByUser = false;
        return merged;
    }

    static T MergeScalar<T>(T baseline, T current, T latest) =>
        EqualityComparer<T>.Default.Equals(baseline, current) ? latest : current;

    static List<string> CleanList(IEnumerable<string>? values, IEqualityComparer<string> comparer) =>
        (values ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(comparer).ToList();

    static List<string> MergeList(IEnumerable<string>? baseline, IEnumerable<string>? current,
        IEnumerable<string>? latest, IEqualityComparer<string> comparer, bool prioritizeRecent = false, int limit = 0)
    {
        var before = CleanList(baseline, comparer);
        var ours = CleanList(current, comparer);
        var theirs = CleanList(latest, comparer);
        if (before.SequenceEqual(ours, comparer)) return limit > 0 ? theirs.Take(limit).ToList() : theirs;

        var beforeSet = new HashSet<string>(before, comparer);
        var ourSet = new HashSet<string>(ours, comparer);
        var theirSet = new HashSet<string>(theirs, comparer);
        var result = new List<string>();
        var resultSet = new HashSet<string>(comparer);
        void Add(string value) { if (resultSet.Add(value)) result.Add(value); }

        if (prioritizeRecent)
        {
            // A repeated search moves to the front, while new searches from a
            // second instance stay ahead of the older shared history.
            foreach (var value in ours.Where(value => !beforeSet.Contains(value))) Add(value);
            foreach (var value in theirs.Where(value => !beforeSet.Contains(value))) Add(value);
            foreach (var value in ours.Where(value => beforeSet.Contains(value) && theirSet.Contains(value))) Add(value);
        }
        else
        {
            // Keep external deletions; remove only entries deleted locally;
            // then append this instance's additions.
            foreach (var value in theirs.Where(value => !beforeSet.Contains(value) || ourSet.Contains(value))) Add(value);
            foreach (var value in ours.Where(value => !beforeSet.Contains(value))) Add(value);
        }
        return limit > 0 ? result.Take(limit).ToList() : result;
    }

    static Dictionary<string, T> CleanMap<T>(IDictionary<string, T>? source, IEqualityComparer<string> comparer)
    {
        var result = new Dictionary<string, T>(comparer);
        if (source is null) return result;
        foreach (var (key, value) in source)
            if (!string.IsNullOrWhiteSpace(key) && value is not null) result[key] = value;
        return result;
    }

    static bool ValuesEqual<T>(T first, T second)
    {
        // Most libraries have thousands of simple category strings. Avoid
        // serializing every unchanged value on each small setting update.
        if (EqualityComparer<T>.Default.Equals(first, second)) return true;
        if (first is string || second is string) return false;
        return JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    }

    static Dictionary<string, T> MergeMap<T>(IDictionary<string, T>? baseline, IDictionary<string, T>? current,
        IDictionary<string, T>? latest, IEqualityComparer<string> comparer)
    {
        var before = CleanMap(baseline, comparer);
        var ours = CleanMap(current, comparer);
        var merged = CleanMap(latest, comparer);
        foreach (var key in before.Keys.Concat(ours.Keys).Distinct(comparer))
        {
            var hadBefore = before.TryGetValue(key, out var oldValue);
            var hasNow = ours.TryGetValue(key, out var newValue);
            if (hadBefore == hasNow && (!hasNow || ValuesEqual(oldValue, newValue))) continue;
            // A delayed automatic scrape from another window must not undo a
            // human approval or rejection that was saved in the meantime.
            if (hasNow && newValue is AiReviewRecord incomingReview
                && merged.TryGetValue(key, out var diskValue) && diskValue is AiReviewRecord savedReview
                && string.Equals(savedReview.DecisionSource, "manual", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(incomingReview.DecisionSource, "manual", StringComparison.OrdinalIgnoreCase)) continue;
            if (hasNow) merged[key] = newValue!;
            else merged.Remove(key);
        }
        return merged;
    }

    static Dictionary<string, List<string>> MergeListMap(IDictionary<string, List<string>>? baseline,
        IDictionary<string, List<string>>? current, IDictionary<string, List<string>>? latest,
        IEqualityComparer<string> keyComparer, IEqualityComparer<string> valueComparer, int limit = 0)
    {
        var before = CleanMap(baseline, keyComparer);
        var ours = CleanMap(current, keyComparer);
        var theirs = CleanMap(latest, keyComparer);
        var merged = new Dictionary<string, List<string>>(keyComparer);
        foreach (var key in before.Keys.Concat(ours.Keys).Concat(theirs.Keys).Distinct(keyComparer))
        {
            before.TryGetValue(key, out var oldValues);
            ours.TryGetValue(key, out var newValues);
            theirs.TryGetValue(key, out var externalValues);
            var values = MergeList(oldValues, newValues, externalValues, valueComparer, limit: limit);
            if (values.Count > 0) merged[key] = values;
        }
        return merged;
    }
}

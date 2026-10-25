using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class TraktScraperDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Verify()
    {
        using var search = JsonDocument.Parse("""
            [{"type":"movie","score":92.1,"movie":{"title":"The Matrix","year":1999,
              "ids":{"trakt":481,"slug":"the-matrix-1999","imdb":"tt0133093","tmdb":603}}},
             {"type":"show","show":{"title":"Breaking Bad","year":2008,
              "ids":{"trakt":1388,"slug":"breaking-bad","imdb":"tt0903747","tmdb":1396}}}]
            """);
        var candidates = TraktScraper.ParseSearch(search.RootElement);
        var candidate = candidates[0];
        using var detail = JsonDocument.Parse("""
            {"title":"The Matrix","year":1999,"released":"1999-03-31","runtime":136,
             "overview":"A hacker learns the truth.","country":"us","language":"en",
             "genres":["action","science-fiction"],"rating":8.65,"votes":12345,
             "ids":{"trakt":481,"slug":"the-matrix-1999","imdb":"tt0133093","tmdb":603}}
            """);
        var full = TraktScraper.ParseDetail(candidate, detail.RootElement);
        var rating = full.PlatformRatings.SingleOrDefault();
        static bool Reject(MovieMetadata candidate, string json)
        {
            using var document = JsonDocument.Parse(json);
            try { _ = TraktScraper.ParseDetail(candidate, document.RootElement); return false; }
            catch (InvalidDataException) { return true; }
        }
        var wrongId = Reject(candidate, """
            {"title":"The Matrix","year":1999,"ids":{"trakt":482,"slug":"the-matrix-1999","imdb":"tt0133093","tmdb":603}}
            """);
        var wrongSlug = Reject(candidate, """
            {"title":"The Matrix","year":1999,"ids":{"trakt":481,"slug":"another-matrix","imdb":"tt0133093","tmdb":603}}
            """);
        var wrongImdb = Reject(candidate, """
            {"title":"The Matrix","year":1999,"ids":{"trakt":481,"slug":"the-matrix-1999","imdb":"tt0000001","tmdb":603}}
            """);
        var wrongTmdb = Reject(candidate, """
            {"title":"The Matrix","year":1999,"ids":{"trakt":481,"slug":"the-matrix-1999","imdb":"tt0133093","tmdb":604}}
            """);
        var wrongYear = Reject(candidate, """
            {"title":"The Matrix","year":2000,"ids":{"trakt":481,"slug":"the-matrix-1999","imdb":"tt0133093","tmdb":603}}
            """);
        var wrongTitle = Reject(candidate, """
            {"title":"The Other Film","year":1999,"ids":{"trakt":481,"slug":"the-matrix-1999","imdb":"tt0133093","tmdb":603}}
            """);
        using var malformed = JsonDocument.Parse("""{"results":[]}""");
        var malformedRejected = false;
        try { _ = TraktScraper.ParseSearch(malformed.RootElement); }
        catch (InvalidDataException) { malformedRejected = true; }
        using var noResults = JsonDocument.Parse("[]");
        var noResultsCorrect = TraktScraper.ParseSearch(noResults.RootElement).Count == 0;
        var badCache = full.Clone();
        badCache.ExternalIds["trakt"] = "999";
        var missingId = full.Clone();
        missingId.ExternalIds.Clear();
        var invalidHost = candidate.Clone();
        invalidHost.SourceUrl = "https://trakt.tv.evil.test/movies/the-matrix-1999";
        var statusConfig = new LibraryConfig { EnableTrakt = true };
        var status = SourceHealthDiagnostics.Build(statusConfig,
            new Dictionary<string, SourceHealthDiagnostics.BackoffState>(),
            new SourceHealthDiagnostics.PaidBudgetState("尚未使用", 0, 3, "2026-10-25"),
            "尚无冷却记录", DateTimeOffset.Parse("2026-10-25T00:00:00Z"),
            traktCredentialReady: false);
        var redacted = SourceHealthDiagnostics.ToRedactedJson(status);
        bool bulkBlocked;
        try { _ = MainWindow.PlanCandidateSources(13, bulk: true); bulkBlocked = false; }
        catch (InvalidOperationException) { bulkBlocked = true; }
        bool missingCredentialBlocked;
        try { MainWindow.RequireManualTrakt(false, true, false); missingCredentialBlocked = false; }
        catch (InvalidOperationException) { missingCredentialBlocked = true; }
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["traktSearchKeepsMovieAndShowStableIds"] = candidates.Count == 2 &&
                candidate.SourceUrl == "https://trakt.tv/movies/the-matrix-1999" &&
                candidate.ExternalIds["trakt"] == "481" && candidate.ExternalIds["imdb"] == "tt0133093" &&
                candidate.ExternalIds["tmdb"] == "603" && candidates[1].SourceUrl == "https://trakt.tv/shows/breaking-bad",
            ["traktDetailChecksIdsTitleYearAndPreservesRating"] = full.DetailFetched && full.DetailIdentityVerified &&
                full.ReleaseDate == "1999-03-31" && full.RuntimeMinutes == 136 &&
                full.ExternalIds["trakt"] == "481" && rating is { Platform: "Trakt", Maximum: 10, VoteCount: 12345 } &&
                Math.Abs(rating.Value - 8.65) < 0.001 &&
                MainWindow.RatingsFromVerifiedDetail(full).Single().Platform == "Trakt",
            ["traktRejectsIdentityConflicts"] = wrongId && wrongSlug && wrongImdb && wrongTmdb && wrongYear && wrongTitle,
            ["traktCachedDetailMustKeepSameIdentity"] = ScrapeCandidateCache.DetailMatchesCandidate(candidate, full) &&
                !ScrapeCandidateCache.DetailMatchesCandidate(candidate, badCache) &&
                !ScrapeCandidateCache.DetailMatchesCandidate(candidate, missingId) &&
                !TraktScraper.TryCandidateIdentity(invalidHost, out _, out _, out _) &&
                !ScrapeCandidateCache.CandidateMatchesCacheSource("Trakt", invalidHost),
            ["traktSourceErrorsAreDistinctFromNoResults"] = malformedRejected && noResultsCorrect,
            ["traktNeverEntersBulkQueue"] = bulkBlocked && MainWindow.PlanCandidateSources(13, false).SequenceEqual([13]) &&
                !MainWindow.PlanCandidateSources(3, false).Contains(13) &&
                MainWindow.FilterTitleQueueCandidates([candidate]).Count == 0,
            ["traktCredentialAndSourceDefaultOff"] = !new LibraryConfig().EnableTrakt &&
                !TraktScraper.ValidClientId("short") && TraktScraper.ValidClientId(new string('a', 64)) &&
                missingCredentialBlocked && status.Sources.Single(source => source.Name == "Trakt").Status == "缺少凭据" &&
                !redacted.Contains(new string('a', 64), StringComparison.Ordinal),
            ["traktRatingsShowExactSourceLink"] = MainWindow.TryRatingSourceUrl(candidate.SourceUrl, out _) &&
                !MainWindow.TryRatingSourceUrl("https://trakt.tv.evil.test/movies/the-matrix-1999", out _)
        };
    }
}

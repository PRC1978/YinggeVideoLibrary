using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class KitsuScraperDiagnostics
{
    const string SearchJson = """
        {"data":[
          {"type":"anime","id":"1","attributes":{"canonicalTitle":"Cowboy Bebop","titles":{"ja_jp":"カウボーイビバップ"},"startDate":"1998-04-03","averageRating":"82.28","posterImage":{"large":"https://media.kitsu.app/anime/poster_images/1/large.jpg"}}},
          {"type":"manga","id":"2","attributes":{"canonicalTitle":"Not anime"}}
        ]}
        """;

    const string DetailJson = """
        {"data":{"type":"anime","id":"1","attributes":{"canonicalTitle":"Cowboy Bebop","titles":{"ja_jp":"カウボーイビバップ"},"startDate":"1998-04-03","subtype":"TV","description":"Space adventures","episodeLength":24,"averageRating":"82.28","ratingFrequencies":{"2":"10","16":"290"},"posterImage":{"large":"https://media.kitsu.app/anime/poster_images/1/large.jpg"},"youtubeVideoId":"0123456789A"}},
         "included":[{"type":"genres","id":"1","attributes":{"name":"Action"}},{"type":"genres","id":"2","attributes":{"name":"Action"}},{"type":"genres","id":"3","attributes":{"name":"Science Fiction"}}]}
        """;

    internal static IReadOnlyDictionary<string, bool> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        using var searchDocument = JsonDocument.Parse(SearchJson);
        using var detailDocument = JsonDocument.Parse(DetailJson);
        var found = KitsuScraper.ParseSearch(searchDocument.RootElement);
        var candidate = found.Single();
        var detail = KitsuScraper.ParseDetail(candidate, detailDocument.RootElement);
        var score = detail.PlatformRatings?.SingleOrDefault(rating => rating.Platform == "Kitsu");
        var noResults = false;
        var malformedFails = false;
        using (var empty = JsonDocument.Parse("""{"data":[]}"""))
            noResults = KitsuScraper.ParseSearch(empty.RootElement).Count == 0;
        using (var malformed = JsonDocument.Parse("""{"errors":[{"detail":"unavailable"}]}"""))
        {
            try { _ = KitsuScraper.ParseSearch(malformed.RootElement); }
            catch (InvalidDataException) { malformedFails = true; }
        }

        bool Reject(MovieMetadata selected, string response)
        {
            using var document = JsonDocument.Parse(response);
            try { _ = KitsuScraper.ParseDetail(selected, document.RootElement); return false; }
            catch (InvalidDataException) { return true; }
        }
        var wrongSource = candidate.Clone(); wrongSource.Source = "TVmaze";
        var wrongHost = candidate.Clone(); wrongHost.SourceUrl = "https://kitsu.io.evil.test/api/edge/anime/1";
        var wrongCode = candidate.Clone(); wrongCode.Code = "Q123";
        var badTitle = DetailJson.Replace("\"canonicalTitle\":\"Cowboy Bebop\"",
            "\"canonicalTitle\":\"Another Work\"", StringComparison.Ordinal);
        var badYear = DetailJson.Replace("1998-04-03", "2001-04-03", StringComparison.Ordinal);
        var badId = DetailJson.Replace("\"id\":\"1\"", "\"id\":\"2\"", StringComparison.Ordinal);
        var queueDefaults = MainWindow.PlanQueueTitleSources(true, true, true, true, true, true, true);
        var queueOptIn = MainWindow.PlanQueueTitleSources(true, true, true, true, true, true, true, kitsu: true);
        var sourceRetryAt = ScrapeQueueStore.RecordSourceFailure(directory, "Kitsu", "HTTP 503", false,
            TimeSpan.FromSeconds(75));
        var rateLimitDirectory = Path.Combine(directory, "rate-limit");
        var rateLimitRetryAt = ScrapeQueueStore.RecordSourceFailure(rateLimitDirectory, "Kitsu",
            "HTTP 429", true, TimeSpan.FromSeconds(90));
        var otherSourceReady = ScrapeQueueStore.GetSourceReadyAt(directory, "TVmaze") <= DateTimeOffset.UtcNow &&
            ScrapeQueueStore.GetSourceReadyAt(directory, "TMDB") <= DateTimeOffset.UtcNow;
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["kitsuSearchOnlyOffersAnimeWithStableId"] = found.Count == 1 &&
                candidate.Source == "Kitsu" && candidate.Code.Length == 0 &&
                candidate.SourceUrl == "https://kitsu.io/api/edge/anime/1" &&
                candidate.OriginalTitle == "カウボーイビバップ" &&
                !candidate.DetailFetched && !candidate.DetailIdentityVerified,
            ["kitsuDetailChecksIdTitleYearAndFields"] = detail.DetailFetched && detail.DetailIdentityVerified &&
                detail.Code.Length == 0 && detail.SourceUrl == candidate.SourceUrl &&
                detail.ReleaseDate == "1998-04-03" && detail.RuntimeMinutes == 24 &&
                detail.Genres.SequenceEqual(["Action", "Science Fiction"]) &&
                detail.TrailerUrl == "https://www.youtube.com/watch?v=0123456789A" &&
                Reject(wrongSource, DetailJson) && Reject(wrongHost, DetailJson) &&
                Reject(wrongCode, DetailJson) &&
                Reject(candidate, badTitle) && Reject(candidate, badYear) && Reject(candidate, badId) &&
                KitsuScraper.TryAnimeId(candidate.SourceUrl, out var id) && id == 1 &&
                !KitsuScraper.TryAnimeId("http://kitsu.io/api/edge/anime/1", out _) &&
                !KitsuScraper.TryAnimeId("https://kitsu.io/api/edge/anime/1?other=1", out _),
            ["kitsuOfficialPercentageAndVotesAreIndependent"] = detail.Rating == "82.28%" &&
                score is { Value: 82.28, Maximum: 100, VoteCount: 300 } &&
                score.SourceUrl == candidate.SourceUrl && score.CollectedAt > DateTime.UtcNow.AddMinutes(-2) &&
                RatingAggregation.Summarize(detail.PlatformRatings) is null &&
                RatingAggregation.Summarize([score!, new PlatformRating { Platform = "TVmaze", Value = 7, Maximum = 10 }])?.SourceCount == 2,
            ["kitsuNoResultsDistinctFromApiFailure"] = noResults && malformedFails,
            ["kitsuSourceRetryDoesNotCoolTvMazeOrTmdb"] =
                sourceRetryAt > DateTimeOffset.UtcNow.AddSeconds(70) &&
                rateLimitRetryAt > DateTimeOffset.UtcNow.AddSeconds(85) && otherSourceReady,
            ["kitsuDefaultOffButExplicitQueueOptIn"] = !new LibraryConfig().EnableKitsu &&
                MainWindow.PlanCandidateSources(11, bulk: false).SequenceEqual([11]) &&
                !queueDefaults.Contains("Kitsu") && queueOptIn.Count(source => source == "Kitsu") == 1 &&
                MainWindow.FilterTitleQueueCandidates([candidate]).SingleOrDefault()?.Source == "Kitsu" &&
                ScrapeSourcePolicy.DefaultIntervalFor("Kitsu", 1) == 5 &&
                ScrapeSourcePolicy.DefaultIntervalFor("TVmaze", 1) == 1,
            ["kitsuCachedDetailIdentityRemainsBoundToCandidate"] =
                ScrapeCandidateCache.SameCandidateIdentity(candidate, detail) &&
                !ScrapeCandidateCache.SameCandidateIdentity(wrongCode, detail)
        };
    }
}

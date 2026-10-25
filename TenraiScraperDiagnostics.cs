using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class TenraiScraperDiagnostics
{
    const string SearchJson = """
        {"data":[
          {"mal_id":1,"url":"https://myanimelist.net/anime/1/Cowboy_Bebop","title":"Cowboy Bebop","title_japanese":"カウボーイビバップ","year":1998,"score":8.75,"scored_by":1000,
           "aired":{"from":"1998-04-03T00:00:00+00:00"},"images":{"jpg":{"large_image_url":"https://cdn.myanimelist.net/images/anime/1/1.jpg"}}},
          {"mal_id":2,"url":"https://myanimelist.net/anime/999/Not_This","title":"Wrong identity"}
        ],"pagination":{"has_next_page":false}}
        """;

    const string DetailJson = """
        {"data":{"mal_id":1,"url":"https://myanimelist.net/anime/1/Cowboy_Bebop","title":"Cowboy Bebop",
          "title_japanese":"カウボーイビバップ","year":1998,"type":"TV","score":8.75,"scored_by":1000,
          "aired":{"from":"1998-04-03T00:00:00+00:00"},"duration":"24 min per ep","synopsis":"Space adventures",
          "images":{"jpg":{"large_image_url":"https://cdn.myanimelist.net/images/anime/1/1.jpg"}},
          "trailer":{"youtube_id":"0123456789A"},
          "studios":[{"name":"Sunrise"}],"genres":[{"name":"Action"},{"name":"Sci-Fi"}],"themes":[{"name":"Space"}]}}
        """;

    internal static IReadOnlyDictionary<string, bool> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        using var searchDocument = JsonDocument.Parse(SearchJson);
        using var detailDocument = JsonDocument.Parse(DetailJson);
        var found = TenraiScraper.ParseSearch(searchDocument.RootElement);
        var candidate = found.Single();
        var detail = TenraiScraper.ParseDetail(candidate, detailDocument.RootElement);
        var rating = detail.PlatformRatings?.SingleOrDefault(item => item.Platform == "MyAnimeList");
        var noResults = false;
        var malformedFails = false;
        using (var empty = JsonDocument.Parse("""{"data":[]}"""))
            noResults = TenraiScraper.ParseSearch(empty.RootElement).Count == 0;
        using (var malformed = JsonDocument.Parse("""{"status":503,"message":"offline"}"""))
        {
            try { _ = TenraiScraper.ParseSearch(malformed.RootElement); }
            catch (InvalidDataException) { malformedFails = true; }
        }

        bool Reject(MovieMetadata selected, string response)
        {
            using var document = JsonDocument.Parse(response);
            try { _ = TenraiScraper.ParseDetail(selected, document.RootElement); return false; }
            catch (InvalidDataException) { return true; }
        }

        var wrongSource = candidate.Clone(); wrongSource.Source = "Bangumi";
        var wrongHost = candidate.Clone(); wrongHost.SourceUrl = "https://myanimelist.net.evil.test/anime/1";
        var wrongCode = candidate.Clone(); wrongCode.Code = "ABC-123";
        var wrongTitle = DetailJson.Replace("\"title\":\"Cowboy Bebop\"",
            "\"title\":\"Another Work\"", StringComparison.Ordinal);
        var wrongYear = DetailJson.Replace("1998-04-03", "2001-04-03", StringComparison.Ordinal)
            .Replace("\"year\":1998", "\"year\":2001", StringComparison.Ordinal);
        var wrongId = DetailJson.Replace("\"mal_id\":1", "\"mal_id\":2", StringComparison.Ordinal);
        var queueDefaults = MainWindow.PlanQueueTitleSources(true, true, true, true, true, true, true, true);
        var queueOptIn = MainWindow.PlanQueueTitleSources(true, true, true, true, true, true, true, true,
            tenrai: true);
        var sourceRetryAt = ScrapeQueueStore.RecordSourceFailure(directory, "Tenrai", "HTTP 503", false,
            TimeSpan.FromSeconds(75));
        var otherSourceReady = ScrapeQueueStore.GetSourceReadyAt(directory, "TVmaze") <= DateTimeOffset.UtcNow;
        var threwPath = false;
        try { _ = TenraiScraper.NormalizeQuery(@"C:\media\show"); }
        catch (ArgumentException) { threwPath = true; }
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["tenraiSearchKeepsStableMalIdentity"] = found.Count == 1 && candidate.Source == "Tenrai" &&
                candidate.SourceUrl == "https://myanimelist.net/anime/1" && candidate.Code.Length == 0 &&
                candidate.OriginalTitle == "カウボーイビバップ" && !candidate.DetailFetched &&
                !candidate.DetailIdentityVerified,
            ["tenraiDetailRequiresSameIdTitleAndYear"] = detail.DetailFetched && detail.DetailIdentityVerified &&
                detail.ReleaseDate == "1998-04-03" && detail.RuntimeMinutes == 24 &&
                detail.Studio == "Sunrise" && detail.Genres.SequenceEqual(["Action", "Sci-Fi", "Space"]) &&
                detail.TrailerUrl == "https://www.youtube.com/watch?v=0123456789A" &&
                Reject(wrongSource, DetailJson) && Reject(wrongHost, DetailJson) &&
                Reject(wrongCode, DetailJson) && Reject(candidate, wrongTitle) &&
                Reject(candidate, wrongYear) && Reject(candidate, wrongId),
            ["tenraiRatingDisclosesMalViaTenrai"] = detail.Rating == "8.75/10" &&
                rating is { Value: 8.75, Maximum: 10, VoteCount: 1000, Platform: "MyAnimeList" } &&
                rating.SourceUrl == candidate.SourceUrl &&
                rating.MatchEvidence.Contains("Tenrai", StringComparison.Ordinal) &&
                detail.FieldProvenance[nameof(MovieMetadata.Rating)].Stage.Contains("Tenrai", StringComparison.Ordinal) &&
                RatingAggregation.CanonicalPlatform("Tenrai") == "MyAnimeList" &&
                RatingAggregation.Summarize(detail.PlatformRatings) is null,
            ["tenraiMalformedResponseIsNotNoMatch"] = noResults && malformedFails,
            ["tenraiRejectsUntrustedUrlsAndFilePaths"] = threwPath &&
                TenraiScraper.TryAnimeId(candidate.SourceUrl, out var id) && id == 1 &&
                !TenraiScraper.TryAnimeId("http://myanimelist.net/anime/1", out _) &&
                !TenraiScraper.TryAnimeId("https://myanimelist.net.evil.test/anime/1", out _) &&
                !TenraiScraper.TryAnimeId("https://myanimelist.net/anime/1?admin=true", out _),
            ["tenraiDefaultOffAndQueueOptIn"] = !new LibraryConfig().EnableTenrai &&
                MainWindow.PlanCandidateSources(12, bulk: false).SequenceEqual([12]) &&
                !queueDefaults.Contains("Tenrai") && queueOptIn.Count(item => item == "Tenrai") == 1 &&
                MainWindow.FilterTitleQueueCandidates([candidate]).SingleOrDefault()?.Source == "Tenrai" &&
                ScrapeSourcePolicy.DefaultIntervalFor("Tenrai", 1) == 5 &&
                sourceRetryAt > DateTimeOffset.UtcNow.AddSeconds(70) && otherSourceReady,
            ["tenraiCachedDetailBoundToChosenCandidate"] =
                ScrapeCandidateCache.SameCandidateIdentity(candidate, detail) &&
                !ScrapeCandidateCache.SameCandidateIdentity(wrongCode, detail)
        };
    }
}

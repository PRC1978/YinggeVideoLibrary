using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class OmdbScraperDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Verify()
    {
        const string searchJson = """
            {"Response":"True","totalResults":"1","Search":[
              {"Title":"Example Film","Year":"2021","imdbID":"tt1234567","Type":"movie","Poster":"N/A"},
              {"Title":"Unrelated Episode","Year":"2021","imdbID":"tt9999999","Type":"episode"}
            ]}
            """;
        const string detailJson = """
            {"Response":"True","Title":"Example Film","Year":"2021","Rated":"PG-13",
             "Released":"06 Oct 2021","Runtime":"112 min","Genre":"Drama, Mystery",
             "Director":"A Director","Actors":"First Actor, Second Actor","Plot":"A verified story.",
             "Language":"English","Country":"United States","Production":"A Studio",
             "imdbRating":"7.5","imdbVotes":"1,234","imdbID":"tt1234567","Type":"movie",
             "Ratings":[{"Source":"Internet Movie Database","Value":"7.5/10"},
                        {"Source":"Rotten Tomatoes","Value":"80%"},
                        {"Source":"Metacritic","Value":"65/100"}]}
            """;
        using var searchDoc = JsonDocument.Parse(searchJson);
        using var detailDoc = JsonDocument.Parse(detailJson);
        var found = OmdbScraper.ParseSearch(searchDoc.RootElement);
        var candidate = found.Single();
        var detail = OmdbScraper.ParseDetail(candidate, detailDoc.RootElement);
        var onlyImdb = detail.Clone();
        onlyImdb.PlatformRatings = onlyImdb.PlatformRatings.Where(item => item.Platform == "IMDb").ToList();
        var ratingIdentity = detail.Rating is null && RatingAggregation.FromMetadata(detail) is null &&
            RatingAggregation.Summarize(onlyImdb.PlatformRatings) is null &&
            RatingAggregation.Summarize(detail.PlatformRatings)?.SourceCount == 3;
        var mismatched = detailJson.Replace("tt1234567", "tt7654321", StringComparison.Ordinal);
        bool rejectsWrongId;
        using (var mismatchDoc = JsonDocument.Parse(mismatched))
        {
            try { _ = OmdbScraper.ParseDetail(candidate, mismatchDoc.RootElement); rejectsWrongId = false; }
            catch (InvalidDataException) { rejectsWrongId = true; }
        }
        var wrongSource = candidate.Clone(); wrongSource.Source = "TVmaze";
        var wrongHost = candidate.Clone(); wrongHost.SourceUrl = "https://www.imdb.com.evil.test/title/tt1234567/";
        bool rejectsWrongSource;
        bool rejectsWrongHost;
        try { _ = OmdbScraper.ParseDetail(wrongSource, detailDoc.RootElement); rejectsWrongSource = false; }
        catch (InvalidDataException) { rejectsWrongSource = true; }
        try { _ = OmdbScraper.ParseDetail(wrongHost, detailDoc.RootElement); rejectsWrongHost = false; }
        catch (InvalidDataException) { rejectsWrongHost = true; }
        bool blocksBulk;
        try { _ = MainWindow.PlanCandidateSources(10, bulk: true); blocksBulk = false; }
        catch (InvalidOperationException) { blocksBulk = true; }
        bool failsWithoutCredential;
        try
        {
            _ = OmdbScraper.RequireKeyValue(null);
            failsWithoutCredential = false;
        }
        catch (InvalidOperationException ex)
        {
            failsWithoutCredential = ex.Message.Contains("API Key", StringComparison.Ordinal);
        }
        bool manualGate;
        try { MainWindow.RequireManualOmdb(bulk: false, enabled: true, credentialReady: false); manualGate = false; }
        catch (InvalidOperationException) { manualGate = true; }
        try { MainWindow.RequireManualOmdb(bulk: false, enabled: false, credentialReady: true); manualGate = false; }
        catch (InvalidOperationException) { manualGate &= true; }
        try { MainWindow.RequireManualOmdb(bulk: false, enabled: true, credentialReady: true); }
        catch { manualGate = false; }
        try { MainWindow.RequireManualOmdb(bulk: true, enabled: true, credentialReady: true); manualGate = false; }
        catch (InvalidOperationException) { manualGate &= true; }
        var queueRejectsOmdb = MainWindow.PlanQueueTitleSources(true, true, true, true, true, true, true)
            .All(source => source != "OMDb") &&
            MainWindow.FilterTitleQueueCandidates([candidate, new MovieMetadata { Source = "TVmaze" }])
                .All(item => item.Source != "OMDb") &&
            !MainWindow.PlanCandidateSources(3, bulk: true).Contains(10);
        var statusConfig = new LibraryConfig { EnableOmdb = true };
        var statusSnapshot = SourceHealthDiagnostics.Build(statusConfig,
            new Dictionary<string, SourceHealthDiagnostics.BackoffState>(),
            new SourceHealthDiagnostics.PaidBudgetState("尚未使用", 0, 3, "2026-10-25"),
            "尚无冷却记录", DateTimeOffset.Parse("2026-10-25T00:00:00Z"), omdbCredentialReady: false);
        var noKeyStatus = statusSnapshot.Sources.Single(item => item.Name == "OMDb").Status == "缺少凭据" &&
            !new LibraryConfig().EnableOmdb;
        var oldConfig = new LibraryConfig
        {
            SourceTrustWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["TMDB"] = 23 }
        };
        MainWindow.RestoreDefaultSourceTrustWeights(oldConfig);
        var oldConfigDefaults = oldConfig.SourceTrustWeights.GetValueOrDefault("OMDb") == 7 &&
            oldConfig.SourceTrustWeights.GetValueOrDefault("TMDB") == 23 &&
            MainWindow.SourceWeightEditorText(oldConfig.SourceTrustWeights).Contains("OMDb=7", StringComparison.Ordinal);
        bool noMatchIsDistinctFromSourceError;
        using (var noMatch = JsonDocument.Parse("""{"Response":"False","Error":"Movie not found!"}"""))
        {
            try { _ = OmdbScraper.ParseSearch(noMatch.RootElement); noMatchIsDistinctFromSourceError = false; }
            catch (ScrapeNotFoundException) { noMatchIsDistinctFromSourceError = true; }
        }
        bool rejectsRawError;
        using (var failure = JsonDocument.Parse("""{"Response":"False","Error":"apikey=PRIVATE-OMDB-KEY"}"""))
        {
            try { _ = OmdbScraper.ParseSearch(failure.RootElement); rejectsRawError = false; }
            catch (InvalidOperationException ex)
            {
                rejectsRawError = !ex.Message.Contains("PRIVATE-OMDB-KEY", StringComparison.Ordinal);
            }
        }
        var sourceBox = new System.Windows.Controls.ComboBox();
        for (var index = 0; index < 8; index++)
            sourceBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "existing-" + index });
        MainWindow.AppendManualSourceOptions(sourceBox);
        var sourceOrder = sourceBox.Items.Count == 14 &&
            (sourceBox.Items[8] as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()?.StartsWith("Javinfo", StringComparison.Ordinal) == true &&
            (sourceBox.Items[9] as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()?.StartsWith("Wikidata", StringComparison.Ordinal) == true &&
            (sourceBox.Items[10] as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()?.StartsWith("OMDb", StringComparison.Ordinal) == true &&
            (sourceBox.Items[11] as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()?.StartsWith("Kitsu", StringComparison.Ordinal) == true &&
            (sourceBox.Items[12] as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()?.StartsWith("Tenrai", StringComparison.Ordinal) == true &&
            (sourceBox.Items[13] as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()?.StartsWith("Trakt", StringComparison.Ordinal) == true;
        var manualSourceGate = new[] { 0, 1, 2, 4, 5, 6, 7, 9, 11, 12 }.All(index =>
        {
            var blocked = false;
            try { MainWindow.RequireManualSourceEnabled(index, false); }
            catch (InvalidOperationException) { blocked = true; }
            try { MainWindow.RequireManualSourceEnabled(index, true); }
            catch { return false; }
            return blocked;
        });
        var ratingConfig = new LibraryConfig { EnableOmdb = true, EnableKitsu = true, EnableTvMaze = true };
        var ratingChoices = MainWindow.PlanRatingSourceChoices(ratingConfig, omdbCredentialReady: true);
        var ratingOptionsExposeSources = new[] { "TVmaze", "Kitsu", "OMDb" }
            .All(name => ratingChoices.Any(choice => choice.Name == name && choice.Enabled)) &&
            !MainWindow.PlanRatingSourceChoices(ratingConfig, omdbCredentialReady: false)
                .Single(choice => choice.Name == "OMDb").Enabled;
        var relayScores = MainWindow.RatingsFromVerifiedDetail(detail);
        var unverifiedDetail = detail.Clone();
        unverifiedDetail.DetailIdentityVerified = false;
        var kit = new MovieMetadata { Source = "Kitsu", Rating = "82.28%", DetailFetched = true,
            DetailIdentityVerified = true, SourceUrl = "https://kitsu.io/api/edge/anime/1" };
        var tv = new MovieMetadata { Source = "TVmaze", Rating = "8.2/10", DetailFetched = true,
            DetailIdentityVerified = true, SourceUrl = "https://www.tvmaze.com/shows/1" };
        var ratingSelectionPreservesPlatforms = relayScores.Select(score => score.Platform)
                .Order(StringComparer.Ordinal).SequenceEqual(new[] { "IMDb", "Metacritic", "Rotten Tomatoes" }) &&
            relayScores.All(score => score.Platform != "OMDb") &&
            MainWindow.RatingsFromVerifiedDetail(unverifiedDetail).Count == 0 &&
            MainWindow.RatingsFromVerifiedDetail(kit).Single().Maximum == 100 &&
            MainWindow.RatingsFromVerifiedDetail(tv).Single().Maximum == 10;
        var ratingLinksAllowed = MainWindow.TryRatingSourceUrl("https://kitsu.io/api/edge/anime/1", out _) &&
            MainWindow.TryRatingSourceUrl("https://www.imdb.com/title/tt1234567/", out _) &&
            !MainWindow.TryRatingSourceUrl("https://www.imdb.com.evil.test/title/tt1234567/", out _);
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["omdbSearchKeepsOnlyMovieAndSeriesWithPublicIdentity"] = found.Count == 1 &&
                candidate.Source == "OMDb" && candidate.Code.Length == 0 &&
                candidate.SourceUrl == "https://www.imdb.com/title/tt1234567/" &&
                !candidate.SourceUrl.Contains("apikey", StringComparison.OrdinalIgnoreCase),
            ["omdbDetailChecksIdAndMapsBoundedFields"] = detail.DetailFetched && detail.DetailIdentityVerified &&
                detail.SourceUrl == candidate.SourceUrl && detail.RuntimeMinutes == 112 &&
                detail.Actors.SequenceEqual(["First Actor", "Second Actor"]) &&
                detail.ReleaseDate == "2021-10-06" && rejectsWrongId && rejectsWrongSource && rejectsWrongHost,
            ["omdbOriginalRatingPlatformsAreNotDoubleCounted"] = ratingIdentity,
            ["omdbIsManualOnlyAndDoesNotLeakErrors"] = blocksBulk &&
                !MainWindow.PlanCandidateSources(3, bulk: false).Contains(10) && rejectsRawError,
            ["manualSourceOptionsPreserveJavinfoWikidataAndOmdbOrder"] = sourceOrder,
            ["omdbMissingCredentialFailsBeforeRequest"] = failsWithoutCredential && manualGate && noKeyStatus,
            ["omdbOldConfigGetsWeightWithoutOverwritingUserWeights"] = oldConfigDefaults,
            ["omdbNoMatchDistinctFromSourceFailure"] = noMatchIsDistinctFromSourceError && rejectsRawError,
            ["omdbCannotEnterAnyTitleQueuePath"] = queueRejectsOmdb,
            ["manualSourceSwitchBlocksEveryDisabledSource"] = manualSourceGate,
            ["ratingPickerExposesNewManualSources"] = ratingOptionsExposeSources,
            ["ratingPickerNeedsVerifiedIdentityAndKeepsRelayPlatforms"] = ratingSelectionPreservesPlatforms,
            ["ratingSourceLinksAllowKitsuAndImdbOnlyOnExactHosts"] = ratingLinksAllowed
        };
    }
}

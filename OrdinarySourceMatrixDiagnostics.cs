using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Fixed, offline responses exercise the same parsers used by ordinary film,
// series and anime searches. Each column has an independent result so one
// broken provider cannot hide the status of the others.
internal static class OrdinarySourceMatrixDiagnostics
{
    static T Read<T>(string json, Func<JsonElement, T> parse)
    {
        using var document = JsonDocument.Parse(json);
        return parse(document.RootElement);
    }

    static bool Reject<TException>(Action action) where TException : Exception
    {
        try { action(); return false; }
        catch (TException) { return true; }
    }

    internal static IReadOnlyDictionary<string, bool> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var findings = new Dictionary<string, bool>(StringComparer.Ordinal);
        void Check(string source, string caseName, Func<bool> test)
        {
            try { findings[$"ordinary{source}{caseName}"] = test(); }
            catch { findings[$"ordinary{source}{caseName}"] = false; }
        }

        bool SourceCooldown(string source)
        {
            var scope = Path.Combine(directory, source);
            Directory.CreateDirectory(scope);
            var other = source == "TVmaze" ? "TMDB" : "TVmaze";
            var until = ScrapeQueueStore.RecordSourceFailure(scope, source, "HTTP 429", true,
                TimeSpan.FromSeconds(90));
            return until > DateTimeOffset.UtcNow.AddSeconds(80) &&
                ScrapeQueueStore.GetSourceCooldownAt(scope, source) > DateTimeOffset.UtcNow.AddSeconds(80) &&
                ScrapeQueueStore.GetSourceReadyAt(scope, other) <= DateTimeOffset.UtcNow &&
                SourceHealthDiagnostics.ClassifyFailure("HTTP 429") == "请求受限（429）";
        }

        const string tmdbMovie = """
            {"results":[{"id":101,"title":"Example Film","original_title":"Example Film",
             "release_date":"2021-03-04","vote_average":7.8,"vote_count":20}]}
            """;
        const string tmdbDetail = """
            {"id":101,"title":"Example Film","release_date":"2021-03-04","runtime":112,
             "genres":[{"name":"Drama"}],"credits":{"cast":[{"name":"Actor A"}]}}
            """;
        Check("Tmdb", "Success", () =>
        {
            var candidate = Read(tmdbMovie, root => AdditionalScrapers.ParseTmdbSearchResults(root, "movie").Single());
            var detail = Read(tmdbDetail, root => AdditionalScrapers.ParseTmdbDetails(candidate, root, "movie"));
            var series = Read("""{"results":[{"id":202,"name":"Example Series","first_air_date":"2020-01-01"}]}""",
                root => AdditionalScrapers.ParseTmdbSearchResults(root, "tv").Single());
            return !candidate.DetailFetched && !candidate.DetailIdentityVerified &&
                candidate.SourceUrl == "https://www.themoviedb.org/movie/101" &&
                detail.DetailFetched && detail.DetailIdentityVerified && detail.RuntimeMinutes == 112 &&
                detail.Genres.SequenceEqual(["Drama"]) &&
                series.Source == "TMDB 剧集" && series.SourceUrl == "https://www.themoviedb.org/tv/202";
        });
        Check("Tmdb", "NoResult", () => Read("""{"results":[]}""",
            root => AdditionalScrapers.ParseTmdbSearchResults(root, "movie").Count) == 0);
        Check("Tmdb", "RateLimit", () =>
        {
            using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(91));
            var correct = false;
            try { AdditionalScrapers.EnsureSourceSuccess(response, "TMDB"); }
            catch (ScrapeHttpException ex)
            {
                correct = ex.StatusCode == HttpStatusCode.TooManyRequests &&
                    ex.RetryAfter == TimeSpan.FromSeconds(91);
            }
            return correct && SourceCooldown("TMDB");
        });
        Check("Tmdb", "Validation", () =>
        {
            using var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return Reject<InvalidOperationException>(() => AdditionalScrapers.EnsureSourceSuccess(unauthorized, "TMDB")) &&
                MainWindow.PlanCandidateSources(0, bulk: false).SequenceEqual([0]);
        });
        Check("Tmdb", "ParserDrift", () =>
            Reject<InvalidDataException>(() => Read("""{"items":[]}""",
                root => AdditionalScrapers.ParseTmdbSearchResults(root, "movie"))) &&
            Reject<InvalidDataException>(() => Read("""{"results":[{"id":"101","title":"Example Film"}]}""",
                root => AdditionalScrapers.ParseTmdbSearchResults(root, "movie"))));
        Check("Tmdb", "IdentityConflict", () =>
        {
            var candidate = Read(tmdbMovie, root => AdditionalScrapers.ParseTmdbSearchResults(root, "movie").Single());
            return Reject<InvalidDataException>(() => Read("""{"id":102,"title":"Different Film"}""",
                    root => AdditionalScrapers.ParseTmdbDetails(candidate, root, "movie"))) &&
                Reject<InvalidDataException>(() => Read(tmdbDetail,
                    root => AdditionalScrapers.ParseTmdbDetails(candidate, root, "tv")));
        });

        const string tvmazeSearch = """
            [{"show":{"id":123,"name":"Example Show","premiered":"2020-05-06",
              "averageRuntime":42,"genres":["Drama"]}}]
            """;
        const string tvmazeDetail = """
            {"id":123,"name":"Example Show","premiered":"2020-05-06",
             "averageRuntime":42,"rating":{"average":8.1}}
            """;
        Check("TvMaze", "Success", () =>
        {
            var candidate = Read(tvmazeSearch, root => AdditionalScrapers.ParseTvMazeSearchResults(root).Single());
            var detail = Read(tvmazeDetail, root => AdditionalScrapers.ParseTvMazeDetails(candidate, root));
            return !candidate.DetailFetched && !candidate.DetailIdentityVerified &&
                candidate.SourceUrl == "https://www.tvmaze.com/shows/123" &&
                detail.DetailFetched && detail.DetailIdentityVerified &&
                detail.RuntimeMinutes == 42 && detail.PlatformRatings.Single().Platform == "TVmaze";
        });
        Check("TvMaze", "NoResult", () => Read("[]",
            root => AdditionalScrapers.ParseTvMazeSearchResults(root).Count) == 0);
        Check("TvMaze", "RateLimit", () =>
        {
            using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(93));
            var correct = false;
            try { AdditionalScrapers.EnsureSourceSuccess(response, "TVmaze"); }
            catch (ScrapeHttpException ex)
            {
                correct = ex.StatusCode == HttpStatusCode.TooManyRequests &&
                    ex.RetryAfter == TimeSpan.FromSeconds(93);
            }
            return correct && SourceCooldown("TVmaze");
        });
        Check("TvMaze", "Validation", () => MainWindow.PlanCandidateSources(2, bulk: false).SequenceEqual([2]));
        Check("TvMaze", "ParserDrift", () =>
            Reject<InvalidDataException>(() => Read("""{"shows":[]}""",
                root => AdditionalScrapers.ParseTvMazeSearchResults(root))) &&
            Reject<InvalidDataException>(() => Read("""[{"show":{"id":"123","name":"Example Show"}}]""",
                root => AdditionalScrapers.ParseTvMazeSearchResults(root))));
        Check("TvMaze", "IdentityConflict", () =>
        {
            var candidate = Read(tvmazeSearch, root => AdditionalScrapers.ParseTvMazeSearchResults(root).Single());
            return Reject<InvalidDataException>(() => Read("""{"id":124,"name":"Other Show"}""",
                root => AdditionalScrapers.ParseTvMazeDetails(candidate, root)));
        });

        const string bangumiSearch = """{"data":[{"id":31,"type":2,"name":"Example Anime"}]}""";
        const string bangumiDetail = """{"id":31,"type":2,"name":"Example Anime","date":"2022-01-01"}""";
        Check("Bangumi", "Success", () =>
        {
            var candidate = Read(bangumiSearch, root => BangumiScraper.ParseSearchResults(root).Single());
            var detail = Read(bangumiDetail, root => BangumiScraper.ParseDetail(candidate, root));
            return !candidate.DetailFetched && detail.DetailFetched && detail.DetailIdentityVerified &&
                detail.SourceUrl == "https://bgm.tv/subject/31";
        });
        Check("Bangumi", "NoResult", () => Read("""{"data":[]}""",
            root => BangumiScraper.ParseSearchResults(root).Count) == 0);
        Check("Bangumi", "RateLimit", () => SourceCooldown("Bangumi"));
        Check("Bangumi", "Validation", () => MainWindow.PlanCandidateSources(4, bulk: false).SequenceEqual([4]));
        Check("Bangumi", "ParserDrift", () =>
            Reject<JsonException>(() => Read("""{"items":[]}""",
                root => BangumiScraper.ParseSearchResults(root))) &&
            Reject<InvalidDataException>(() => Read("""{"data":[{"type":2,"name":"Example Anime"}]}""",
                root => BangumiScraper.ParseSearchResults(root))));
        Check("Bangumi", "IdentityConflict", () =>
        {
            var candidate = Read(bangumiSearch, root => BangumiScraper.ParseSearchResults(root).Single());
            return Reject<JsonException>(() => Read("""{"id":32,"type":2,"name":"Other Anime"}""",
                root => BangumiScraper.ParseDetail(candidate, root)));
        });

        const string anilistSearch = """
            {"data":{"Page":{"media":[{"id":47,"title":{"english":"Example Anime","native":"例のアニメ"},
              "startDate":{"year":2022,"month":1,"day":1}}]}}}
            """;
        const string anilistDetail = """
            {"data":{"Media":{"id":47,"title":{"english":"Example Anime","native":"例のアニメ"},
              "startDate":{"year":2022,"month":1,"day":1},"averageScore":80}}}
            """;
        Check("AniList", "Success", () =>
        {
            var candidate = Read(anilistSearch, root => AniListScraper.ParseSearchResults(root).Single());
            var detail = Read(anilistDetail, root => AniListScraper.ParseDetail(candidate, root));
            return !candidate.DetailFetched && detail.DetailFetched && detail.DetailIdentityVerified &&
                detail.PlatformRatings.Single().Platform == "AniList";
        });
        Check("AniList", "NoResult", () => Read("""{"data":{"Page":{"media":[]}}}""",
            root => AniListScraper.ParseSearchResults(root).Count) == 0);
        Check("AniList", "RateLimit", () =>
            !MainWindow.PlanQueueTitleSources(true, true, true, true, true, true, true, true)
                .Contains("AniList") &&
            SourceHealthDiagnostics.ClassifyFailure("HTTP 429") == "请求受限（429）");
        Check("AniList", "Validation", () =>
            MainWindow.PlanCandidateSources(6, bulk: false).SequenceEqual([6]) &&
            Reject<InvalidOperationException>(() => MainWindow.PlanCandidateSources(6, bulk: true)) &&
            !AniListScraper.TryMediaId("https://anilist.co.evil.test/anime/47", out _));
        Check("AniList", "ParserDrift", () =>
            Reject<InvalidDataException>(() => Read("""{"data":{"Page":{"items":[]}}}""",
                root => AniListScraper.ParseSearchResults(root))) &&
            Reject<InvalidDataException>(() => Read("""{"data":{"Page":{"media":[{"title":{"english":"Example Anime"}}]}}}""",
                root => AniListScraper.ParseSearchResults(root))));
        Check("AniList", "IdentityConflict", () =>
        {
            var candidate = Read(anilistSearch, root => AniListScraper.ParseSearchResults(root).Single());
            return Reject<InvalidDataException>(() => Read("""{"data":{"Media":{"id":48,"title":{"english":"Other Anime"}}}}""",
                root => AniListScraper.ParseDetail(candidate, root)));
        });

        const string wikidataSearch = """{"search":[{"id":"Q155653","label":"Spirited Away"}]}""";
        const string wikidataEntities = """
            {"entities":{"Q155653":{"id":"Q155653","labels":{"en":{"value":"Spirited Away"}},
              "claims":{"P31":[{"mainsnak":{"datavalue":{"value":{"id":"Q20650540"}}}}]}}}}
            """;
        Check("Wikidata", "Success", () =>
        {
            var hits = Read(wikidataSearch, WikidataScraper.ParseSearchHits);
            var candidate = Read(wikidataEntities,
                root => WikidataScraper.ParseSearchResults(hits, root, "en").Single());
            var detail = Read(wikidataEntities, root => WikidataScraper.ParseDetail(candidate,
                root.GetProperty("entities").GetProperty("Q155653"), null));
            return !candidate.DetailFetched && detail.DetailFetched && detail.DetailIdentityVerified &&
                detail.SourceUrl == "https://www.wikidata.org/wiki/Q155653";
        });
        Check("Wikidata", "NoResult", () => Read("""{"search":[]}""",
            root => WikidataScraper.ParseSearchHits(root).Count) == 0);
        Check("Wikidata", "RateLimit", () => SourceCooldown("Wikidata"));
        Check("Wikidata", "Validation", () =>
            WikidataScraper.TryItemId("https://www.wikidata.org/wiki/Q155653", out _) &&
            !WikidataScraper.TryItemId("https://www.wikidata.org.evil.test/wiki/Q155653", out _));
        Check("Wikidata", "ParserDrift", () =>
            Reject<InvalidDataException>(() => Read("""{"errors":[]}""",
                WikidataScraper.ParseSearchHits)) &&
            Reject<InvalidDataException>(() => Read("""{"search":[{"label":"Spirited Away"}]}""",
                WikidataScraper.ParseSearchHits)));
        Check("Wikidata", "IdentityConflict", () =>
        {
            var hits = Read(wikidataSearch, WikidataScraper.ParseSearchHits);
            var candidate = Read(wikidataEntities, root => WikidataScraper.ParseSearchResults(hits, root, "en").Single());
            return Reject<InvalidDataException>(() => Read("""
                {"id":"Q155654","claims":{"P31":[{"mainsnak":{"datavalue":{"value":{"id":"Q20650540"}}}}]}}
                """, root => WikidataScraper.ParseDetail(candidate, root, null)));
        });

        const string kitsuSearch = """
            {"data":[{"type":"anime","id":"1","attributes":{"canonicalTitle":"Cowboy Bebop",
              "startDate":"1998-04-03","averageRating":"82.28"}}]}
            """;
        const string kitsuDetail = """
            {"data":{"type":"anime","id":"1","attributes":{"canonicalTitle":"Cowboy Bebop",
              "startDate":"1998-04-03","averageRating":"82.28"}}}
            """;
        Check("Kitsu", "Success", () =>
        {
            var candidate = Read(kitsuSearch, root => KitsuScraper.ParseSearch(root).Single());
            var detail = Read(kitsuDetail, root => KitsuScraper.ParseDetail(candidate, root));
            return !candidate.DetailFetched && detail.DetailFetched && detail.DetailIdentityVerified &&
                detail.PlatformRatings.Single().Platform == "Kitsu";
        });
        Check("Kitsu", "NoResult", () => Read("""{"data":[]}""",
            root => KitsuScraper.ParseSearch(root).Count) == 0);
        Check("Kitsu", "RateLimit", () => SourceCooldown("Kitsu"));
        Check("Kitsu", "Validation", () =>
            KitsuScraper.TryAnimeId("https://kitsu.io/api/edge/anime/1", out _) &&
            !KitsuScraper.TryAnimeId("http://kitsu.io/api/edge/anime/1", out _));
        Check("Kitsu", "ParserDrift", () =>
            Reject<InvalidDataException>(() => Read("""{"errors":[]}""",
                KitsuScraper.ParseSearch)) &&
            Reject<InvalidDataException>(() => Read("""{"data":[{"type":"anime","attributes":{"canonicalTitle":"Cowboy Bebop"}}]}""",
                KitsuScraper.ParseSearch)));
        Check("Kitsu", "IdentityConflict", () =>
        {
            var candidate = Read(kitsuSearch, root => KitsuScraper.ParseSearch(root).Single());
            return Reject<InvalidDataException>(() => Read("""
                {"data":{"type":"anime","id":"2","attributes":{"canonicalTitle":"Cowboy Bebop","startDate":"1998-04-03"}}}
                """, root => KitsuScraper.ParseDetail(candidate, root)));
        });

        const string omdbSearch = """
            {"Response":"True","Search":[{"imdbID":"tt1234567","Title":"Example Film","Year":"2021","Type":"movie"}]}
            """;
        const string omdbDetail = """
            {"Response":"True","imdbID":"tt1234567","Title":"Example Film","Year":"2021","Type":"movie",
             "imdbRating":"7.5","imdbVotes":"120"}
            """;
        Check("Omdb", "Success", () =>
        {
            var candidate = Read(omdbSearch, root => OmdbScraper.ParseSearch(root).Single());
            var detail = Read(omdbDetail, root => OmdbScraper.ParseDetail(candidate, root));
            return !candidate.DetailFetched && detail.DetailFetched && detail.DetailIdentityVerified &&
                detail.SourceUrl == "https://www.imdb.com/title/tt1234567/" &&
                detail.PlatformRatings.Single().Platform == "IMDb";
        });
        Check("Omdb", "NoResult", () => Reject<ScrapeNotFoundException>(() => Read(
            """{"Response":"False","Error":"Movie not found!"}""", OmdbScraper.ParseSearch)));
        Check("Omdb", "RateLimit", () =>
        {
            var quotaIsSafe = false;
            try { _ = Read("""
                    {"Response":"False","Error":"Request quota exceeded; apikey=SECRET"}
                    """, OmdbScraper.ParseSearch); }
            catch (InvalidOperationException ex)
            {
                quotaIsSafe = ex.Message.Contains("限额", StringComparison.Ordinal) &&
                    !ex.Message.Contains("SECRET", StringComparison.Ordinal);
            }
            return quotaIsSafe &&
                !MainWindow.PlanQueueTitleSources(true, true, true, true, true, true, true, true).Contains("OMDb");
        });
        Check("Omdb", "Validation", () =>
            OmdbScraper.TryImdbId("https://www.imdb.com/title/tt1234567/", out _) &&
            !OmdbScraper.TryImdbId("https://www.imdb.com.evil.test/title/tt1234567/", out _) &&
            Reject<InvalidOperationException>(() => MainWindow.PlanCandidateSources(10, bulk: true)));
        Check("Omdb", "ParserDrift", () =>
            Reject<InvalidDataException>(() => Read("""{"Response":"True","items":[]}""",
                OmdbScraper.ParseSearch)) &&
            Reject<InvalidDataException>(() => Read("""{"Response":"True","Search":[{"Title":"Example Film"}]}""",
                OmdbScraper.ParseSearch)));
        Check("Omdb", "IdentityConflict", () =>
        {
            var candidate = Read(omdbSearch, root => OmdbScraper.ParseSearch(root).Single());
            return Reject<InvalidDataException>(() => Read("""
                {"Response":"True","imdbID":"tt7654321","Title":"Other Film","Type":"movie"}
                """, root => OmdbScraper.ParseDetail(candidate, root)));
        });
        return findings;
    }
}

using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class WikidataScraperDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Verify()
    {
        var findings = new Dictionary<string, bool>();
        using var search = JsonDocument.Parse("""
            {"search":[
              {"id":"Q155653","label":"Spirited Away"},
              {"id":"Q1079","label":"Breaking Bad"},
              {"id":"Q999","label":"Spirited Away franchise"}]}
            """);
        using var entities = JsonDocument.Parse("""
            {"entities":{
              "Q155653":{"id":"Q155653","labels":{"en":{"value":"Spirited Away"},"ja":{"value":"千と千尋の神隠し"}},
                "descriptions":{"en":{"value":"2001 Japanese animated film"}},
                "claims":{"P31":[{"mainsnak":{"datavalue":{"value":{"id":"Q20650540"}}}}],
                  "P577":[{"mainsnak":{"datavalue":{"value":{"time":"+2001-07-20T00:00:00Z","precision":11}}}}],
                  "P1476":[{"mainsnak":{"datavalue":{"value":{"text":"千と千尋の神隠し","language":"ja"}}}}],
                  "P2047":[{"mainsnak":{"datavalue":{"value":{"amount":"+125","unit":"http://www.wikidata.org/entity/Q7727"}}}}],
                  "P57":[{"mainsnak":{"datavalue":{"value":{"id":"Q55400"}}}}],
                  "P136":[{"mainsnak":{"datavalue":{"value":{"id":"Q157394"}}}}]}},
              "Q1079":{"id":"Q1079","labels":{"en":{"value":"Breaking Bad"}},
                "claims":{"P31":[{"mainsnak":{"datavalue":{"value":{"id":"Q5398426"}}}}]}},
              "Q999":{"id":"Q999","labels":{"en":{"value":"Spirited Away franchise"}},
                "claims":{"P31":[{"mainsnak":{"datavalue":{"value":{"id":"Q196600"}}}}]}}
            }}
            """);
        using var related = JsonDocument.Parse("""
            {"entities":{
              "Q55400":{"id":"Q55400","labels":{"en":{"value":"Hayao Miyazaki"},"zh":{"value":"宫崎骏"}}},
              "Q157394":{"id":"Q157394","labels":{"en":{"value":"fantasy film"},"zh":{"value":"奇幻电影"}}}
            }}
            """);

        var hits = WikidataScraper.ParseSearchHits(search.RootElement);
        var candidates = WikidataScraper.ParseSearchResults(hits, entities.RootElement, "en");
        findings["wikidataSearchFiltersNonWorks"] = candidates.Count == 2 &&
            candidates.Any(item => item.SourceUrl == "https://www.wikidata.org/wiki/Q155653") &&
            candidates.Any(item => item.SourceUrl == "https://www.wikidata.org/wiki/Q1079") &&
            candidates.All(item => !item.DetailFetched && !item.DetailIdentityVerified);
        var detail = WikidataScraper.ParseDetail(candidates.Single(item => item.SourceUrl.EndsWith("Q155653", StringComparison.Ordinal)),
            entities.RootElement.GetProperty("entities").GetProperty("Q155653"), related.RootElement);
        findings["wikidataVerifiedFieldsAndProvenance"] = detail.DetailFetched && detail.DetailIdentityVerified &&
            detail.OriginalTitle == "千と千尋の神隠し" && detail.ReleaseDate == "2001-07-20" &&
            detail.RuntimeMinutes == 125 && detail.Director == "宫崎骏" && detail.Genres.SequenceEqual(["奇幻电影"]) &&
            detail.FieldProvenance.GetValueOrDefault(nameof(MovieMetadata.Director))?.Source == "Wikidata";
        var mismatched = candidates[0].Clone();
        mismatched.SourceUrl = "https://www.wikidata.org/wiki/Q1079";
        try
        {
            _ = WikidataScraper.ParseDetail(mismatched,
                entities.RootElement.GetProperty("entities").GetProperty("Q155653"), related.RootElement);
            findings["wikidataRejectsDifferentIdentity"] = false;
        }
        catch (InvalidDataException) { findings["wikidataRejectsDifferentIdentity"] = true; }
        findings["wikidataRejectsUntrustedDetailUrl"] =
            !WikidataScraper.TryItemId("https://www.wikidata.org.evil.test/wiki/Q155653", out _) &&
            !WikidataScraper.TryItemId("http://www.wikidata.org/wiki/Q155653", out _) &&
            !WikidataScraper.TryItemId("https://www.wikidata.org:8443/wiki/Q155653", out _);
        using var broken = JsonDocument.Parse("""{"error":{"code":"badresponse"}}""");
        try { _ = WikidataScraper.ParseSearchHits(broken.RootElement); findings["wikidataMalformedSearchIsNotNoMatch"] = false; }
        catch (InvalidDataException) { findings["wikidataMalformedSearchIsNotNoMatch"] = true; }
        static bool RefusesPath(string value)
        {
            try { _ = WikidataScraper.NormalizeSearchQuery(value); return false; }
            catch (ArgumentException) { return true; }
        }
        findings["wikidataRejectsLocalPathsBeforeNetwork"] =
            RefusesPath(@"C:\Users\Example\movie.mkv") && RefusesPath("/home/example/movie.mkv") &&
            RefusesPath("file:///C:/movie.mkv") && RefusesPath("https://example.test/movie") &&
            WikidataScraper.NormalizeSearchQuery(" Fate/Zero ") == "Fate/Zero";
        return findings;
    }
}

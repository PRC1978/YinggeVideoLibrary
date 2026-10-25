namespace YinggeVideoLibrary;

internal static class JavDatabaseCatalogDiagnostics
{
    const string MovieUrl = "https://www.javdatabase.com/movies/ABC-123/";
    const string IdolUrl = "https://www.javdatabase.com/idols/performer-one/";
    const string SeriesUrl = "https://www.javdatabase.com/series/example-series/";

    static string Detail(string code = "ABC-123", string actorUrl = IdolUrl, string actorName = "Performer One") =>
        $"<html><h1>{code} Example title</h1>" +
        $"<p class='mb-1'><b>DVD ID: </b>{code}</p>" +
        $"<p class='mb-1'><b>Idol(s)/Actress(es): </b><span><a href='{actorUrl}'>{actorName}</a></span></p>" +
        $"<p class='mb-1'><b>JAV Series: </b><span><a href='{SeriesUrl}'>Example Series</a></span></p>" +
        "<aside><a href='https://www.javdatabase.com/idols/other-person/'>Other Person</a></aside>" +
        new string('x', 550) + "</html>";

    static string CatalogPage(string canonical = IdolUrl, string heading = "Performer One - JAV Profile",
        string code = "DEF-456", string next = IdolUrl + "?ipage=2") =>
        "<html><head><link rel='canonical' href='" + canonical + "'></head><body>" +
        "<h1 class='idol-name'>" + heading + "</h1>" +
        "<div class='card borderlesscard'><p class='pcard'><a href='https://www.javdatabase.com/movies/" + code.ToLowerInvariant() + "/'>" + code + "</a></p>" +
        "<div class='mt-auto'><a href='https://www.javdatabase.com/movies/" + code.ToLowerInvariant() + "/'>Related work</a></div></div>" +
        "<a href='" + next + "'>2</a>" +
        new string('x', 550) + "</body></html>";

    internal static Dictionary<string, bool> Verify()
    {
        var candidate = new MovieMetadata { Code = "ABC-123", Title = "ABC-123", Source = "JAVDatabase", SourceUrl = MovieUrl };
        var links = JavDatabaseCatalog.ExtractVerifiedCatalogLinks(candidate, Detail());
        var link = links.Single(item => item.Kind == "演员");
        var series = links.Single(item => item.Kind == "系列");
        var page = JavDatabaseCatalog.ParseVerifiedCatalogPage(link, link.Url, CatalogPage());
        var secondPage = JavDatabaseCatalog.ParseVerifiedCatalogPage(link, IdolUrl + "?ipage=2",
            CatalogPage(canonical: IdolUrl + "?ipage=2"));
        var seriesPage = JavDatabaseCatalog.ParseVerifiedCatalogPage(series, series.Url,
            CatalogPage(canonical: SeriesUrl, heading: "Example Series JAV Videos", next: SeriesUrl + "?ipage=2"));
        var wrongCode = candidate.Clone(); wrongCode.Code = "ABC-124";
        var wrongSource = candidate.Clone(); wrongSource.Source = "Javinfo · JAVDatabase";
        return new Dictionary<string, bool>
        {
            ["nativeCatalogLinksFromExactDetail"] = links.Count == 2 && link.Name == "Performer One"
                && series.Name == "Example Series" && series.Url == SeriesUrl
                && link.OriginCode == "ABC-123" && link.OriginMovieUrl == MovieUrl,
            ["nativeCatalogSidebarNotRelation"] = links.All(item => !item.Url.Contains("other-person", StringComparison.Ordinal)),
            ["nativeCatalogWrongDetailRejected"] = Rejects(() => JavDatabaseCatalog.ExtractVerifiedCatalogLinks(candidate, Detail("ABC-124")))
                && Rejects(() => JavDatabaseCatalog.ExtractVerifiedCatalogLinks(wrongCode, Detail()))
                && Rejects(() => JavDatabaseCatalog.ExtractVerifiedCatalogLinks(wrongSource, Detail())),
            ["nativeCatalogHostAndQueryBounded"] = !JavDatabaseCatalog.TryCatalogUrl("https://www.javdatabase.com.evil.test/idols/person/", out _, out _)
                && !JavDatabaseCatalog.TryCatalogUrl("https://www.javdatabase.com/series/example/?token=secret", out _, out _)
                && !JavDatabaseCatalog.TryCatalogUrl("https://www.javdatabase.com@evil.test/idols/person/", out _, out _)
                && !JavDatabaseCatalog.TryCatalogPageUrl("https://www.javdatabase.com/idols/performer-one/?ipage=21", link, out _, out _)
                && !JavDatabaseCatalog.TryCatalogPageUrl("https://www.javdatabase.com/idols/another/?ipage=2", link, out _, out _)
                && !JavDatabaseCatalog.TryCatalogPageUrl(SeriesUrl, link, out _, out _),
            ["nativeCatalogPageCandidatesOnly"] = page.Candidates.Count == 1 && page.Candidates[0].Code == "DEF-456"
                && page.Candidates[0].Source == "JAVDatabase" && !page.Candidates[0].DetailFetched
                && !page.Candidates[0].DetailIdentityVerified && page.NextPageUrl == IdolUrl + "?ipage=2"
                && secondPage.Page == 2 && seriesPage.Candidates.Count == 1 && seriesPage.NextPageUrl == SeriesUrl + "?ipage=2",
            ["nativeCatalogPageIdentityRejected"] = Rejects(() => JavDatabaseCatalog.ParseVerifiedCatalogPage(link, link.Url,
                    CatalogPage(canonical: "https://www.javdatabase.com/idols/other-person/")))
                && Rejects(() => JavDatabaseCatalog.ParseVerifiedCatalogPage(link, link.Url,
                    CatalogPage(heading: "Different Person - JAV Profile")))
                && Rejects(() => JavDatabaseCatalog.ParseVerifiedCatalogPage(series, series.Url,
                    CatalogPage(canonical: SeriesUrl, heading: "Different Series JAV Videos")))
                && Rejects(() => JavDatabaseCatalog.ParseVerifiedCatalogPage(link, link.Url,
                    CatalogPage(code: "NOT A CODE"))),
            ["nativeCatalogUnavailableNotEmpty"] = Rejects(() => JavDatabaseCatalog.ParseVerifiedCatalogPage(link,
                link.Url, "<html>Just a moment...</html>"))
        };
    }

    static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (Exception ex) when (ex is InvalidOperationException or ScrapeNotFoundException or ArgumentException) { return true; }
    }
}

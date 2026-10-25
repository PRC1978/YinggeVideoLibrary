namespace YinggeVideoLibrary;

internal static class CrossSourceRelationDiagnostics
{
    const string DatabaseDetailUrl = "https://www.javdatabase.com/movies/ABC-123/";
    const string DatabaseActorUrl = "https://www.javdatabase.com/idols/performer-one/";

    internal static IReadOnlyDictionary<string, bool> Verify()
    {
        var javDbCandidate = new MovieMetadata
        {
            Code = "ABC-123", Title = "作品甲", Source = "JavDB", SourceUrl = "https://javdb.com/v/source123"
        };
        var javDbDetail = JavDbScraper.ParseJavDbDetails(javDbCandidate,
            "<div class='panel-block'><strong>番號:</strong><span class='value'>ABC-123</span></div>" +
            "<div class='panel-block'><strong>演員:</strong><span class='value'><a href='/actors/Av2e'>演員甲</a></span></div>" +
            "<div class='panel-block'><strong>系列:</strong><span class='value'><a href='/series/S2e'>系列甲</a></span></div>" +
            "<div class='panel-block'><strong>類別:</strong><span class='value'><a href='/tags?c3=43'>标签甲</a></span></div>");
        var databaseCandidate = new MovieMetadata
        {
            Code = "ABC-123", Title = "ABC-123", Source = "JAVDatabase", SourceUrl = DatabaseDetailUrl
        };
        var databaseLinks = JavDatabaseCatalog.ExtractVerifiedCatalogLinks(databaseCandidate,
            "<html><h1>ABC-123 Example title</h1>" +
            "<p class='mb-1'><b>DVD ID: </b>ABC-123</p>" +
            "<p class='mb-1'><b>Idol(s)/Actress(es): </b><span><a href='" + DatabaseActorUrl + "'>Performer One</a></span></p>" +
            "<p class='mb-1'><b>JAV Series: </b><span><a href='https://www.javdatabase.com/series/example-series/'>Example Series</a></span></p>" +
            new string('x', 550) + "</html>");
        var actor = databaseLinks.Single(link => link.Kind == "演员");
        var series = databaseLinks.Single(link => link.Kind == "系列");
        var javDbLinks = javDbDetail.CatalogLinks;
        var actorTagSeriesAccepted = javDbLinks.Count == 3 &&
            javDbLinks.All(link => Accepts(() => CrossSourceRelationBrowser.ValidateRelations(javDbDetail, link, actor))) &&
            Accepts(() => CrossSourceRelationBrowser.ValidateRelations(javDbDetail, javDbLinks[0], series));

        var javDbPage = JavDbScraper.ParseCatalogPage(
            "<a class='box card' href='/v/abc123'><img title='作品甲' src='https://c0.jdbstatic.com/a.jpg'><strong>ABC-123</strong></a>" +
            "<a class='box card' href='/v/def456'><img title='作品乙' src='https://c0.jdbstatic.com/b.jpg'><strong>DEF-456</strong></a>");
        var databasePage = JavDatabaseCatalog.ParseVerifiedCatalogPage(actor, actor.Url,
            "<html><head><link rel='canonical' href='" + DatabaseActorUrl + "'></head><body>" +
            "<h1>Performer One - JAV Profile</h1>" +
            "<div class='card borderlesscard'><p class='pcard'><a href='https://www.javdatabase.com/movies/abc-123/'>ABC-123</a></p>" +
            "<div class='mt-auto'><a href='https://www.javdatabase.com/movies/abc-123/'>Example work</a></div></div>" +
            new string('x', 550) + "</body></html>");
        var common = CrossSourceRelationBrowser.IntersectObservedCandidates(javDbPage, databasePage.Candidates);
        var rawAlternative = javDbPage[0].Clone();
        rawAlternative.Code = "abc123";
        var normalized = CrossSourceRelationBrowser.IntersectObservedCandidates([rawAlternative], databasePage.Candidates);
        var duplicate = javDbPage[0].Clone(); duplicate.SourceUrl = "https://javdb.com/v/different123";
        var forged = javDbPage[0].Clone(); forged.SourceUrl = "https://javdb.com.evil.test/v/abc123";
        var identityMismatch = databasePage.Candidates[0].Clone(); identityMismatch.Code = "ABC-124";
        identityMismatch.SourceUrl = "https://www.javdatabase.com/movies/abc-124/";
        var wrongOrigin = javDbDetail.Clone(); wrongOrigin.DetailIdentityVerified = false;
        var injectedLink = javDbLinks[0].Clone(); injectedLink.Url = "https://javdb.com/actors/other";
        var badDatabase = actor with { OriginCode = "ABC-124" };
        var requestedJavDbPages = new List<int>();
        var requestedDatabaseUrls = new List<string>();
        var bounded = CrossSourceRelationBrowser.FetchPreviewCoreAsync(javDbDetail, javDbLinks[0], actor,
            (_, page, _) =>
            {
                requestedJavDbPages.Add(page);
                return Task.FromResult(new JavDbScraper.CatalogPageResult(javDbPage, true));
            },
            (_, url, _) =>
            {
                requestedDatabaseUrls.Add(url);
                var page = requestedDatabaseUrls.Count;
                return Task.FromResult(new JavDatabaseRelationPage(databasePage.Candidates,
                    DatabaseActorUrl + "?ipage=" + (page + 1), page));
            }).GetAwaiter().GetResult();
        var finite = CrossSourceRelationBrowser.FetchPreviewCoreAsync(javDbDetail, javDbLinks[0], actor,
            (_, _, _) => Task.FromResult(new JavDbScraper.CatalogPageResult(javDbPage, false)),
            (_, _, _) => Task.FromResult(new JavDatabaseRelationPage(databasePage.Candidates, null, 1)))
            .GetAwaiter().GetResult();
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["crossSourceVerifiedActorSeriesTagSelection"] = actorTagSeriesAccepted,
            ["crossSourceStrictCodeIntersection"] = common.Count == 1 && common[0].Code == "ABC-123" &&
                common[0].JavDb.SourceUrl == "https://javdb.com/v/abc123" &&
                common[0].JavDatabase.SourceUrl == "https://www.javdatabase.com/movies/abc-123/" &&
                !common[0].JavDb.DetailFetched && !common[0].JavDatabase.DetailFetched,
            ["crossSourceOnlyStandardSeparatorNormalizes"] = normalized.Count == 1 &&
                CrossSourceRelationBrowser.IntersectObservedCandidates(javDbPage, [identityMismatch]).Count == 0,
            ["crossSourceAmbiguousCodeExcluded"] = CrossSourceRelationBrowser.IntersectObservedCandidates(
                [.. javDbPage, duplicate], databasePage.Candidates).Count == 0,
            ["crossSourceHostSpoofExcluded"] = CrossSourceRelationBrowser.IntersectObservedCandidates(
                [forged], databasePage.Candidates).Count == 0,
            ["crossSourceUnverifiedOrInventedRelationRejected"] =
                Rejects(() => CrossSourceRelationBrowser.ValidateRelations(wrongOrigin, javDbLinks[0], actor)) &&
                Rejects(() => CrossSourceRelationBrowser.ValidateRelations(javDbDetail, injectedLink, actor)) &&
                Rejects(() => CrossSourceRelationBrowser.ValidateRelations(javDbDetail, javDbLinks[0], badDatabase)),
            ["crossSourceBoundedPublicPages"] = CrossSourceRelationBrowser.MaximumPagesPerSource == 3 &&
                CrossSourceRelationBrowser.MaximumCandidates == 48 &&
                requestedJavDbPages.SequenceEqual([1, 2, 3]) &&
                requestedDatabaseUrls.SequenceEqual([DatabaseActorUrl, DatabaseActorUrl + "?ipage=2", DatabaseActorUrl + "?ipage=3"]) &&
                bounded.JavDbPagesRead == 3 && bounded.JavDatabasePagesRead == 3 &&
                bounded.JavDbMayHaveMore && bounded.JavDatabaseMayHaveMore &&
                finite.JavDbPagesRead == 1 && finite.JavDatabasePagesRead == 1 &&
                !finite.JavDbMayHaveMore && !finite.JavDatabaseMayHaveMore &&
                !JavDbScraper.TryCatalogUrl("https://javdb.com/tags?c3=43&c7=28", out _, out _)
        };
    }

    static bool Accepts(Action action)
    {
        try { action(); return true; }
        catch { return false; }
    }

    static bool Rejects(Action action) => !Accepts(action);
}

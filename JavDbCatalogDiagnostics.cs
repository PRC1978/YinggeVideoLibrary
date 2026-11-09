namespace YinggeVideoLibrary;

internal static class JavDbCatalogDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Verify()
    {
        var findings = new Dictionary<string, bool>(StringComparer.Ordinal);
        var source = new MovieMetadata
        {
            Code = "ABC-123", Title = "作品甲", Source = "JavDB", SourceUrl = "https://javdb.com/v/abc123"
        };
        const string detailHtml = "<div class='panel-block'><strong>番號:</strong><span class='value'>ABC-123</span></div>"
            + "<div class='panel-block'><strong>演員:</strong><span class='value'>"
            + "<a href='/actors/Av2e'>三上悠亞</a><a href='https://javdb.com.evil.test/actors/Av2e'>伪造演员</a></span></div>"
            + "<div class='panel-block'><strong>類別:</strong><span class='value'>"
            + "<a href='/tags?c3=43'>标签一</a><a href='/tags?c7=28'>标签二</a>"
            + "<a href='/tags?c3=43&amp;page=2'>不接收组合网址</a></span></div>";
        var verified = JavDbScraper.ParseJavDbDetails(source, detailHtml);
        findings["actorNameFromVerifiedDetail"] = verified.DetailFetched && verified.DetailIdentityVerified
            && JavDbScraper.MatchActorLinks([verified], "三上悠亜") is { Count: 1 } matches
            && matches[0].Url == "https://javdb.com/actors/Av2e";
        var unverified = verified.Clone();
        unverified.DetailIdentityVerified = false;
        findings["actorNameRejectsUnverifiedDetail"] = JavDbScraper.MatchActorLinks([unverified], "三上悠亜").Count == 0;
        findings["actorNameRejectsPathLikeInput"] = !JavDbScraper.TryActorSearchName("C:\\videos\\person", out _)
            && !JavDbScraper.TryActorSearchName("https://example.com", out _)
            && JavDbScraper.TryActorSearchName("三上悠亜", out _);
        findings["tagLinksFromSourceOnly"] = verified.CatalogLinks.Count(link => link.Kind == "标签") == 2
            && !verified.CatalogLinks.Any(link => link.Name is "伪造演员" or "不接收组合网址");
        findings["catalogRequiresVerifiedDetailBeforeLinksOrMatch"] =
            MainWindow.RequireVerifiedJavDbCatalogLinks(source, verified).Count == verified.CatalogLinks.Count
            && RejectsCatalogLinks(source, unverified)
            && RejectsCatalogLinks(new MovieMetadata { Code = "XYZ-999" }, verified)
            && RejectsCatalogLinks(new MovieMetadata { Code = "ABC-123", Source = "JavDB", SourceUrl = "https://javdb.com/v/other" }, verified);

        const string actorsHtml = "<html><title>演员搜索 | JavDB</title><div id='actors' class='actors'>"
            + "<div class='box actor-box'><a href='/actors/Av2e' title='三上悠亜, 三上悠亞'>"
            + "<figure class='image'><img class='avatar' src='https://example.test/avatar.jpg'></figure><strong>三上悠亜</strong></a></div>"
            + "<div class='box actor-box'><a href='https://javdb.com.evil.test/actors/x'><strong>伪造演员</strong></a></div></div></html>";
        var actors = JavDbScraper.ParseActorSearch(actorsHtml);
        findings["directActorSearchParsesOnlyPublicActorLinks"] = actors.Count == 1
            && actors[0].Name == "三上悠亜" && actors[0].Url == "https://javdb.com/actors/Av2e";
        findings["directActorSearchDistinguishesEmptyAndLogin"] =
            JavDbScraper.ParseActorSearch("<html><title>演员搜索 | JavDB</title><div id='actors'><div class='empty-message'>暫無內容</div></div></html>").Count == 0
            && ActorSearchFails("<html><title>登入 | JavDB 成人影片數據庫</title><div id='actors'><div class='empty-message'>暫無內容</div></div></html>", sourceUnavailable: true)
            && ActorSearchFails("<html><title>unknown</title></html>");
        const string actorPage = "<nav class='pagination'><a rel='next' class='pagination-next' href='/actors/Av2e?page=2'>下一頁</a></nav>";
        const string tagPage = "<nav class='pagination'><a href='/tags?c3=43&page=2' rel='next'>下一頁</a></nav>";
        findings["catalogPaginationStaysOnObservedDirectory"] =
            JavDbScraper.TryCatalogPageUrl("https://javdb.com/actors/Av2e", 2, out var actorPageUrl)
            && actorPageUrl == "https://javdb.com/actors/Av2e?page=2"
            && JavDbScraper.HasCatalogNextPage(actorPage, "https://javdb.com/actors/Av2e", 1)
            && JavDbScraper.HasCatalogNextPage(actorPage.Replace("/actors/Av2e?page=2", "?page=2"), "https://javdb.com/actors/Av2e", 1)
            && JavDbScraper.HasCatalogNextPage(tagPage, "https://javdb.com/tags?c3=43", 1)
            && !JavDbScraper.HasCatalogNextPage(actorPage.Replace("Av2e?page=2", "Other?page=2"), "https://javdb.com/actors/Av2e", 1)
            && !JavDbScraper.TryCatalogPageUrl("https://javdb.com/actors/Av2e", 21, out _)
            && !JavDbScraper.TryCatalogPageUrl("https://javdb.com/tags?c3=43&c7=28", 2, out _);

        var first = JavDbScraper.ParseCatalogPage("<a class='box card' href='/v/abc123'><img title='作品甲' src='https://c0.jdbstatic.com/a.jpg'><strong>ABC-123</strong></a>"
            + "<a class='box card' href='/v/def456'><img title='作品乙' src='https://c0.jdbstatic.com/b.jpg'><strong>DEF-456</strong></a>");
        var second = JavDbScraper.ParseCatalogPage("<a class='box card' href='/v/abc123'><img title='作品甲' src='https://c0.jdbstatic.com/a.jpg'><strong>ABC-123</strong></a>"
            + "<a class='box card' href='/v/ghi789'><img title='作品丙' src='https://c0.jdbstatic.com/c.jpg'><strong>GHI-789</strong></a>");
        var third = JavDbScraper.ParseCatalogPage("<a class='box card' href='/v/abc123'><img title='作品甲' src='https://c0.jdbstatic.com/a.jpg'><strong>ABC-123</strong></a>");
        var two = JavDbScraper.IntersectCatalogPages([first, second]);
        var three = JavDbScraper.IntersectCatalogPages([first, second, third]);
        findings["tagIntersectionOfObservedCards"] = two.Count == 1 && three.Count == 1
            && two[0].Code == "ABC-123" && !two[0].DetailFetched;
        var conflictingCard = first[0].Clone(); conflictingCard.Code = "XYZ-999";
        findings["tagIntersectionKeepsIdentityDistinct"] = JavDbScraper.IntersectCatalogPages([first, new List<MovieMetadata> { conflictingCard }]).Count == 0;
        findings["catalogRejectsUnverifiedCombinedQuery"] = !JavDbScraper.TryCatalogUrl("/tags?c3=43&c7=28", out _, out _)
            && !JavDbScraper.TryCatalogUrl("https://javdb.com.evil.test/tags?c3=43", out _, out _)
            && JavDbScraper.TryCatalogUrl("/tags?c3=43", out _, out _);
        var namedWork = verified.Clone();
        namedWork.DetailIdentityVerified = false;
        namedWork.CatalogLinks.Clear();
        var namedVideo = new VideoItem { Id = "named-actor-test", Title = "ABC-123", Path = "ABC-123.mp4",
            Folder = "", Size = 1, Modified = DateTime.UtcNow };
        var namedActor = new JavDbCatalogLink { Kind = "演员", Name = "三上悠亜", Url = "https://javdb.com/actors/Av2e" };
        Task<List<JavDbCatalogLink>> Search(string _, CancellationToken __) => Task.FromResult(new List<JavDbCatalogLink> { namedActor });
        Task<JavDbScraper.CatalogPageResult> Page(string _, int __, CancellationToken ___) =>
            Task.FromResult(new JavDbScraper.CatalogPageResult([first[0]], false));
        JavDbCatalogLink? Find(VideoItem video, MovieMetadata work,
            Func<string, CancellationToken, Task<List<JavDbCatalogLink>>>? search = null,
            Func<string, int, CancellationToken, Task<JavDbScraper.CatalogPageResult>>? page = null) =>
            RoleNamedActorDiscovery.FindAsync(video, work, "三上悠亜", default, search ?? Search, page ?? Page)
                .GetAwaiter().GetResult();
        findings["namedActorFindsExactWorkAcrossDirectory"] = Find(namedVideo, namedWork)?.Url == namedActor.Url;
        var wrongWork = namedWork.Clone(); wrongWork.SourceUrl = "https://javdb.com/v/other";
        var wrongVideo = new VideoItem { Id = "wrong-actor-test", Title = "XYZ-999", Path = "XYZ-999.mp4",
            Folder = "", Size = 1, Modified = DateTime.UtcNow };
        findings["namedActorRejectsWrongWorkAndLocalCode"] = Find(namedVideo, wrongWork) is null
            && Find(wrongVideo, namedWork) is null;
        Task<List<JavDbCatalogLink>> AmbiguousSearch(string _, CancellationToken __) =>
            Task.FromResult(new List<JavDbCatalogLink> { namedActor,
                new() { Kind = "演员", Name = namedActor.Name, Url = "https://javdb.com/actors/Other" } });
        findings["namedActorRejectsAmbiguousSameName"] = Find(namedVideo, namedWork, AmbiguousSearch) is null;
        return findings;
    }

    static bool RejectsCatalogLinks(MovieMetadata candidate, MovieMetadata details)
    {
        try { MainWindow.RequireVerifiedJavDbCatalogLinks(candidate, details); return false; }
        catch (System.IO.InvalidDataException) { return true; }
    }

    static bool ActorSearchFails(string html, bool sourceUnavailable = false)
    {
        try { _ = JavDbScraper.ParseActorSearch(html); return false; }
        catch (ScrapeSourceUnavailableException ex) when (sourceUnavailable)
        { return ex.RetryAt > DateTimeOffset.UtcNow; }
        catch (InvalidOperationException) when (!sourceUnavailable) { return true; }
    }
}

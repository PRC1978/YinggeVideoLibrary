using System.IO;

namespace YinggeVideoLibrary;

internal static class FankuHubScraperDiagnostics
{
    const string WorkId = "cmpaxwfz30fc7mhlh93h0wxok";
    const string WorkUrl = "https://fankuhub.com/anime/" + WorkId;

    const string SearchHtml = """
        <html><head><title>番剧索引 - 番库</title></head><body>
        <input placeholder="搜索番剧标题，回车搜索"/><p title="共 2 条结果">共 2 条结果</p>
        <article class="app-card"><a href="/anime/cmpaxwfz30fc7mhlh93h0wxok">
          <img src="https://assets.fankuhub.cn/i/anime/cmpaxwfz30fc7mhlh93h0wxok/example/small.webp"/>
          <p title="机动战士高达 铁血的奥尔芬斯">机动战士高达 铁血的奥尔芬斯</p>
          <p title="機動戦士ガンダム 鉄血のオルフェンズ">機動戦士ガンダム 鉄血のオルフェンズ</p>
          <span>2015年10月</span>
        </a></article>
        <article class="app-card"><a href="https://example.test/anime/123456789">
          <p title="不是番库作品">不是番库作品</p></a></article>
        </body></html>
        """;

    const string DetailHtml = """
        <html><head><script type="application/ld+json">
        {"@context":"https://schema.org","@type":"TVSeries",
         "name":"机动战士高达 铁血的奥尔芬斯",
         "alternateName":["機動戦士ガンダム 鉄血のオルフェンズ","Mobile Suit Gundam"],
         "url":"https://fankuhub.com/anime/cmpaxwfz30fc7mhlh93h0wxok",
         "image":"https://assets.fankuhub.cn/i/anime/cmpaxwfz30fc7mhlh93h0wxok/example/detail.webp",
         "description":"在番库查看作品简介。这里不是正文。",
         "datePublished":"2015-10-04","numberOfEpisodes":25,
         "genre":["高达","原创","热血"],
         "aggregateRating":{"@type":"AggregateRating","ratingValue":6.92,
                            "ratingCount":13,"bestRating":10,"worstRating":1}}
        </script></head><body><h1>机动战士高达 铁血的奥尔芬斯</h1>
        <dt>时长</dt><dd title="25 分">25 分</dd>
        <dt>制作</dt><dd title="Sunrise">Sunrise</dd>
        <div data-collapsible-body="true"><p>主人公三日月·奥古斯的故事。</p></div>
        </body></html>
        """;

    internal static IReadOnlyDictionary<string, bool> Verify()
    {
        var found = FankuHubScraper.ParseSearch(SearchHtml);
        var candidate = found.Single();
        var detail = FankuHubScraper.ParseDetail(candidate, DetailHtml);
        var rating = detail.PlatformRatings.SingleOrDefault(item => item.Platform == "FankuHub");

        bool Reject(MovieMetadata selected, string page)
        {
            try { _ = FankuHubScraper.ParseDetail(selected, page); return false; }
            catch (InvalidDataException) { return true; }
        }

        bool RejectSearch(string page)
        {
            try { _ = FankuHubScraper.ParseSearch(page); return false; }
            catch (InvalidDataException) { return true; }
        }

        var wrongHost = candidate.Clone();
        wrongHost.SourceUrl = "https://fankuhub.com.evil.test/anime/" + WorkId;
        var wrongCode = candidate.Clone();
        wrongCode.Code = "ABP-123";
        var wrongSource = candidate.Clone();
        wrongSource.Source = "Bangumi";
        var badYear = DetailHtml.Replace("2015-10-04", "2016-10-04", StringComparison.Ordinal);
        var badTitle = DetailHtml.Replace("\"name\":\"机动战士高达 铁血的奥尔芬斯\"",
            "\"name\":\"其他作品\"", StringComparison.Ordinal)
            .Replace("\"alternateName\":[\"機動戦士ガンダム 鉄血のオルフェンズ\"",
                "\"alternateName\":[\"其他别名\"", StringComparison.Ordinal);
        var badId = DetailHtml.Replace("/anime/" + WorkId, "/anime/otherid12345", StringComparison.Ordinal);
        var empty = SearchHtml.Replace("共 2 条结果", "共 0 条结果", StringComparison.Ordinal)
            .Replace("<article class=\"app-card\">", "<article-no-card>", StringComparison.Ordinal)
            .Replace("</article>", "</article-no-card>", StringComparison.Ordinal);
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["fankuSearchPublicCardsOnly"] = found.Count == 1 && candidate.Source == "FankuHub" &&
                candidate.SourceUrl == WorkUrl && candidate.Code.Length == 0 &&
                candidate.Title == "机动战士高达 铁血的奥尔芬斯" &&
                candidate.OriginalTitle == "機動戦士ガンダム 鉄血のオルフェンズ" &&
                candidate.ReleaseDate == "2015" && !candidate.DetailFetched &&
                !candidate.DetailIdentityVerified && candidate.ExternalIds["FankuHub"] == WorkId,
            ["fankuDetailVerifiedFieldsAndOwnRating"] = detail.DetailFetched &&
                detail.DetailIdentityVerified && detail.ReleaseDate == "2015-10-04" &&
                detail.RuntimeMinutes == 25 && detail.Studio == "Sunrise" &&
                detail.Summary == "主人公三日月·奥古斯的故事。" &&
                detail.Genres.SequenceEqual(["高达", "原创", "热血"]) &&
                detail.Rating == "6.92/10（13 票）" && rating is
                    { Value: 6.92, Maximum: 10, VoteCount: 13 } &&
                detail.FieldProvenance[nameof(MovieMetadata.Summary)].Source == "FankuHub" &&
                detail.FieldProvenance[nameof(MovieMetadata.Studio)].Stage == "番库公开详情页",
            ["fankuDetailRejectsIdentityConflicts"] = Reject(wrongHost, DetailHtml) &&
                Reject(wrongCode, DetailHtml) && Reject(wrongSource, DetailHtml) &&
                Reject(candidate, badId) && Reject(candidate, badYear) && Reject(candidate, badTitle) &&
                FankuHubScraper.TryAnimeId(WorkUrl, out var id) && id == WorkId &&
                !FankuHubScraper.TryAnimeId("http://fankuhub.com/anime/" + WorkId, out _) &&
                !FankuHubScraper.TryAnimeId(WorkUrl + "?other=1", out _) &&
                !FankuHubScraper.TryAnimeId("https://fankuhub.com.evil.test/anime/" + WorkId, out _),
            ["fankuNoResultsDistinctFromMalformedPage"] =
                FankuHubScraper.ParseSearch(empty).Count == 0 &&
                RejectSearch("<html><h1>访问验证</h1></html>") &&
                RejectSearch(SearchHtml.Replace("共 2 条结果", "无可读结果", StringComparison.Ordinal)),
            ["fankuRejectsFilePathAndUrlQueries"] =
                FankuHubScraper.NormalizeQuery("  进击的巨人  ") == "进击的巨人" &&
                RejectQuery("C:\\Videos\\Work.mkv") && RejectQuery("https://example.test/anime")
        };
    }

    static bool RejectQuery(string query)
    {
        try { _ = FankuHubScraper.NormalizeQuery(query); return false; }
        catch (ArgumentException) { return true; }
    }
}

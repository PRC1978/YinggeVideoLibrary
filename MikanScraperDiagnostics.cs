using System.IO;

namespace YinggeVideoLibrary;

internal static class MikanScraperDiagnostics
{
    // Short excerpts model the public search-card and detail markup observed on
    // mikanani.me. No live request or release item is needed to check parsing.
    const string SearchHtml = """
        <html><form method="get" action="/Home/Search"><input name="searchstr"></form>
        <ul class="list-inline an-ul">
          <li><a href="/Home/Bangumi/3141" target="_blank">
            <span data-src="/images/Bangumi/202309/5ce9fed1.jpg?width=400&amp;height=400&amp;format=webp" class="b-lazy"></span>
            <div class="an-info"><div class="an-info-group">
              <div class="an-text" title="&#x846C;&#x9001;&#x7684;&#x8299;&#x8389;&#x83B2;">葬送的芙莉莲</div>
            </div></div></a></li>
          <li><a href="/Home/Bangumi/3821"><div class="an-text" title="葬送的芙莉莲 第二季"></div></a></li>
        </ul><span class="header2-text">搜索结果</span>
        <a href="/Home/Episode/0123456789abcdef">[字幕组] 葬送的芙莉莲 第一季全集</a>
        """;

    const string DetailHtml = """
        <html><div class="bangumi-poster" style="background-image: url('/images/Bangumi/202309/5ce9fed1.jpg?width=400&height=560&format=webp');"></div>
        <p class="bangumi-title">&#x846C;&#x9001;&#x7684;&#x8299;&#x8389;&#x83B2; <a href="/RSS/Bangumi?bangumiId=3141" class="mikan-rss">RSS</a></p>
        <p class="bangumi-info">放送日期：星期五</p>
        <p class="bangumi-info">放送开始：9/29/2023</p>
        <p class="bangumi-info">官方网站：<a href="https://frieren-anime.jp/">作品网站</a></p>
        <div class="header2"><span class="header2-text">概况介绍</span></div>
        <p class="header2-desc">旅途中的&#x5C11;女 <b>芙莉莲</b> &amp; 同伴。 [简介原文] 旅を続ける少女と仲間。</p>
        """;

    internal static IReadOnlyDictionary<string, bool> Verify()
    {
        var found = MikanScraper.ParseSearch(SearchHtml);
        var candidate = found[0];
        var detail = MikanScraper.ParseDetail(candidate, DetailHtml);
        // A page that states it found nothing is a real empty answer; it must not
        // be reported as a parsing error. The wording is asserted here so the
        // marker constant cannot silently drift away from the parser.
        var unrelated = MikanScraper.ParseSearch(
            "<form action='/Home/Search'></form><span>搜索结果</span><p>没有找到相关番组</p>" +
            "<a href='/Home/Episode/1'>仅有发布资源</a>");
        // Same page, no empty marker, no catalog anchors: not decidable at all.
        var undecidableThrows = RejectSearch(
            "<form action='/Home/Search'></form><span>搜索结果</span><a href='/Home/Episode/1'>仅有发布资源</a>");
        // Shape of the live no-match page (sampled 2026-10-31, ev-fix/l2/
        // mikan-empty-1.html): no result heading, no catalog anchors, only the
        // "找不到对应结果" notice. This must stay a real empty answer.
        var liveEmpty = MikanScraper.ParseSearch(
            "<html><body><form method='get' action='/Home/Search'><input name='searchstr'></form>" +
            "<div class=\"no-subscribe-bangumi\">\"zzqqxxyy\" 找不到对应结果 &gt;_&lt; 试试别的关键词吧。</div>" +
            "<div class=\"m-home\"><img src='/images/mainvisual_line2.jpg' /></div></body></html>");
        // The card class moved but the catalog anchors are still there: the page
        // is not empty, the parser no longer understands it.
        var renamedCards = SearchHtml.Replace("an-text", "an-name", StringComparison.Ordinal);
        var structureChangeThrows = RejectSearch(renamedCards) &&
            renamedCards.Contains("/Home/Bangumi/3141", StringComparison.Ordinal) &&
            // Anchors without the result heading are a structure change too.
            RejectSearch("<form action='/Home/Search'></form>" +
                "<a href='/Home/Bangumi/3141'><div class='an-name' title='x'></div></a>");
        var malformedThrows = RejectSearch("<html>登录验证</html>");
        var pathThrows = false;
        try { _ = MikanScraper.NormalizeQuery(@"C:\Anime\Series"); }
        catch (ArgumentException) { pathThrows = true; }
        var wrongSource = candidate.Clone(); wrongSource.Source = "Bangumi";
        var wrongCode = candidate.Clone(); wrongCode.Code = "ABP-123";
        var wrongUrl = candidate.Clone(); wrongUrl.SourceUrl = "https://mikanani.me.evil.test/Home/Bangumi/3141";
        var priorYear = candidate.Clone(); priorYear.ReleaseDate = "2024";
        var originalOnly = MikanScraper.ParseDetail(candidate, DetailHtml.Replace(
            "旅途中的&#x5C11;女 <b>芙莉莲</b> &amp; 同伴。 [简介原文] 旅を続ける少女と仲間。",
            "[简介原文] 旅を続ける少女と仲間。", StringComparison.Ordinal));
        var wrongSection = MikanScraper.ParseDetail(candidate,
            DetailHtml.Replace("概况介绍", "其他介绍", StringComparison.Ordinal));
        var longSynopsis = MikanScraper.ParseDetail(candidate, DetailHtml.Replace(
            "旅途中的&#x5C11;女 <b>芙莉莲</b> &amp; 同伴。 [简介原文] 旅を続ける少女と仲間。",
            new string('文', 2500), StringComparison.Ordinal));
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["mikanSearchUsesOnlyAnimeCards"] = found.Count == 2 &&
                found.All(item => item.Source == "Mikan" && item.Code.Length == 0 &&
                    !item.DetailFetched && !item.DetailIdentityVerified) &&
                candidate.SourceUrl == "https://mikanani.me/Home/Bangumi/3141" &&
                candidate.Title == "葬送的芙莉莲" && unrelated.Count == 0,
            ["mikanDetailRequiresSameCatalogIdAndTitle"] = detail.DetailFetched &&
                detail.DetailIdentityVerified && detail.Title == candidate.Title &&
                detail.ReleaseDate == "2023-09-29" &&
                detail.PosterUrl == "https://mikanani.me/images/Bangumi/202309/5ce9fed1.jpg?width=400&height=560&format=webp" &&
                detail.Rating is null && detail.Summary == "旅途中的少女 芙莉莲 & 同伴。" &&
                RejectDetail(candidate, DetailHtml.Replace("bangumiId=3141", "bangumiId=3821", StringComparison.Ordinal)) &&
                RejectDetail(candidate, DetailHtml.Replace("&#x846C;&#x9001;&#x7684;&#x8299;&#x8389;&#x83B2;", "不同的番剧", StringComparison.Ordinal)) &&
                RejectDetail(priorYear, DetailHtml) &&
                RejectDetail(wrongSource, DetailHtml) && RejectDetail(wrongCode, DetailHtml) &&
                RejectDetail(wrongUrl, DetailHtml),
            ["mikanRejectsUntrustedUrlsAndMalformedPage"] = malformedThrows && pathThrows &&
                MikanScraper.TryBangumiId(candidate.SourceUrl, out var id) && id == 3141 &&
                !MikanScraper.TryBangumiId("http://mikanani.me/Home/Bangumi/3141", out _) &&
                !MikanScraper.TryBangumiId("https://mikanani.me.evil.test/Home/Bangumi/3141", out _) &&
                !MikanScraper.TryBangumiId("https://mikanani.me/Home/Bangumi/3141?next=evil", out _),
            ["mikanSynopsisIsBoundedAndFromOverviewOnly"] =
                originalOnly.Summary == "旅を続ける少女と仲間。" &&
                wrongSection.Summary is null && longSynopsis.Summary?.Length == 2000,
            ["mikanPosterCannotPointOutsideSite"] =
                MikanScraper.ParseDetail(candidate, DetailHtml.Replace(
                    "/images/Bangumi/202309/5ce9fed1.jpg?width=400&height=560&format=webp",
                    "https://evil.test/cover.jpg", StringComparison.Ordinal)).PosterUrl == candidate.PosterUrl,
            ["mikanEmptyResultPageIsNotAnError"] = unrelated.Count == 0,
            ["mikanLiveEmptyResultPageIsNotAnError"] = liveEmpty.Count == 0,
            ["mikanStructureChangeIsNotAnEmptyResult"] = structureChangeThrows && undecidableThrows,
            ["mikanRateLimitKeepsRetryAfter"] = RateLimitStatus(),
            ["mikanAccessVerificationBecomesCooldown"] = AccessVerificationStatus()
        };
        AddCapturedPageRegression(checks);
        return checks;
    }

    // Optional offline regression against the live search page captured on
    // 2026-10-31 (ev-fix/l2/mikan-search-2.html). The download itself timed out
    // mid-stream, so only the prefix the scraper actually reads is used. Enabled
    // only through YINGGE_MIKAN_SEARCH_FIXTURE so the default run stays hermetic.
    static void AddCapturedPageRegression(IDictionary<string, bool> checks)
    {
        var path = Environment.GetEnvironmentVariable("YINGGE_MIKAN_SEARCH_FIXTURE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var html = File.ReadAllText(path);
        if (html.Length > 192_000) html = html[..192_000];
        var parsed = MikanScraper.ParseSearch(html);
        checks["mikanCapturedLiveSearchPageParsesCards"] =
            parsed.Count == 2 && parsed.All(item => item.Source == "Mikan" && item.Title.Length > 0);
    }

    // 429 keeps its Retry-After window; nothing is turned into a permanent
    // failure by the status mapping itself.
    static bool RateLimitStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var (error, pause) = MikanScraper.ClassifyStatus(System.Net.HttpStatusCode.TooManyRequests,
            TimeSpan.FromSeconds(90), now);
        var fallback = MikanScraper.ClassifyStatus(System.Net.HttpStatusCode.TooManyRequests, null, now);
        return error is ScrapeHttpException { StatusCode: System.Net.HttpStatusCode.TooManyRequests, RetryAfter: { } window }
            && window == TimeSpan.FromSeconds(90) && pause == TimeSpan.FromSeconds(90)
            && fallback.Error is ScrapeHttpException { RetryAfter: { } defaultWindow }
            && defaultWindow == TimeSpan.FromMinutes(1);
    }

    // 403 and an anti-bot interstitial become a source cooldown of about one
    // hour, so the queue keeps the task instead of failing the video.
    static bool AccessVerificationStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var (forbidden, forbiddenPause) = MikanScraper.ClassifyStatus(
            System.Net.HttpStatusCode.Forbidden, null, now);
        var (serviceUnavailable, _) = MikanScraper.ClassifyStatus(
            System.Net.HttpStatusCode.ServiceUnavailable, null, now);
        return forbidden is ScrapeSourceUnavailableException { RetryAt: { } until }
            && until - now >= TimeSpan.FromMinutes(59) && until - now <= TimeSpan.FromMinutes(61)
            && forbiddenPause == TimeSpan.FromHours(1)
            && serviceUnavailable is ScrapeHttpException { StatusCode: System.Net.HttpStatusCode.ServiceUnavailable }
            && MikanScraper.IsAccessInterstitial("<html><title>Just a moment...</title></html>")
            && MikanScraper.IsAccessInterstitial("<script src='/cdn-cgi/challenge-platform/cf-chl'></script>")
            && !MikanScraper.IsAccessInterstitial(SearchHtml);
    }

    static bool RejectSearch(string html)
    {
        try { _ = MikanScraper.ParseSearch(html); return false; }
        catch (InvalidDataException) { return true; }
    }

    static bool RejectDetail(MovieMetadata candidate, string html)
    {
        try { _ = MikanScraper.ParseDetail(candidate, html); return false; }
        catch (InvalidDataException) { return true; }
    }
}

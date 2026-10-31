using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// Mikan's public search page has a small anime recommendation section followed
// by a potentially huge release list. Read only the bounded page prefix and
// only /Home/Bangumi cards. Never parse or request episode/torrent/magnet links.
public static class MikanScraper
{
    const int MaxSearchPrefixBytes = 192_000;
    const int MaxDetailPrefixBytes = 128_000;
    const string Site = "https://mikanani.me";
    static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(20) };
    static readonly SemaphoreSlim RequestGate = new(1, 1);
    static DateTimeOffset nextRequestAt;
    static readonly TimeSpan HtmlRegexTimeout = TimeSpan.FromMilliseconds(500);
    static readonly Regex SearchCard = new(
        """<a\b[^>]*\bhref\s*=\s*["']/Home/Bangumi/(?<id>[1-9][0-9]{0,8})["'][^>]*>(?<body>.{0,5000}?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    static readonly Regex CardTitle = new(
        """<div\b[^>]*\bclass\s*=\s*["'][^"']*\ban-text\b[^"']*["'][^>]*\btitle\s*=\s*["'](?<value>[^"']{1,1000})["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, HtmlRegexTimeout);
    static readonly Regex CardPoster = new(
        """\bdata-src\s*=\s*["'](?<value>[^"']{1,1000})["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, HtmlRegexTimeout);
    static readonly Regex DetailTitle = new(
        """<p\b[^>]*\bclass\s*=\s*["'][^"']*\bbangumi-title\b[^"']*["'][^>]*>(?<value>.{1,1000}?)<a\b[^>]*\bhref\s*=\s*["']/RSS/Bangumi\?bangumiId=(?<id>[1-9][0-9]{0,8})["']""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    static readonly Regex DetailPoster = new(
        """<div\b[^>]*\bclass\s*=\s*["'][^"']*\bbangumi-poster\b[^"']*["'][^>]*\bstyle\s*=\s*["'][^"']*background-image\s*:\s*url\(['"]?(?<value>/images/Bangumi/[^)'"<>]{1,500})""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, HtmlRegexTimeout);
    static readonly Regex AirDate = new(
        """<p\b[^>]*\bclass\s*=\s*["'][^"']*\bbangumi-info\b[^"']*["'][^>]*>\s*放送开始\s*[：:]\s*(?<value>[0-9]{1,2}/[0-9]{1,2}/[0-9]{4})\s*</p>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, HtmlRegexTimeout);
    static readonly Regex Synopsis = new(
        """<span\b[^>]*\bclass\s*=\s*["'][^"']*\bheader2-text\b[^"']*["'][^>]*>\s*概况介绍\s*</span>\s*</div>\s*<p\b[^>]*\bclass\s*=\s*["'][^"']*\bheader2-desc\b[^"']*["'][^>]*>(?<value>.{0,12000}?)</p>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    static readonly Regex Tag = new("<[^>]*>", RegexOptions.Singleline | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, HtmlRegexTimeout);
    // Empty-result wording of the public search page. Sampled live on 2026-10-31:
    // the no-match page has no result heading and only the notice
    // "找不到对应结果" (see ev-fix/l2/mikan-empty-1.html). The other spellings are
    // tolerated variants. This is the only signal that separates "nothing
    // matched" from "the card markup changed", so it is also asserted in
    // diagnostics rather than trusted silently.
    static readonly Regex EmptyResultMarker = new(
        @"找不到对应结果|没有找到|沒有找到|未找到|無符合|无符合|无匹配|查询不到|暂无(?:相关)?(?:搜索结果|结果|番组|匹配)",
        RegexOptions.CultureInvariant, HtmlRegexTimeout);
    // Catalog anchors survive a rename of the card classes. Counting them lets
    // ParseSearch tell "no cards because nothing matched" from "no cards
    // because the markup moved".
    static readonly Regex BangumiAnchor = new(
        """(?:href\s*=\s*["']/Home/Bangumi/|data-src\s*=\s*["']/images/Bangumi/)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, HtmlRegexTimeout);

    public static async Task<List<MovieMetadata>> SearchAsync(string query,
        CancellationToken cancellationToken = default)
    {
        query = NormalizeQuery(query);
        if (query.Length == 0) return [];
        var html = await GetPrefixAsync(Site + "/Home/Search?searchstr=" +
            Uri.EscapeDataString(query), MaxSearchPrefixBytes, cancellationToken);
        return ParseSearch(html);
    }

    public static async Task<MovieMetadata> FetchFullDetailsAsync(MovieMetadata candidate,
        CancellationToken cancellationToken = default)
    {
        if (candidate is null || !candidate.Source.Equals("Mikan", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(candidate.Code) || !TryBangumiId(candidate.SourceUrl, out var id))
            throw new InvalidDataException("蜜柑候选缺少有效的番组编号。");
        var html = await GetPrefixAsync(Site + "/Home/Bangumi/" + id.ToString(CultureInfo.InvariantCulture),
            MaxDetailPrefixBytes, cancellationToken);
        return ParseDetail(candidate, html);
    }

    internal static string NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        query = query.Trim();
        if (query.Length > 160) query = query[..160].TrimEnd();
        if (query.Any(char.IsControl) || query.StartsWith('/') || query.Contains('\\') ||
            Uri.TryCreate(query, UriKind.Absolute, out _) ||
            (query.Length >= 2 && char.IsLetter(query[0]) && query[1] == ':'))
            throw new ArgumentException("蜜柑只接受番剧名称，不接受文件路径或网址。", nameof(query));
        return query;
    }

    static async Task<string> GetPrefixAsync(string url, int maxBytes, CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            var delay = nextRequestAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            nextRequestAt = DateTimeOffset.UtcNow.AddSeconds(5);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
            var version = typeof(MikanScraper).Assembly.GetName().Version?.ToString(3) ?? "1.5.3";
            request.Headers.TryAddWithoutValidation("User-Agent",
                $"PRC1978/YinggeVideoLibrary/{version} (https://github.com/PRC1978/YinggeVideoLibrary)");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var header = response.Headers.RetryAfter;
                var retry = header?.Delta ?? (header?.Date is DateTimeOffset date
                    ? date - DateTimeOffset.UtcNow : null);
                var (error, pause) = ClassifyStatus(response.StatusCode, retry, DateTimeOffset.UtcNow);
                if (pause is { } window) nextRequestAt = DateTimeOffset.UtcNow.Add(window);
                throw error;
            }
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("蜜柑返回的不是网页，未使用其内容。");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream(Math.Min(maxBytes, 32_768));
            var chunk = new byte[8192];
            while (buffer.Length < maxBytes)
            {
                var count = await stream.ReadAsync(chunk.AsMemory(0,
                    (int)Math.Min(chunk.Length, maxBytes - buffer.Length)), cancellationToken);
                if (count == 0) break;
                buffer.Write(chunk, 0, count);
            }
            var html = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            if (IsAccessInterstitial(html))
            {
                var challenge = AccessVerificationFailure(HttpStatusCode.Forbidden,
                    DateTimeOffset.UtcNow, "人机校验页");
                nextRequestAt = challenge.RetryAt ?? DateTimeOffset.UtcNow.AddHours(1);
                throw challenge;
            }
            return html;
        }
        finally { RequestGate.Release(); }
    }

    // An interstitial ("Just a moment...", cf-chl) is the site refusing this
    // client, not a page without candidates.
    internal static bool IsAccessInterstitial(string? html) =>
        !string.IsNullOrEmpty(html) &&
        (html.Contains("cf-chl", StringComparison.OrdinalIgnoreCase) ||
         html.Contains("Just a moment...", StringComparison.OrdinalIgnoreCase));

    internal static ScrapeSourceUnavailableException AccessVerificationFailure(HttpStatusCode status,
        DateTimeOffset now, string? reason = null)
    {
        var detail = reason is null ? $"HTTP {(int)status}" : $"{reason}，HTTP {(int)status}";
        return new ScrapeSourceUnavailableException(
            $"蜜柑要求访问验证或拒绝访问（{detail}），已停止读取该页面；稍后自动重试。",
            now.AddHours(1));
    }

    // Same split as the fankuhub scraper: only "the site refuses this client"
    // becomes a cooldown; 429/503 stay rate-limit errors with their window.
    internal static (Exception Error, TimeSpan? Pause) ClassifyStatus(HttpStatusCode status,
        TimeSpan? retryAfter, DateTimeOffset now)
    {
        if (status == HttpStatusCode.Forbidden)
            return (AccessVerificationFailure(status, now), TimeSpan.FromHours(1));
        if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            var fallback = status == HttpStatusCode.TooManyRequests
                ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(20);
            var pause = retryAfter is { } specified && specified > TimeSpan.Zero ? specified : fallback;
            pause = pause > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : pause;
            return (new ScrapeHttpException($"蜜柑返回 HTTP {(int)status}，稍后重试。", status, pause), pause);
        }
        return (new ScrapeHttpException($"蜜柑返回 HTTP {(int)status}，稍后重试。", status, retryAfter), null);
    }

    internal static List<MovieMetadata> ParseSearch(string html)
    {
        if (string.IsNullOrWhiteSpace(html) || !html.Contains("/Home/Search", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("蜜柑搜索页面结构不完整，不能判断为无结果。");
        // The recommendation section precedes the release table. Never let a
        // release title masquerade as a catalogued anime candidate. A live page
        // with no match carries no result heading at all (sampled 2026-10-31), so
        // the heading is optional and only bounds the recommendation region.
        var end = html.IndexOf("搜索结果", StringComparison.Ordinal);
        var cards = end < 0 ? "" : html[..end];
        var resultsSection = end < 0 ? html : html[end..];
        // Anchors are counted over the whole page: they survive a rename of the
        // card classes, which is exactly the case that must not look empty.
        var anchors = BangumiAnchor.Matches(html).Count;
        var results = new List<MovieMetadata>();
        var seen = new HashSet<int>();
        foreach (Match match in SearchCard.Matches(cards))
        {
            if (!int.TryParse(match.Groups["id"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var id) || !seen.Add(id)) continue;
            var titleMatch = CardTitle.Match(match.Groups["body"].Value);
            var title = CleanText(titleMatch.Groups["value"].Value, 240);
            if (title is null) continue;
            var poster = PosterUrl(CardPoster.Match(match.Groups["body"].Value).Groups["value"].Value);
            var candidate = new MovieMetadata
            {
                Source = "Mikan", SourceUrl = Site + "/Home/Bangumi/" + id,
                Code = "", Title = title, PosterUrl = poster,
                DetailNote = "蜜柑搜索页的番组卡片；尚未核对详情，须人工审核"
            };
            candidate.StampAvailableFields("搜索结果");
            results.Add(candidate);
            if (results.Count >= 10) break;
        }
        if (results.Count > 0) return results;
        // Zero cards is only a real "no match" when the page says so. The page
        // carries bangumi anchors (poster images or catalog links) even when the
        // card class names change, so anchors without parsed cards mean the
        // markup moved, not that nothing matched.
        if (EmptyResultMarker.IsMatch(resultsSection)) return results;
        if (anchors > 0)
            throw new InvalidDataException("蜜柑搜索页面结构可能已变化，不能当作无结果。");
        throw new InvalidDataException("蜜柑搜索页面没有可核对的结果标记，不能判断为无结果。");
    }

    internal static MovieMetadata ParseDetail(MovieMetadata candidate, string html)
    {
        if (candidate is null || !candidate.Source.Equals("Mikan", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(candidate.Code) || !TryBangumiId(candidate.SourceUrl, out var expectedId) ||
            string.IsNullOrWhiteSpace(html))
            throw new InvalidDataException("蜜柑详情缺少所选番组身份。");
        var titleMatch = DetailTitle.Match(html);
        if (!titleMatch.Success || !int.TryParse(titleMatch.Groups["id"].Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out var returnedId) ||
            returnedId != expectedId)
            throw new InvalidDataException("蜜柑详情的番组编号与所选候选不一致。");
        var title = CleanText(titleMatch.Groups["value"].Value, 240);
        if (title is null || !SameTitle(candidate.Title, title))
            throw new InvalidDataException("蜜柑详情的番名与搜索候选冲突；资料未采用。");
        var dateText = AirDate.Match(html).Groups["value"].Value;
        string? releaseDate = null;
        if (DateOnly.TryParseExact(dateText, "M/d/yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var airDate))
            releaseDate = airDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (candidate.ReleaseDate is { Length: >= 4 } previous && releaseDate is { Length: >= 4 } updated &&
            !previous[..4].Equals(updated[..4], StringComparison.Ordinal))
            throw new InvalidDataException("蜜柑详情的放送年份与搜索候选冲突；资料未采用。");
        var record = candidate.Clone();
        record.Title = title;
        record.ReleaseDate = releaseDate;
        record.Summary = ParseSynopsis(html);
        record.PosterUrl = PosterUrl(DetailPoster.Match(html).Groups["value"].Value) ?? candidate.PosterUrl;
        record.Source = "Mikan";
        record.SourceUrl = Site + "/Home/Bangumi/" + expectedId;
        record.Code = "";
        record.RefreshChangedFieldProvenance(candidate, "番组详情（编号与番名核验）");
        record.DetailFetched = true;
        record.DetailIdentityVerified = true;
        record.DetailNote = "已核对蜜柑番组编号和番名；仅收录页面明示的放送日期、简介与海报，不读取发布资源；须人工审核";
        return record;
    }

    static string? ParseSynopsis(string html)
    {
        var match = Synopsis.Match(html);
        if (!match.Success) return null;
        var text = Whitespace.Replace(WebUtility.HtmlDecode(
            Tag.Replace(match.Groups["value"].Value, " ")), " ").Trim();
        const string originalMarker = "[简介原文]";
        var marker = text.IndexOf(originalMarker, StringComparison.Ordinal);
        if (marker >= 0)
        {
            var translated = text[..marker].Trim();
            var original = text[(marker + originalMarker.Length)..].Trim();
            text = translated.Length > 0 ? translated : original;
        }
        if (text.Length > 2000) text = text[..2000].TrimEnd();
        return text.Length > 0 && !text.Any(char.IsControl) ? text : null;
    }

    internal static bool TryBangumiId(string? value, out int id)
    {
        id = 0;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("mikanani.me", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var match = Regex.Match(uri.AbsolutePath, @"^/Home/Bangumi/(?<id>[1-9][0-9]{0,8})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, HtmlRegexTimeout);
        return match.Success && int.TryParse(match.Groups["id"].Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    static string? PosterUrl(string? raw)
    {
        raw = WebUtility.HtmlDecode(raw?.Trim());
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 700) return null;
        if (!Regex.IsMatch(raw, @"^/images/Bangumi/[A-Za-z0-9/_-]+\.(?:jpg|jpeg|png|webp)(?:\?width=[0-9]{1,4}&height=[0-9]{1,4}&format=webp)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, HtmlRegexTimeout)) return null;
        return Site + raw;
    }

    static string? CleanText(string? raw, int limit)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 4000) return null;
        var text = Whitespace.Replace(WebUtility.HtmlDecode(Tag.Replace(raw, " ")), " ").Trim();
        return text.Length is > 0 and <= 240 && text.Length <= limit && !text.Any(char.IsControl)
            ? text : null;
    }

    static bool SameTitle(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
           string.Equals(string.Concat(left.Normalize().Where(char.IsLetterOrDigit)),
               string.Concat(right.Normalize().Where(char.IsLetterOrDigit)), StringComparison.OrdinalIgnoreCase);
}

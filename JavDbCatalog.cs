using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

public static partial class JavDbScraper
{
    static readonly Regex CatalogPath = new(@"^/(?<kind>actors|series)/[A-Za-z0-9_-]{1,64}/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex TagFilter = new(@"^c[0-9]{1,2}=[0-9]{1,6}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CatalogAnchor = new("""<a\b(?<attrs>[^>]{0,1000})>(?<name>.*?)</a>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex AnchorHref = new("""\bhref\s*=\s*['\"](?<href>[^'\"]+)['\"]""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ActorSearchSection = new("""<div\b[^>]*\bid\s*=\s*['\"]actors['\"][^>]*>""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ActorSearchCard = new("""<div\b[^>]*\bclass\s*=\s*['\"][^'\"]*\bactor-box\b[^'\"]*['\"][^>]*>\s*<a\b(?<attrs>[^>]{0,1000})>(?<body>.*?)</a>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex ActorSearchName = new(@"<strong\b[^>]*>(?<name>.*?)</strong>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex NextPageLink = new("""<a\b(?<attrs>[^>]{0,1000}\brel\s*=\s*['\"]next['\"][^>]{0,1000})>""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly SemaphoreSlim CatalogRequestGate = new(1, 1);
    static DateTimeOffset nextCatalogRequest = DateTimeOffset.MinValue;
    // Public catalog browsing never accepts or sends cookies and never follows
    // redirects to a login page. Other JavDB scraping paths keep their client.
    static readonly HttpClient CatalogClient = CreateClient(allowRedirect: false, useCookies: false);
    internal sealed record CatalogPageResult(List<MovieMetadata> Movies, bool HasNextPage);

    // A manual catalog action may inspect several pages, but never bursts
    // requests. This gate also applies to actor-name discovery.
    static async Task<T> ThrottledCatalogRequestAsync<T>(Func<Task<T>> request, CancellationToken cancellationToken)
    {
        await CatalogRequestGate.WaitAsync(cancellationToken);
        try
        {
            var remaining = nextCatalogRequest - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
            return await request();
        }
        finally
        {
            nextCatalogRequest = DateTimeOffset.UtcNow.AddMilliseconds(900);
            CatalogRequestGate.Release();
        }
    }

    // Detail pages supply tag/series links; the public actor search can supply
    // actor links. Never build an ID from a display name or unverified API.
    internal static List<JavDbCatalogLink> ExtractCatalogLinks(string html)
    {
        var links = new List<JavDbCatalogLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match block in DetailBlocks.Matches(html))
        {
            var body = block.Groups["body"].Value;
            var labelMatch = DetailLabel.Match(body);
            if (!labelMatch.Success) continue;
            var label = CleanHtml(labelMatch.Groups["value"].Value).Replace(" ", "").ToLowerInvariant();
            string expectedKind;
            if (label.Contains("演員") || label.Contains("演员") || label.Contains("actress") || label.Contains("actor")) expectedKind = "演员";
            else if (label.Contains("系列") || label.Contains("series")) expectedKind = "系列";
            else if (label.Contains("類別") || label.Contains("类别") || label.Contains("標籤") || label.Contains("标签") || label.Contains("genre") || label.Contains("tag")) expectedKind = "标签";
            else continue;
            foreach (Match anchor in CatalogAnchor.Matches(body))
            {
                var href = WebUtility.HtmlDecode(AnchorHref.Match(anchor.Groups["attrs"].Value).Groups["href"].Value);
                if (!TryCatalogUrl(href, out var url, out var actualKind) || actualKind != expectedKind || !seen.Add(url)) continue;
                var name = CleanHtml(anchor.Groups["name"].Value);
                if (name.Length is 0 or > 80) continue;
                links.Add(new JavDbCatalogLink { Kind = actualKind, Name = name, Url = url });
                if (links.Count >= 60) return links;
            }
        }
        return links;
    }

    internal static bool TryCatalogUrl(string value, out string url, out string kind)
    {
        url = ""; kind = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(new Uri("https://javdb.com"), value, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("javdb.com", StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443 || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0) return false;
        // Current detail pages expose tag filters as /tags?c7=11, not as
        // /tags/<id>. Keep the exact, bounded filter they supplied.
        if (uri.AbsolutePath.Equals("/tags", StringComparison.OrdinalIgnoreCase)
            && TagFilter.IsMatch(uri.Query.TrimStart('?')))
        { url = uri.GetLeftPart(UriPartial.Path) + uri.Query; kind = "标签"; return true; }
        if (uri.Query.Length != 0) return false;
        var match = CatalogPath.Match(uri.AbsolutePath);
        if (!match.Success) return false;
        kind = match.Groups["kind"].Value.ToLowerInvariant() switch
        {
            "actors" => "演员", "series" => "系列", _ => ""
        };
        url = uri.GetLeftPart(UriPartial.Path);
        return kind.Length > 0;
    }

    // A cross-source relation must retain the exact work detail that supplied
    // the selected link. Actor-name search cards by themselves are not proof.
    internal static bool IsVerifiedCatalogOrigin(MovieMetadata origin, JavDbCatalogLink link)
    {
        if (!string.Equals(origin.Source, "JavDB", StringComparison.OrdinalIgnoreCase) ||
            !origin.DetailFetched || !origin.DetailIdentityVerified ||
            ScrapeCodeIdentity.Key(origin.Code).Length == 0 ||
            !TryCandidateUrl(origin.SourceUrl, out _) ||
            !TryCatalogUrl(link.Url, out var selectedUrl, out var kind) || kind != link.Kind)
            return false;
        return (origin.CatalogLinks ?? []).Any(observed =>
            observed.Kind == kind && observed.Name == link.Name &&
            TryCatalogUrl(observed.Url, out var observedUrl, out var observedKind) &&
            observedKind == kind && observedUrl.Equals(selectedUrl, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsCatalogCandidate(MovieMetadata candidate) =>
        string.Equals(candidate.Source, "JavDB", StringComparison.OrdinalIgnoreCase) &&
        TryCandidateUrl(candidate.SourceUrl, out _) &&
        ScrapeCodeIdentity.Key(candidate.Code).Length >= 4;

    internal static bool TryCatalogPageUrl(string catalogUrl, int page, out string requestUrl)
    {
        requestUrl = "";
        if (!TryCatalogUrl(catalogUrl, out var safeUrl, out _) || page is < 1 or > 20) return false;
        requestUrl = safeUrl + (page == 1 ? "" : (safeUrl.Contains('?') ? "&page=" : "?page=") + page);
        return true;
    }

    public static async Task<List<MovieMetadata>> FetchCatalogPageAsync(string catalogUrl, int page,
        CancellationToken cancellationToken = default)
        => (await FetchCatalogPageWithNavigationAsync(catalogUrl, page, cancellationToken)).Movies;

    internal static async Task<CatalogPageResult> FetchCatalogPageWithNavigationAsync(string catalogUrl, int page,
        CancellationToken cancellationToken = default)
    {
        if (!TryCatalogUrl(catalogUrl, out var safeUrl, out _))
            throw new InvalidOperationException("关系目录必须是 JavDB 详情页提供的演员、系列或标签链接。");
        if (page is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(page), "只允许手动浏览前 20 页。");
        _ = TryCatalogPageUrl(safeUrl, page, out var requestUrl);
        return await ThrottledCatalogRequestAsync(async () =>
        {
            var html = await FetchPublicCatalogHtmlAsync(requestUrl, cancellationToken);
            return new CatalogPageResult(ParseCatalogPage(html), HasCatalogNextPage(html, safeUrl, page));
        }, cancellationToken);
    }

    static async Task<string> FetchPublicCatalogHtmlAsync(string requestUrl, CancellationToken cancellationToken)
    {
        using var response = await CatalogClient.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            throw new InvalidOperationException("JavDB 公开目录要求登录或跳转验证；不会把它当作没有作品。");
        EnsureSuccessfulResponse(response);
        if (response.Content.Headers.ContentLength is > 1_048_576)
            throw new InvalidOperationException("JavDB 公开目录页面超出读取上限。");
        await response.Content.LoadIntoBufferAsync(1_048_576, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureReadablePage(html);
        return html;
    }

    internal static bool HasCatalogNextPage(string html, string catalogUrl, int currentPage)
    {
        if (currentPage >= 20 || !TryCatalogPageUrl(catalogUrl, currentPage + 1, out var nextUrl)) return false;
        foreach (Match link in NextPageLink.Matches(html))
        {
            var href = WebUtility.HtmlDecode(AnchorHref.Match(link.Groups["attrs"].Value).Groups["href"].Value);
            if (Uri.TryCreate(new Uri(catalogUrl), href, out var actual)
                && actual.AbsoluteUri.Equals(nextUrl, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static bool TryActorSearchName(string? name, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(name)) return false;
        var value = name.Trim();
        if (value.Length is < 2 or > 80 || value.Any(char.IsControl)
            || value.IndexOfAny(['/', '\\', ':', '?', '#', '&', '=']) >= 0) return false;
        normalized = NormalizeActorName(value);
        return normalized.Length >= 2;
    }

    static string NormalizeActorName(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC)
            .Replace('亞', '亜').Replace('亚', '亜');
        return string.Concat(normalized.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
    }

    internal static List<JavDbCatalogLink> MatchActorLinks(IEnumerable<MovieMetadata> verifiedDetails, string actorName)
    {
        if (!TryActorSearchName(actorName, out var normalized)) return [];
        return verifiedDetails.Where(detail => detail.DetailFetched && detail.DetailIdentityVerified)
            .SelectMany(detail => detail.CatalogLinks ?? [])
            .Where(link => link.Kind == "演员" && TryCatalogUrl(link.Url, out _, out var kind) && kind == "演员"
                && NormalizeActorName(link.Name) == normalized)
            .GroupBy(link => link.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Clone()).Take(4).ToList();
    }

    internal static List<JavDbCatalogLink> ParseActorSearch(string html)
    {
        EnsureReadablePage(html);
        var actorSection = ActorSearchSection.Match(html);
        if (!actorSection.Success)
            throw new InvalidOperationException("JavDB 未返回可识别的公开演员搜索页。");
        var cards = ActorSearchCard.Matches(html);
        var results = new List<JavDbCatalogLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match card in cards)
        {
            var href = WebUtility.HtmlDecode(AnchorHref.Match(card.Groups["attrs"].Value).Groups["href"].Value);
            if (!TryCatalogUrl(href, out var url, out var kind) || kind != "演员" || !seen.Add(url)) continue;
            var name = CleanHtml(ActorSearchName.Match(card.Groups["body"].Value).Groups["name"].Value);
            if (name.Length is < 2 or > 80) continue;
            results.Add(new JavDbCatalogLink { Kind = "演员", Name = name, Url = url });
            if (results.Count >= 20) break;
        }
        if (results.Count > 0) return results;
        if (cards.Count > 0) throw new InvalidOperationException("JavDB 演员结果缺少可核对的姓名或目录链接。");
        var sectionStart = actorSection.Index + actorSection.Length;
        var sectionPreview = html.Substring(sectionStart, Math.Min(1024, html.Length - sectionStart));
        if (sectionPreview.Contains("empty-message", StringComparison.OrdinalIgnoreCase)
            && (sectionPreview.Contains("暫無內容", StringComparison.OrdinalIgnoreCase)
                || sectionPreview.Contains("暂无内容", StringComparison.OrdinalIgnoreCase))) return results;
        throw new InvalidOperationException("JavDB 演员搜索页面结构已变化，不能判定为无结果。");
    }

    // The public actor search returns actor directory links directly. The user
    // chooses one; no actor ID is inferred from a name or local file path.
    public static async Task<List<JavDbCatalogLink>> FindActorLinksByNameAsync(string actorName,
        CancellationToken cancellationToken = default)
    {
        if (!TryActorSearchName(actorName, out _))
            throw new ArgumentException("演员名称需为 2–80 字，不能包含路径或网址字符。", nameof(actorName));
        var url = "https://javdb.com/search?f=actor&q=" + Uri.EscapeDataString(actorName.Trim());
        return await ThrottledCatalogRequestAsync(async () =>
            ParseActorSearch(await FetchPublicCatalogHtmlAsync(url, cancellationToken)), cancellationToken);
    }

    internal static List<MovieMetadata> IntersectCatalogPages(IReadOnlyList<IReadOnlyList<MovieMetadata>> pages)
    {
        if (pages.Count is < 2 or > 3) throw new ArgumentOutOfRangeException(nameof(pages));
        static string? Key(MovieMetadata movie)
            => movie.Source.Equals("JavDB", StringComparison.OrdinalIgnoreCase)
               && TryCandidateUrl(movie.SourceUrl, out var url)
               && movie.Code.Length is >= 2 and <= 40
                ? url + "|" + movie.Code.Trim().ToUpperInvariant() : null;
        var common = pages.Skip(1)
            .Select(page => page.Select(Key).Where(key => key is not null).ToHashSet(StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return pages[0].Where(movie => Key(movie) is string key && common.All(set => set.Contains(key)))
            .DistinctBy(movie => Key(movie), StringComparer.OrdinalIgnoreCase).Take(48)
            .Select(movie => movie.Clone()).ToList();
    }

    // The source has supplied each single-tag URL on a verified detail page.
    // Intersect up to three observed pages per public tag locally instead of
    // guessing undocumented combined-filter URLs. This is a bounded preview.
    public static async Task<List<MovieMetadata>> FetchTagIntersectionAsync(
        IReadOnlyList<JavDbCatalogLink> selectedTags, CancellationToken cancellationToken = default)
    {
        if (selectedTags.Count is < 2 or > 3) throw new ArgumentOutOfRangeException(nameof(selectedTags), "请选择 2–3 个标签。 ");
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in selectedTags)
            if (tag.Kind != "标签" || !TryCatalogUrl(tag.Url, out var url, out var kind)
                || kind != "标签" || !urls.Add(url))
                throw new InvalidOperationException("组合标签只能使用作品详情页提供的 2–3 个不同标签链接。 ");
        var pages = new List<IReadOnlyList<MovieMetadata>>();
        foreach (var url in urls)
        {
            var tagMovies = new List<MovieMetadata>();
            for (var page = 1; page <= 3; page++)
            {
                var result = await FetchCatalogPageWithNavigationAsync(url, page, cancellationToken);
                tagMovies.AddRange(result.Movies);
                if (!result.HasNextPage) break;
            }
            pages.Add(tagMovies);
        }
        return IntersectCatalogPages(pages);
    }
}

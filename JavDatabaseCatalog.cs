using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// A relation must be observed on an exact-code detail page before it can be
// browsed. A display name alone is never enough to construct a catalog URL.
internal sealed record JavDatabaseRelationLink(string Kind, string Name, string Url, string OriginCode, string OriginMovieUrl)
{
    public string DisplayName => $"{Kind} · {Name} · 来自 {OriginCode}";
}

internal sealed record JavDatabaseRelationPage(IReadOnlyList<MovieMetadata> Candidates, string? NextPageUrl, int Page);

internal static partial class JavDatabaseCatalog
{
    const int MaximumHtmlLength = 2_000_000;
    static readonly Uri Site = new("https://www.javdatabase.com/");
    static readonly Regex CatalogPath = new(@"^/(?<kind>idols|series)/[a-z0-9][a-z0-9-]{0,239}/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DetailRow = new(@"<p\b[^>]*class\s*=\s*(['""'])[^'""']*\bmb-1\b[^'""']*\1[^>]*>\s*<b\b[^>]*>(?<label>.*?)</b>(?<body>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Anchor = new(@"<a\b(?<attributes>[^>]*)>(?<body>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Attribute = new(@"(?<key>[\w:-]+)\s*=\s*(['""'])(?<value>.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex LinkTag = new(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Heading = new(@"<h1\b[^>]*>(?<body>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex PageQuery = new(@"^\?ipage=(?<page>\d{1,2})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly HttpClient Client = CreateClient();
    static readonly SemaphoreSlim RequestGate = new(1, 1);
    static DateTimeOffset nextRequest = DateTimeOffset.MinValue;

    static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        }) { Timeout = TimeSpan.FromSeconds(18) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    internal static bool TryCatalogUrl(string? raw, out string url, out string kind)
    {
        url = ""; kind = "";
        if (!Uri.TryCreate(Site, WebUtility.HtmlDecode(raw ?? ""), out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 || uri.Query.Length != 0 ||
            uri.Host is not ("www.javdatabase.com" or "javdatabase.com") ||
            !CatalogPath.IsMatch(uri.AbsolutePath)) return false;
        url = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        kind = CatalogPath.Match(uri.AbsolutePath).Groups["kind"].Value.Equals("idols", StringComparison.OrdinalIgnoreCase)
            ? "演员" : "系列";
        return true;
    }

    internal static IReadOnlyList<JavDatabaseRelationLink> ExtractVerifiedCatalogLinks(MovieMetadata candidate, string detailHtml)
    {
        if (candidate.Source != "JAVDatabase" ||
            !JavDatabaseScraper.IsVerifiedDetailSource(candidate.Code, candidate.SourceUrl))
            throw new InvalidOperationException("必须先选定 JAVDatabase 的有效番号作品详情。 ");
        // ParseDetail checks the URL slug, H1 and DVD ID independently. A
        // sidebar link or a search card cannot become relation evidence.
        var verified = JavDatabaseScraper.ParseDetail(candidate, detailHtml);
        if (!verified.DetailFetched || !verified.DetailIdentityVerified ||
            !ScrapeCodeIdentity.Same(candidate.Code, verified.Code))
            throw new InvalidOperationException("JAVDatabase 作品详情未核对同一番号。 ");
        var actors = verified.Actors.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var links = new List<JavDatabaseRelationLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match row in DetailRow.Matches(detailHtml))
        {
            var label = CleanText(row.Groups["label"].Value).TrimEnd(':', '：');
            var expectedKind = label.Equals("Idol(s)/Actress(es)", StringComparison.OrdinalIgnoreCase)
                ? "演员" : label is "JAV Series" or "Series" ? "系列" : "";
            if (expectedKind.Length == 0) continue;
            foreach (Match anchor in Anchor.Matches(row.Groups["body"].Value))
            {
                var name = CleanText(anchor.Groups["body"].Value);
                var href = ReadAttribute(anchor.Groups["attributes"].Value, "href");
                if (name.Length is < 2 or > 240 ||
                    (expectedKind == "演员" ? !actors.Contains(name) :
                        !string.Equals(verified.Series, name, StringComparison.OrdinalIgnoreCase)) ||
                    !TryCatalogUrl(href, out var catalogUrl, out var actualKind) || actualKind != expectedKind ||
                    !seen.Add(catalogUrl)) continue;
                links.Add(new JavDatabaseRelationLink(actualKind, name, catalogUrl, verified.Code, verified.SourceUrl));
                if (links.Count == 20) return links;
            }
        }
        return links;
    }

    internal static async Task<IReadOnlyList<JavDatabaseRelationLink>> FetchVerifiedCatalogLinksAsync(
        MovieMetadata candidate, CancellationToken cancellationToken = default)
    {
        if (candidate.Source != "JAVDatabase" ||
            !JavDatabaseScraper.IsVerifiedDetailSource(candidate.Code, candidate.SourceUrl))
            throw new InvalidOperationException("演员和系列关系仅可从 JAVDatabase 的番号详情读取。 ");
        var html = await GetHtmlAsync(candidate.SourceUrl, cancellationToken);
        return ExtractVerifiedCatalogLinks(candidate, html);
    }

    internal static bool TryCatalogPageUrl(string? raw, JavDatabaseRelationLink link, out string url, out int page)
    {
        url = ""; page = 0;
        if (!TryCatalogUrl(link.Url, out var root, out var kind) || kind != link.Kind ||
            !JavDatabaseScraper.IsVerifiedDetailSource(link.OriginCode, link.OriginMovieUrl) ||
            !Uri.TryCreate(Site, WebUtility.HtmlDecode(raw ?? ""), out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host is not ("www.javdatabase.com" or "javdatabase.com") ||
            !string.Equals(uri.AbsolutePath.TrimEnd('/'), new Uri(root).AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return false;
        if (uri.Query.Length == 0) page = 1;
        else
        {
            var match = PageQuery.Match(uri.Query);
            if (!match.Success || !int.TryParse(match.Groups["page"].Value, out page) || page is < 2 or > 20)
                return false;
        }
        url = page == 1 ? root : root + "?ipage=" + page;
        return true;
    }

    internal static JavDatabaseRelationPage ParseVerifiedCatalogPage(JavDatabaseRelationLink link, string requestedUrl, string html)
    {
        if (!TryCatalogPageUrl(requestedUrl, link, out var safePageUrl, out var page))
            throw new InvalidOperationException("关系作品目录地址无效。 ");
        EnsureNormalPage(html);
        var canonical = LinkTag.Matches(html).Cast<Match>()
            .FirstOrDefault(tag => ReadAttribute(tag.Value, "rel")?.Equals("canonical", StringComparison.OrdinalIgnoreCase) == true);
        if (canonical is null ||
            !TryCatalogPageUrl(ReadAttribute(canonical.Value, "href"), link, out var canonicalUrl, out _) ||
            !string.Equals(canonicalUrl, safePageUrl, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("关系目录未给出预期的规范地址，不能确认来源身份。 ");
        var heading = Heading.Match(html);
        var suffix = link.Kind == "演员" ? @"\s+-\s+JAV Profile$" : @"\s+JAV Videos$";
        var headingName = heading.Success ? Regex.Replace(CleanText(heading.Groups["body"].Value),
            suffix, "", RegexOptions.IgnoreCase) : "";
        if (!string.Equals(headingName, link.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("关系目录标题与作品详情中的名称不一致。 ");

        // The site's own movie-card parser validates each /movies/<code>/ URL
        // against the printed code. These are candidates, not accepted data.
        var candidates = JavDatabaseScraper.ParseTitleSearch(html);
        string? next = null;
        if (page < 20)
        {
            foreach (Match anchor in Anchor.Matches(html))
            {
                var href = ReadAttribute(anchor.Groups["attributes"].Value, "href");
                if (!TryCatalogPageUrl(href, link, out var parsed, out var number) || number != page + 1) continue;
                next = parsed;
                break;
            }
        }
        return new JavDatabaseRelationPage(candidates, next, page);
    }

    internal static async Task<JavDatabaseRelationPage> FetchCatalogPageAsync(JavDatabaseRelationLink link,
        string? pageUrl = null, CancellationToken cancellationToken = default)
    {
        pageUrl ??= link.Url;
        if (!TryCatalogPageUrl(pageUrl, link, out var safeUrl, out _))
            throw new InvalidOperationException("只允许浏览已核对作品详情提供的关系目录及其公开分页。 ");
        return ParseVerifiedCatalogPage(link, safeUrl, await GetHtmlAsync(safeUrl, cancellationToken));
    }

    static async Task<string> GetHtmlAsync(string url, CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            var wait = nextRequest - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : null);
                throw new ScrapeHttpException($"JAVDatabase 返回 {(int)response.StatusCode}，请稍后重试。", response.StatusCode, delay);
            }
            if (response.Content.Headers.ContentType?.MediaType is not "text/html" ||
                response.Content.Headers.ContentLength is > MaximumHtmlLength)
                throw new InvalidOperationException("关系目录没有返回可读取的 HTML 页面。 ");
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureNormalPage(html);
            return html;
        }
        finally
        {
            nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(900);
            RequestGate.Release();
        }
    }

    static void EnsureNormalPage(string html)
    {
        if (html.Length < 500 || Regex.IsMatch(html,
            @"(?:Access Denied|not available in your region|Age Verification|verify you are human|cf-browser-verification|Just a moment\.\.\.)",
            RegexOptions.IgnoreCase))
            throw new InvalidOperationException("JAVDatabase 关系目录不可用或需要验证；不能当作没有作品。 ");
        if (html.Length > MaximumHtmlLength)
            throw new InvalidOperationException("JAVDatabase 关系目录超出安全大小限制。 ");
    }

    static string? ReadAttribute(string element, string key) => Attribute.Matches(element).Cast<Match>()
        .FirstOrDefault(match => match.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } found
        ? WebUtility.HtmlDecode(found.Groups["value"].Value) : null;

    static string CleanText(string html) => Regex.Replace(WebUtility.HtmlDecode(HtmlTag.Replace(html, " ")),
        @"\s+", " ").Trim();
}

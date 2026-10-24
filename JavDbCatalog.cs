using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

public static partial class JavDbScraper
{
    static readonly Regex CatalogPath = new(@"^/(?<kind>actors|series)/[A-Za-z0-9_-]{1,64}/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex TagFilter = new(@"^c[0-9]{1,2}=[0-9]{1,6}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CatalogAnchor = new("""<a\b(?<attrs>[^>]{0,1000})>(?<name>.*?)</a>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex AnchorHref = new("""\bhref\s*=\s*['\"](?<href>[^'\"]+)['\"]""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Only links actually supplied by a detail page can be navigated. Never
    // build an actor/tag/series ID from a display name or use an unverified API.
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

    public static async Task<List<MovieMetadata>> FetchCatalogPageAsync(string catalogUrl, int page,
        CancellationToken cancellationToken = default)
    {
        if (!TryCatalogUrl(catalogUrl, out var safeUrl, out _))
            throw new InvalidOperationException("关系目录必须是 JavDB 详情页提供的演员、系列或标签链接。");
        if (page is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(page), "只允许手动浏览前 20 页。");
        var requestUrl = safeUrl + (page == 1 ? "" : (safeUrl.Contains('?') ? "&page=" : "?page=") + page);
        using var response = await Client.GetAsync(requestUrl, HttpCompletionOption.ResponseContentRead, cancellationToken);
        EnsureSuccessfulResponse(response);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseCatalogPage(html);
    }
}

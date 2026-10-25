using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

internal sealed record JavDatabaseActorSearchHit(string Name, string Url);

internal static partial class JavDatabaseCatalog
{
    static readonly Regex ActorCard = new(@"<p\b[^>]*\bclass\s*=\s*(['""'])[^'""']*\bpcard\b[^'""']*\1[^>]*>(?<body>.*?)</p>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    internal static bool TryActorSearchName(string? name, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(name)) return false;
        var value = name.Trim();
        if (value.Length is < 2 or > 80 || value.Any(char.IsControl) ||
            value.IndexOfAny(['/', '\\', ':', '?', '#', '&', '=']) >= 0) return false;
        normalized = NormalizeActorName(value);
        return normalized.Length >= 2;
    }

    static string NormalizeActorName(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC)
        .Replace('亞', '亜').Replace('亚', '亜')
        .Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();

    static bool IsExpectedSearchCanonical(string? raw, string normalized)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host is not ("www.javdatabase.com" or "javdatabase.com") ||
            uri.AbsolutePath != "/" || !uri.Query.StartsWith("?s=", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Contains('&')) return false;
        return NormalizeActorName(WebUtility.UrlDecode(uri.Query[3..])) == normalized;
    }

    // The public search form on JAVDatabase uses GET /?s=<name>. It returns
    // mixed movie and idol cards, so only a same-name /idols/ card is a lead.
    // A lead is not yet a verified relationship and cannot be used to write
    // metadata or to construct a made-up idol URL.
    internal static IReadOnlyList<JavDatabaseActorSearchHit> ParseActorSearchPage(string actorName, string html)
    {
        if (!TryActorSearchName(actorName, out var normalized))
            throw new ArgumentException("演员姓名需为 2–80 字，不能包含路径或网址字符。", nameof(actorName));
        EnsureNormalPage(html);
        var canonical = LinkTag.Matches(html).Cast<Match>()
            .FirstOrDefault(tag => ReadAttribute(tag.Value, "rel")?.Equals("canonical", StringComparison.OrdinalIgnoreCase) == true);
        if (canonical is null || !IsExpectedSearchCanonical(ReadAttribute(canonical.Value, "href"), normalized))
            throw new InvalidOperationException("JAVDatabase 搜索页未确认当前演员姓名和来源地址。 ");
        var heading = Heading.Match(html);
        var headingText = heading.Success ? CleanText(heading.Groups["body"].Value) : "";
        var headingName = Regex.Match(headingText, @"^Search Results for:\s*(?<name>.+)$", RegexOptions.IgnoreCase);
        var noResults = Regex.IsMatch(html, @"No results found\.\s*Please adjust your search term\.", RegexOptions.IgnoreCase);
        if ((!headingName.Success || NormalizeActorName(headingName.Groups["name"].Value) != normalized) && !noResults)
            throw new InvalidOperationException("JAVDatabase 搜索页标题与演员姓名不一致，或页面结构已变化。 ");
        if (noResults) return [];

        var hits = new List<JavDatabaseActorSearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match card in ActorCard.Matches(html).Cast<Match>().Take(100))
        {
            var anchor = Anchor.Match(card.Groups["body"].Value);
            if (!anchor.Success) continue;
            var name = CleanText(anchor.Groups["body"].Value);
            if (NormalizeActorName(name) != normalized ||
                !TryCatalogUrl(ReadAttribute(anchor.Groups["attributes"].Value, "href"), out var url, out var kind) ||
                kind != "演员" || !seen.Add(url)) continue;
            hits.Add(new JavDatabaseActorSearchHit(name, url));
            if (hits.Count == 4) break;
        }
        return hits;
    }

    // The actor profile is a second, independent identity check. Its movie
    // cards are still only leads; the corresponding detail must verify DVD ID
    // and list this exact actor before the relationship becomes browseable.
    internal static IReadOnlyList<MovieMetadata> ParseVerifiedActorProfile(JavDatabaseActorSearchHit hit, string html)
    {
        if (!TryCatalogUrl(hit.Url, out var root, out var kind) || kind != "演员" ||
            !TryActorSearchName(hit.Name, out var normalized))
            throw new ArgumentException("演员目录地址或姓名无效。", nameof(hit));
        EnsureNormalPage(html);
        var canonical = LinkTag.Matches(html).Cast<Match>()
            .FirstOrDefault(tag => ReadAttribute(tag.Value, "rel")?.Equals("canonical", StringComparison.OrdinalIgnoreCase) == true);
        if (canonical is null ||
            !TryCatalogUrl(ReadAttribute(canonical.Value, "href"), out var canonicalUrl, out var canonicalKind) ||
            canonicalKind != "演员" || !string.Equals(canonicalUrl, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("演员目录未给出预期的规范地址。 ");
        var heading = Heading.Match(html);
        var headingText = heading.Success ? CleanText(heading.Groups["body"].Value) : "";
        var headingName = Regex.Replace(headingText, @"\s+-\s+JAV Profile$", "", RegexOptions.IgnoreCase);
        if (NormalizeActorName(headingName) != normalized ||
            string.Equals(headingName, headingText, StringComparison.Ordinal))
            throw new InvalidOperationException("演员目录标题与搜索结果的姓名不一致。 ");
        return JavDatabaseScraper.ParseTitleSearch(html);
    }

    public static async Task<IReadOnlyList<JavDatabaseRelationLink>> FindActorLinksByNameAsync(string actorName,
        CancellationToken cancellationToken = default)
    {
        if (!TryActorSearchName(actorName, out _))
            throw new ArgumentException("演员姓名需为 2–80 字，不能包含路径或网址字符。", nameof(actorName));
        var searchUrl = new Uri(Site, "?s=" + Uri.EscapeDataString(actorName.Trim())).AbsoluteUri;
        var hits = ParseActorSearchPage(actorName, await GetHtmlAsync(searchUrl, cancellationToken));
        var verified = new List<JavDatabaseRelationLink>();
        Exception? lastError = null;
        foreach (var hit in hits.Take(2))
        {
            IReadOnlyList<MovieMetadata> movies;
            try { movies = ParseVerifiedActorProfile(hit, await GetHtmlAsync(hit.Url, cancellationToken)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastError = ex; continue; }

            foreach (var movie in movies.Take(3))
            {
                try
                {
                    var links = ExtractVerifiedCatalogLinks(movie,
                        await GetHtmlAsync(movie.SourceUrl, cancellationToken));
                    var exact = links.FirstOrDefault(link => link.Kind == "演员" &&
                        string.Equals(link.Url, hit.Url, StringComparison.OrdinalIgnoreCase) &&
                        NormalizeActorName(link.Name) == NormalizeActorName(hit.Name));
                    if (exact is null) continue;
                    verified.Add(exact);
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { lastError = ex; }
            }
        }
        if (verified.Count == 0 && lastError is not null)
            throw new InvalidOperationException("公开演员结果未能通过作品详情核实；不能据此判断演员不存在。", lastError);
        return verified.DistinctBy(link => link.Url, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

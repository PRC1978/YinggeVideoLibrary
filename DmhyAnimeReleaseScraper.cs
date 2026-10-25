using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace YinggeVideoLibrary;

// A DMHY item is a user-published release, not a verified anime catalog entry.
// In particular, the RSS enclosure and description are deliberately ignored.
public sealed record DmhyAnimeRelease
{
    public required string ReleaseTitle { get; init; }
    public required string WorkTitle { get; init; }
    public int? Season { get; init; }
    public string? Episode { get; init; }
    public string? SubtitleGroup { get; init; }
    public string? Resolution { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public required string SourceUrl { get; init; }
    public string? Category { get; init; }
}

public static class DmhyAnimeReleaseScraper
{
    const string Site = "https://share.dmhy.org";
    const int MaxResponseBytes = 4_000_000;
    const int MaxResults = 50;
    static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate |
            DecompressionMethods.Brotli,
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(20) };
    static readonly SemaphoreSlim RequestGate = new(1, 1);
    static DateTimeOffset nextRequestAt;
    static readonly Regex PagePath = new(
        @"^/topics/view/[1-9][0-9]{0,11}(?:_[^/\\?#]{1,300})?\.html$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    public static async Task<List<DmhyAnimeRelease>> SearchAsync(string query,
        CancellationToken cancellationToken = default)
    {
        query = NormalizeQuery(query);
        if (query.Length == 0) return [];
        // The keyword endpoint is documented in DMHY's public FAQ. No login,
        // torrent endpoint, magnet URI or enclosure is ever requested.
        var url = Site + "/topics/rss/rss.xml?keyword=" + Uri.EscapeDataString(query);
        var xml = await GetRssAsync(url, cancellationToken);
        return ParseRss(xml);
    }

    internal static string NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        query = query.Trim();
        if (query.Length > 160) query = query[..160].TrimEnd();
        if (query.Any(char.IsControl) || query.StartsWith('/') || query.Contains('\\') ||
            Uri.TryCreate(query, UriKind.Absolute, out _) ||
            (query.Length >= 2 && char.IsLetter(query[0]) && query[1] == ':'))
            throw new ArgumentException("动漫花园只接受番剧名称，不接受文件路径或网址。", nameof(query));
        return query;
    }

    static async Task<string> GetRssAsync(string url, CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            var delay = nextRequestAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            nextRequestAt = DateTimeOffset.UtcNow.AddSeconds(5);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rss+xml"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
            var version = typeof(DmhyAnimeReleaseScraper).Assembly.GetName().Version?.ToString(3) ?? "1.5.3";
            request.Headers.TryAddWithoutValidation("User-Agent",
                $"PRC1978/YinggeVideoLibrary/{version} (https://github.com/PRC1978/YinggeVideoLibrary)");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var header = response.Headers.RetryAfter;
                var retry = header?.Delta ?? (header?.Date is DateTimeOffset date
                    ? date - DateTimeOffset.UtcNow : null);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or
                    HttpStatusCode.ServiceUnavailable)
                {
                    var fallback = response.StatusCode == HttpStatusCode.Forbidden
                        ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1);
                    var pause = retry is { } specified && specified > TimeSpan.Zero ? specified : fallback;
                    nextRequestAt = DateTimeOffset.UtcNow.Add(pause > TimeSpan.FromHours(24)
                        ? TimeSpan.FromHours(24) : pause);
                }
                throw new ScrapeHttpException($"动漫花园返回 HTTP {(int)response.StatusCode}，稍后重试。",
                    response.StatusCode, retry);
            }
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new InvalidDataException("动漫花园 RSS 超出解析上限。");
            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken);
            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.Content.Headers.ContentType?.MediaType?.Contains("html",
                    StringComparison.OrdinalIgnoreCase) == true ||
                xml.AsSpan().TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                xml.AsSpan().TrimStart().StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase))
            {
                nextRequestAt = DateTimeOffset.UtcNow.AddMinutes(10);
                throw new InvalidDataException("动漫花园返回网页验证或访问限制，RSS 未读取；稍后再试。");
            }
            return xml;
        }
        finally { RequestGate.Release(); }
    }

    internal static List<DmhyAnimeRelease> ParseRss(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new InvalidDataException("动漫花园 RSS 为空，不能判断为无结果。");
        XDocument document;
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxResponseBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true
            });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("动漫花园 RSS 不是可安全读取的 XML。", ex);
        }
        if (document.Root?.Name.LocalName != "rss" ||
            document.Root.Elements().FirstOrDefault(element => element.Name.LocalName == "channel")
                is not { } channel)
            throw new InvalidDataException("动漫花园返回内容不是 RSS 频道，不能判断为无结果。");

        var results = new List<DmhyAnimeRelease>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in channel.Elements().Where(element => element.Name.LocalName == "item"))
        {
            // Read only metadata fields. Never inspect enclosure, description or
            // arbitrary links embedded in them.
            var title = Field(item, "title", 500);
            var link = Field(item, "link", 600);
            var category = Field(item, "category", 40);
            if (title is null || link is null || !IsAnimeCategory(category) ||
                !TryPageUrl(link, out var sourceUrl) || !seen.Add(sourceUrl)) continue;
            var parsed = AnimeReleaseTitleParser.Analyze(title);
            if (parsed.SeriesTitle.Length == 0) continue;
            var dateText = Field(item, "pubDate", 80);
            DateTimeOffset? publishedAt = DateTimeOffset.TryParse(dateText,
                CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date) ? date : null;
            results.Add(new DmhyAnimeRelease
            {
                ReleaseTitle = title,
                WorkTitle = parsed.SeriesTitle,
                Season = parsed.Season,
                Episode = parsed.Episode,
                SubtitleGroup = parsed.SubtitleGroup,
                Resolution = parsed.Resolution,
                PublishedAt = publishedAt,
                SourceUrl = sourceUrl,
                Category = category
            });
            if (results.Count >= MaxResults) break;
        }
        return results;
    }

    static string? Field(XElement parent, string name, int maxLength)
    {
        var value = parent.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim();
        return value is { Length: > 0 } && value.Length <= maxLength && !value.Any(char.IsControl)
            ? value : null;
    }

    static bool IsAnimeCategory(string? category)
        => category is "動畫" or "动画" or "季度全集" or "RAW" or "RＡＷ" or "完結番組" or "完结番组";

    internal static bool TryPageUrl(string? value, out string canonicalUrl)
    {
        canonicalUrl = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !uri.Host.Equals("share.dmhy.org", StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme == "http" && uri.Port != 80) ||
            (uri.Scheme == "https" && uri.Port != 443) ||
            !PagePath.IsMatch(uri.AbsolutePath) ||
            uri.AbsolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("%5c", StringComparison.OrdinalIgnoreCase))
            return false;
        canonicalUrl = Site + uri.AbsolutePath;
        return true;
    }
}

internal sealed record ParsedAnimeReleaseTitle(string SeriesTitle, int? Season, string? Episode,
    string? SubtitleGroup, string? Resolution);

public static class AnimeReleaseTitleParser
{
    static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    static readonly Regex LeadingBracket = new(
        @"^\s*(?:\[(?<text>[^\]\r\n]{1,80})\]|【(?<text>[^】\r\n]{1,80})】)\s*",
        RegexOptions.CultureInvariant, Timeout);
    static readonly Regex Promo = new(@"^\s*★[^★\r\n]{1,35}★\s*",
        RegexOptions.CultureInvariant, Timeout);
    static readonly Regex SeasonNumber = new(
        @"(?i)(?:\bS(?<n>[0-9]{1,2})(?![0-9A-Za-z])|\bSeason\s*(?<n>[0-9]{1,2})\b|\b(?<n>[0-9]{1,2})(?:st|nd|rd|th)\s+Season\b|第\s*(?<cn>[一二三四五六七八九十0-9]{1,3})\s*季)",
        RegexOptions.CultureInvariant, Timeout);
    static readonly Regex MultiSeason = new(@"(?i)\bS[0-9]{1,2}\s*(?:[-+~]|and)\s*S[0-9]{1,2}\b",
        RegexOptions.CultureInvariant, Timeout);
    static readonly Regex EpisodeNumber = new(
        @"(?i)(?:\bS[0-9]{1,2}E(?<n>[0-9]{1,3})\b|\b(?:EP?|Episode)[\s._-]*(?<n>[0-9]{1,3})\b|第\s*(?<n>[0-9]{1,3})\s*[話话集]|[\[【](?<n>[0-9]{1,3}(?:[-~][0-9]{1,3})?)[\]】]|(?:\s[-|]\s)(?<n>[0-9]{1,3}(?:[-~][0-9]{1,3})?)(?![0-9]))",
        RegexOptions.CultureInvariant, Timeout);
    static readonly Regex ResolutionPattern = new(
        @"(?i)(?<![0-9])(?:3840[x×]2160|2160p|1920[x×]1080|1080p|1280[x×]720|720p|480p|4K)(?![0-9])",
        RegexOptions.CultureInvariant, Timeout);
    static readonly Regex MediaExtension = new(@"(?i)\.(?:mkv|mp4|avi|mov|webm|m2ts|ts|wmv|flv)$",
        RegexOptions.CultureInvariant, Timeout);
    static readonly Regex QualityStart = new(
        @"(?i)(?:\s[-|]\s*[0-9]{1,3}(?:[-~][0-9]{1,3})?(?:\b|\s)|\bS[0-9]{1,2}(?:E[0-9]{1,3})?\b|第\s*[一二三四五六七八九十0-9]{1,3}\s*季|\bSeason\s*[0-9]{1,2}\b|\b(?:2160p|1080p|720p|480p|BDRip|WebRip|HEVC|AV1|x264|x265)\b|[\[【（(])",
        RegexOptions.CultureInvariant, Timeout);
    static readonly Regex Space = new(@"\s+", RegexOptions.CultureInvariant, Timeout);

    public static string CleanWorkTitle(string? releaseTitle) => Analyze(releaseTitle).SeriesTitle;

    internal static ParsedAnimeReleaseTitle Analyze(string? releaseTitle)
    {
        if (string.IsNullOrWhiteSpace(releaseTitle)) return new("", null, null, null, null);
        var value = MediaExtension.Replace(releaseTitle.Trim(), "");
        if (value.Length > 600) value = value[..600];
        var season = ParseSeason(value);
        var episode = EpisodeNumber.Match(value).Groups["n"].Value;
        var resolution = ResolutionPattern.Match(value).Value;
        var group = (string?)null;

        var first = LeadingBracket.Match(value);
        if (first.Success)
        {
            var rest = RemovePromo(value[first.Length..].TrimStart());
            var second = LeadingBracket.Match(rest);
            if ((second.Success && (LooksLikeTitle(second.Groups["text"].Value) ||
                    LooksLikeUnbracketedTitle(rest[second.Length..].TrimStart()))) ||
                (!second.Success && LooksLikeUnbracketedTitle(rest)))
            {
                group = first.Groups["text"].Value.Trim();
                value = rest;
            }
        }
        value = RemovePromo(value.TrimStart());
        // Some groups add a second language/distributor badge before the work
        // name. Keep that badge out of title matching and preserve the first
        // group as release provenance.
        for (var i = 0; i < 2; i++)
        {
            var badge = LeadingBracket.Match(value);
            if (!badge.Success || !LooksLikeUnbracketedTitle(value[badge.Length..].TrimStart())) break;
            value = value[badge.Length..].TrimStart();
        }
        var titleBracket = LeadingBracket.Match(value);
        var title = titleBracket.Success && LooksLikeTitle(titleBracket.Groups["text"].Value)
            ? titleBracket.Groups["text"].Value : value;
        if (!titleBracket.Success)
        {
            var stop = QualityStart.Match(title);
            if (stop.Success) title = title[..stop.Index];
        }
        title = SelectPrimaryAlias(title);
        title = Space.Replace(title.Trim().Trim('-', '_', '.', '|', '·', '：', ':'), " ");
        if (!LooksLikeTitle(title)) title = "";
        return new(title, season, episode.Length == 0 ? null : episode,
            group, resolution.Length == 0 ? null : resolution);
    }

    static string RemovePromo(string value)
    {
        for (var i = 0; i < 2; i++)
        {
            var match = Promo.Match(value);
            if (!match.Success) break;
            value = value[match.Length..].TrimStart();
        }
        return value;
    }

    static bool LooksLikeUnbracketedTitle(string value)
    {
        if (value.Length == 0 || value[0] is '-' or '|' or '(' or '（') return false;
        var segment = QualityStart.Match(value) is { Success: true } stop
            ? value[..stop.Index] : value;
        return LooksLikeTitle(segment);
    }

    static bool LooksLikeTitle(string value)
    {
        value = value.Trim();
        if (value.Length is < 2 or > 240 || !value.Any(char.IsLetter)) return false;
        if (value.All(char.IsDigit) || ResolutionPattern.IsMatch(value) ||
            Regex.IsMatch(value, @"^(?i:BDRip|WebRip|HEVC|AV1|x264|x265|简体|繁体|繁日雙語|简日双语|简繁字幕)$",
                RegexOptions.CultureInvariant, Timeout) ||
            SeasonNumber.IsMatch(value) && value.Length <= 15)
            return false;
        return true;
    }

    static string SelectPrimaryAlias(string title)
    {
        var slash = title.IndexOf(" / ", StringComparison.Ordinal);
        if (slash < 0) slash = title.IndexOf('／');
        if (slash < 0)
        {
            var compactSlash = title.IndexOf('/');
            if (compactSlash > 0 && title[..compactSlash].Any(c => c > 127) &&
                title[(compactSlash + 1)..].Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
                slash = compactSlash;
        }
        return slash > 0 ? title[..slash].Trim() : title;
    }

    static int? ParseSeason(string title)
    {
        if (MultiSeason.IsMatch(title)) return null;
        var match = SeasonNumber.Match(title);
        if (!match.Success) return null;
        var raw = match.Groups["n"].Success ? match.Groups["n"].Value : match.Groups["cn"].Value;
        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return number is >= 1 and <= 99 ? number : null;
        var digit = "一二三四五六七八九".IndexOf(raw, StringComparison.Ordinal);
        if (digit >= 0) return digit + 1;
        return raw == "十" ? 10 : null;
    }
}

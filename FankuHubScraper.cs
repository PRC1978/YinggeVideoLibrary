using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// FankuHub publishes an unauthenticated anime index and work details. Only
// those public HTML pages are read; the authenticated pages and /api/ are not
// used. Search cards are suggestions, never verified metadata for a local file.
public static class FankuHubScraper
{
    const string SourceName = "FankuHub";
    const string Site = "https://fankuhub.com";
    const int MaxHtmlBytes = 1_000_000;
    static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);
    static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(20) };
    static readonly SemaphoreSlim RequestGate = new(1, 1);
    static DateTimeOffset nextRequestAt;

    static readonly Regex ResultCount = new(@"共\s*(?<count>[\d,]+)\s*条结果",
        RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex Article = new(@"<article\b[^>]*>(?<body>.*?)</article>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex AnimeLink = new("""<a\b[^>]*\bhref\s*=\s*["']/anime/(?<id>[a-z0-9]{8,40})["'][^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex CardTitle = new("""<p\b[^>]*\btitle\s*=\s*["'](?<title>[^"']{1,800})["'][^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex CardPoster = new("""<img\b[^>]*\bsrc\s*=\s*["'](?<url>[^"']{1,1000})["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex YearMonth = new(@"(?<year>(?:19|20)\d{2})年(?:1[0-2]|[1-9])月",
        RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex JsonLd = new("""<script\b[^>]*\btype\s*=\s*["']application/ld\+json["'][^>]*>(?<json>.*?)</script>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex SummaryBlock = new("""<div\b[^>]*\bdata-collapsible-body\s*=\s*["']true["'][^>]*>(?<body>.{0,12000}?)</div>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex HtmlTags = new(@"<[^>]+>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, RegexTimeout);

    public static async Task<List<MovieMetadata>> SearchAsync(string query,
        CancellationToken cancellationToken = default)
    {
        query = NormalizeQuery(query);
        if (query.Length == 0) return [];
        var html = await GetHtmlAsync(Site + "/anime?q=" + Uri.EscapeDataString(query), cancellationToken);
        return ParseSearch(html);
    }

    public static async Task<MovieMetadata> FetchFullDetailsAsync(MovieMetadata candidate,
        CancellationToken cancellationToken = default)
    {
        if (candidate is null || !candidate.Source.Equals(SourceName, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(candidate.Code) || !TryAnimeId(candidate.SourceUrl, out var id))
            throw new InvalidDataException("番库候选缺少有效的番剧条目链接。");
        var html = await GetHtmlAsync(Site + "/anime/" + id, cancellationToken);
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
            throw new ArgumentException("番库只接受作品标题，不接受文件路径或网址。", nameof(query));
        return query;
    }

    static async Task<string> GetHtmlAsync(string url, CancellationToken cancellationToken)
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
            var version = typeof(FankuHubScraper).Assembly.GetName().Version?.ToString(3) ?? "1.5.3";
            request.Headers.TryAddWithoutValidation("User-Agent",
                $"PRC1978/YinggeVideoLibrary/{version} (https://github.com/PRC1978/YinggeVideoLibrary)");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var header = response.Headers.RetryAfter;
                var retry = header?.Delta ?? (header?.Date is DateTimeOffset date
                    ? date - DateTimeOffset.UtcNow : null);
                var effectiveRetry = retry;
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden or
                    HttpStatusCode.ServiceUnavailable)
                {
                    var fallback = response.StatusCode switch
                    {
                        HttpStatusCode.Forbidden => TimeSpan.FromHours(1),
                        HttpStatusCode.TooManyRequests => TimeSpan.FromMinutes(1),
                        _ => TimeSpan.FromSeconds(20)
                    };
                    var pause = retry is { } specified && specified > TimeSpan.Zero ? specified : fallback;
                    effectiveRetry = pause > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : pause;
                    nextRequestAt = DateTimeOffset.UtcNow.Add(effectiveRetry.Value);
                }
                throw new ScrapeHttpException($"番库返回 HTTP {(int)response.StatusCode}，稍后重试。",
                    response.StatusCode, effectiveRetry);
            }
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("番库返回的不是公开网页，未使用其内容。");
            if (response.Content.Headers.ContentLength > MaxHtmlBytes)
                throw new InvalidDataException("番库网页超出解析大小上限。");
            await response.Content.LoadIntoBufferAsync(MaxHtmlBytes, cancellationToken);
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (html.Contains("cf-chl", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("Just a moment...", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("番库要求额外访问验证，已停止读取该页面。");
            return html;
        }
        finally { RequestGate.Release(); }
    }

    internal static List<MovieMetadata> ParseSearch(string html)
    {
        if (string.IsNullOrWhiteSpace(html) ||
            !html.Contains("番剧索引", StringComparison.Ordinal) ||
            !html.Contains("搜索番剧标题", StringComparison.Ordinal))
            throw new InvalidDataException("番库搜索页面结构不完整，不能判断为无结果。");
        var countMatch = ResultCount.Match(html);
        if (!countMatch.Success || !int.TryParse(countMatch.Groups["count"].Value.Replace(",", ""),
                NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            throw new InvalidDataException("番库搜索页面没有可核对的结果数。");
        var results = new List<MovieMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match article in Article.Matches(html))
        {
            var body = article.Groups["body"].Value;
            var link = AnimeLink.Match(body);
            if (!link.Success) continue;
            var id = link.Groups["id"].Value.ToLowerInvariant();
            if (!seen.Add(id)) continue;
            var titles = CardTitle.Matches(body);
            var title = titles.Count > 0 ? CleanText(titles[0].Groups["title"].Value, 240) : null;
            if (title is null) continue;
            var original = titles.Count > 1 ? CleanText(titles[1].Groups["title"].Value, 240) : null;
            if (SameTitle(title, original)) original = null;
            var year = YearMonth.Match(body).Groups["year"].Value;
            var poster = PosterUrl(CardPoster.Match(body).Groups["url"].Value);
            var candidate = new MovieMetadata
            {
                Source = SourceName, SourceUrl = Site + "/anime/" + id,
                ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [SourceName] = id
                },
                Code = "", Title = title, OriginalTitle = original,
                ReleaseDate = year.Length == 4 ? year : null, PosterUrl = poster,
                DetailNote = "番库公开索引的搜索摘要；需要读取详情并由用户审核"
            };
            candidate.StampAvailableFields("搜索结果");
            results.Add(candidate);
            if (results.Count >= 10) break;
        }
        if (count > 0 && results.Count == 0)
            throw new InvalidDataException("番库报告有搜索结果，但未找到可核对的番剧卡片。");
        if (count == 0 && results.Count > 0)
            throw new InvalidDataException("番库搜索结果数与番剧卡片不一致，未采用该页面。");
        return results;
    }

    internal static MovieMetadata ParseDetail(MovieMetadata candidate, string html)
    {
        if (candidate is null || !candidate.Source.Equals(SourceName, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(candidate.Code) || !TryAnimeId(candidate.SourceUrl, out var id) ||
            string.IsNullOrWhiteSpace(html) || !html.Contains("<h1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("番库详情缺少所选番剧的公开页面。");
        JsonDocument? workDocument = null;
        try
        {
            foreach (Match script in JsonLd.Matches(html))
            {
                var json = script.Groups["json"].Value;
                if (json.Length > 128_000) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 40 }); }
                catch (JsonException) { continue; }
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    Text(document.RootElement, "@type", 30) is "TVSeries" or "Movie" or "VideoObject")
                {
                    workDocument = document;
                    break;
                }
                document.Dispose();
            }
            if (workDocument is null)
                throw new InvalidDataException("番库详情缺少可核对的作品结构化资料。");
            var work = workDocument.RootElement;
            if (Text(work, "@context", 100) != "https://schema.org")
                throw new InvalidDataException("番库详情的作品结构化资料来源不明。");
            if (!TryAnimeId(Text(work, "url", 300), out var returnedId) ||
                !id.Equals(returnedId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("番库详情作品编号与所选候选不一致。");
            var title = Text(work, "name", 240);
            var aliases = StringArray(work, "alternateName", 20, 240);
            if (title is null || !(SameTitle(candidate.Title, title) ||
                aliases.Any(alias => SameTitle(candidate.Title, alias))))
                throw new InvalidDataException("番库详情番名与搜索候选冲突；资料未采用。");
            var date = IsoDate(Text(work, "datePublished", 20));
            if (candidate.ReleaseDate is { Length: >= 4 } oldDate && date is { Length: >= 4 } newDate &&
                !oldDate[..4].Equals(newDate[..4], StringComparison.Ordinal))
                throw new InvalidDataException("番库详情的首播年份与搜索候选冲突；资料未采用。");
            var record = candidate.Clone();
            record.Source = SourceName;
            record.SourceUrl = Site + "/anime/" + id;
            record.Code = "";
            record.ExternalIds[SourceName] = id;
            record.Title = title;
            record.OriginalTitle = aliases.FirstOrDefault(alias => !SameTitle(alias, title))
                ?? candidate.OriginalTitle;
            record.ReleaseDate = date ?? candidate.ReleaseDate;
            record.PosterUrl = PosterUrl(Text(work, "image", 1000)) ?? candidate.PosterUrl;
            record.Genres = StringArray(work, "genre", 30, 80);
            record.Summary = Summary(html);
            record.RuntimeMinutes = LabeledMinutes(html, "时长");
            record.Studio = LabeledText(html, "制作", 200);
            if (work.TryGetProperty("aggregateRating", out var aggregate) && aggregate.ValueKind == JsonValueKind.Object &&
                Number(aggregate, "ratingValue") is double score and >= 0 and <= 10 &&
                Number(aggregate, "bestRating") is 10d &&
                Int(aggregate, "ratingCount") is int votes and > 0)
            {
                record.Rating = score.ToString("0.##", CultureInfo.InvariantCulture) + $"/10（{votes} 票）";
                RatingAggregation.AddOrUpdate(record, new PlatformRating
                {
                    Platform = SourceName, SourceUrl = record.SourceUrl, Value = score,
                    Maximum = 10, VoteCount = votes,
                    MatchEvidence = "番库公开详情页作品编号核对", CollectedAt = DateTime.UtcNow
                });
            }
            else record.Rating = null;
            record.RefreshChangedFieldProvenance(candidate, "番库公开详情页");
            record.DetailFetched = true;
            record.DetailIdentityVerified = true;
            var episodes = Int(work, "numberOfEpisodes");
            record.DetailNote = $"已核对番库公开详情的作品编号、标题和首播年份；{(episodes is > 0 ? $"共 {episodes} 话，" : "")}" +
                "时长为单集分钟数；未提供的字段保持空白。候选仍须人工审核";
            return record;
        }
        finally { workDocument?.Dispose(); }
    }

    internal static bool TryAnimeId(string? value, out string id)
    {
        id = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("fankuhub.com", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        var match = Regex.Match(uri.AbsolutePath, @"^/anime/(?<id>[a-z0-9]{8,40})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        if (!match.Success) return false;
        id = match.Groups["id"].Value.ToLowerInvariant();
        return true;
    }

    static string? CleanText(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = Whitespace.Replace(WebUtility.HtmlDecode(text), " ").Trim();
        return text.Length is > 0 and <= 20000 ? text.Length <= max ? text : text[..max].TrimEnd() : null;
    }

    static string? Text(JsonElement item, string property, int max)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()?.Trim();
        return text is { Length: > 0 } && text.Length <= max ? text : null;
    }

    static List<string> StringArray(JsonElement item, string property, int limit, int maxLength)
    {
        if (!item.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray().Take(limit).Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()?.Trim()).Where(value => value is { Length: > 0 } && value.Length <= maxLength)
            .Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static double? Number(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    static int? Int(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) && number >= 0 ? number : null;

    static string? IsoDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    static bool SameTitle(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(string.Concat(left.Normalize().Where(char.IsLetterOrDigit)),
            string.Concat(right.Normalize().Where(char.IsLetterOrDigit)), StringComparison.OrdinalIgnoreCase);

    static string? PosterUrl(string? value)
    {
        value = WebUtility.HtmlDecode(value ?? "");
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("assets.fankuhub.cn", StringComparison.OrdinalIgnoreCase) && uri.Port == 443 &&
            uri.UserInfo.Length == 0 && uri.AbsolutePath.StartsWith("/i/anime/", StringComparison.Ordinal) &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri.AbsoluteUri : null;
    }

    static string? Summary(string html)
    {
        var body = SummaryBlock.Match(html).Groups["body"].Value;
        if (body.Length == 0) return null;
        return CleanText(HtmlTags.Replace(body, " "), 4_000);
    }

    static string? LabeledText(string html, string label, int max)
    {
        var pattern = Regex.Escape(label) + @"</dt>\s*<dd\b[^>]*\btitle\s*=\s*[""'](?<value>[^""']{1,500})[""']";
        var match = Regex.Match(html, pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        return CleanText(match.Groups["value"].Value, max);
    }

    static int? LabeledMinutes(string html, string label)
    {
        var text = LabeledText(html, label, 80);
        if (text is null) return null;
        var match = Regex.Match(text, @"^(?<minutes>\d{1,3})\s*分$", RegexOptions.CultureInvariant, RegexTimeout);
        return match.Success && int.TryParse(match.Groups["minutes"].Value, out var minutes) && minutes is > 0 and <= 600
            ? minutes : null;
    }
}

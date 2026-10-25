using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// Tenrai serves a read-only, cached MyAnimeList catalogue. Search cards are
// suggestions only: a separate detail request must confirm MAL ID and title.
// This optional source never approves its own result.
public static class TenraiScraper
{
    const int MaxSearchBytes = 1_000_000;
    const int MaxDetailBytes = 750_000;
    static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(18) };
    static readonly SemaphoreSlim RequestGate = new(1, 1);
    static DateTimeOffset nextRequestAt;

    public static async Task<List<MovieMetadata>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        query = NormalizeQuery(query);
        if (query.Length == 0) return [];
        using var document = await GetJsonAsync("https://api.tenrai.org/v1/anime?q=" +
            Uri.EscapeDataString(query) + "&limit=10&sfw", MaxSearchBytes, cancellationToken);
        return ParseSearch(document.RootElement);
    }

    public static async Task<MovieMetadata> FetchFullDetailsAsync(MovieMetadata candidate,
        CancellationToken cancellationToken = default)
    {
        if (candidate is null || !candidate.Source.Equals("Tenrai", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(candidate.Code) || !TryAnimeId(candidate.SourceUrl, out var id))
            throw new InvalidDataException("Tenrai 候选缺少有效的 MyAnimeList 动画编号。");
        using var document = await GetJsonAsync($"https://api.tenrai.org/v1/anime/{id}",
            MaxDetailBytes, cancellationToken);
        return ParseDetail(candidate, document.RootElement);
    }

    internal static string NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        query = query.Trim();
        if (query.Length > 160) query = query[..160].TrimEnd();
        if (query.Any(char.IsControl) || query.StartsWith('/') || query.Contains('\\') ||
            Uri.TryCreate(query, UriKind.Absolute, out _) ||
            (query.Length >= 2 && char.IsLetter(query[0]) && query[1] == ':'))
            throw new ArgumentException("Tenrai 只接受作品标题，不接受文件路径或网址。", nameof(query));
        return query;
    }

    static async Task<JsonDocument> GetJsonAsync(string url, int maxBytes, CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            var delay = nextRequestAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            // Deliberately below the published public quota; this is a shared
            // service and a title-queue item can need both search and detail.
            nextRequestAt = DateTimeOffset.UtcNow.AddSeconds(5);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var version = typeof(TenraiScraper).Assembly.GetName().Version?.ToString(3) ?? "1.5.1";
            request.Headers.TryAddWithoutValidation("User-Agent",
                $"PRC1978/YinggeVideoLibrary/{version} (https://github.com/PRC1978/YinggeVideoLibrary)");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var header = response.Headers.RetryAfter;
                var retry = header?.Delta ?? (header?.Date is DateTimeOffset date
                    ? date - DateTimeOffset.UtcNow : null);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var fallback = response.StatusCode == HttpStatusCode.TooManyRequests
                        ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(20);
                    var pause = retry is { } specified && specified > TimeSpan.Zero ? specified : fallback;
                    nextRequestAt = DateTimeOffset.UtcNow.Add(pause > TimeSpan.FromHours(24)
                        ? TimeSpan.FromHours(24) : pause);
                }
                throw new ScrapeHttpException($"Tenrai 返回 HTTP {(int)response.StatusCode}，稍后重试。",
                    response.StatusCode, retry);
            }
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new InvalidDataException("Tenrai 响应超出解析上限。");
            await response.Content.LoadIntoBufferAsync(maxBytes, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 48 }, cancellationToken);
        }
        finally { RequestGate.Release(); }
    }

    internal static List<MovieMetadata> ParseSearch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Tenrai 搜索响应缺少动画列表，不能判断为无结果。");
        var entries = data.EnumerateArray().Take(10).ToList();
        var results = new List<MovieMetadata>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in entries)
        {
            var record = ParseAnime(item);
            if (record is null || !seen.Add(record.SourceUrl)) continue;
            record.DetailNote = "Tenrai 搜索摘要；MyAnimeList 条目尚未核对，须读取详情并人工审核";
            record.StampAvailableFields("搜索结果");
            results.Add(record);
        }
        if (entries.Count > 0 && results.Count == 0)
            throw new InvalidDataException("Tenrai 搜索条目缺少可核对的动画编号或标题。");
        return results;
    }

    internal static MovieMetadata ParseDetail(MovieMetadata candidate, JsonElement root)
    {
        if (!candidate.Source.Equals("Tenrai", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(candidate.Code) || !TryAnimeId(candidate.SourceUrl, out var expectedId) ||
            root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var item) ||
            ParseAnime(item) is not { } parsed || !TryAnimeId(parsed.SourceUrl, out var returnedId) ||
            expectedId != returnedId)
            throw new InvalidDataException("Tenrai 详情的动画编号与所选候选不一致。");
        if (!SameTitle(candidate.Title, item) || !SameYear(candidate.ReleaseDate, parsed.ReleaseDate))
            throw new InvalidDataException("Tenrai 详情的标题或发行年份与候选冲突；资料未采用。");
        var record = candidate.Clone();
        record.Source = "Tenrai";
        record.SourceUrl = parsed.SourceUrl;
        record.Code = "";
        record.Title = parsed.Title;
        record.OriginalTitle = parsed.OriginalTitle;
        record.ReleaseDate = parsed.ReleaseDate;
        record.PosterUrl = parsed.PosterUrl;
        record.Summary = parsed.Summary;
        record.Rating = parsed.Rating;
        record.RuntimeMinutes = parsed.RuntimeMinutes;
        record.TrailerUrl = parsed.TrailerUrl;
        record.Studio = JoinedRefs(item, "studios");
        record.Genres = Refs(item, "genres", 20).Concat(Refs(item, "themes", 12))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(25).ToList();
        record.RefreshChangedFieldProvenance(candidate, "Tenrai 动画详情（MyAnimeList 编号核验）");
        record.DetailFetched = true;
        record.DetailIdentityVerified = true;
        var mediaType = Text(item, "type", 30) ?? "类型未提供";
        var yearEvidence = candidate.ReleaseDate is { Length: >= 4 } && parsed.ReleaseDate is { Length: >= 4 }
            ? "年份一致" : "年份资料不足";
        record.DetailNote = $"已核对 Tenrai 提供的 MyAnimeList 编号和标题，{yearEvidence}；{mediaType}，时长为单集分钟数；可能是缓存资料，须人工审核";
        var rating = RatingAggregation.FromMetadata(record,
            "MyAnimeList 评分经 Tenrai 提供；已核对动画编号和标题");
        if (rating is not null)
        {
            rating.VoteCount = Number(item, "scored_by") is int votes and > 0 ? votes : null;
            record.FieldProvenance[nameof(MovieMetadata.Rating)] = new MetadataFieldProvenance
            {
                Source = "Tenrai", SourceUrl = record.SourceUrl,
                Stage = "动画详情：MyAnimeList 评分经 Tenrai 提供"
            };
            RatingAggregation.AddOrUpdate(record, rating);
        }
        return record;
    }

    static MovieMetadata? ParseAnime(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || Number(item, "mal_id") is not int id ||
            id <= 0 || id > 999_999_999) return null;
        var title = Text(item, "title", 240);
        if (title is null) return null;
        if (Text(item, "url", 500) is { } returnedUrl &&
            (!TryReturnedAnimeId(returnedUrl, out var returnedId) || returnedId != id)) return null;
        var japanese = Text(item, "title_japanese", 240);
        var releaseDate = Date(item);
        var record = new MovieMetadata
        {
            Source = "Tenrai", SourceUrl = $"https://myanimelist.net/anime/{id}",
            Title = title, OriginalTitle = japanese is not null && !SameNormalized(title, japanese)
                ? japanese : null,
            ReleaseDate = releaseDate, PosterUrl = Poster(item),
            Summary = CleanSummary(Text(item, "synopsis", 6_000)),
            RuntimeMinutes = Duration(Text(item, "duration", 80)),
            TrailerUrl = Trailer(item), Rating = Score(item)
        };
        return record;
    }

    static string? Date(JsonElement item)
    {
        if (item.TryGetProperty("aired", out var aired) && aired.ValueKind == JsonValueKind.Object &&
            Text(aired, "from", 40) is { } from &&
            DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                out var parsed)) return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Number(item, "year") is int year and >= 1900 and <= 2200
            ? year.ToString(CultureInfo.InvariantCulture) : null;
    }

    static string? Score(JsonElement item)
        => Decimal(item, "score") is decimal score and > 0 and <= 10
            ? score.ToString("0.##", CultureInfo.InvariantCulture) + "/10" : null;

    static string? Poster(JsonElement item)
    {
        if (!item.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object ||
            !images.TryGetProperty("jpg", out var jpg) || jpg.ValueKind != JsonValueKind.Object) return null;
        var value = Text(jpg, "large_image_url", 700) ?? Text(jpg, "image_url", 700);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("cdn.myanimelist.net", StringComparison.OrdinalIgnoreCase) &&
            uri.Port == 443 && uri.UserInfo.Length == 0 ? uri.ToString() : null;
    }

    static string? Trailer(JsonElement item)
    {
        if (!item.TryGetProperty("trailer", out var trailer) || trailer.ValueKind != JsonValueKind.Object) return null;
        var id = Text(trailer, "youtube_id", 20);
        return id is { Length: 11 } && Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$")
            ? "https://www.youtube.com/watch?v=" + id : null;
    }

    static int? Duration(string? text)
    {
        if (text is null) return null;
        var hours = Regex.Match(text, @"(?<n>\d{1,2})\s*hr");
        var minutes = Regex.Match(text, @"(?<n>\d{1,3})\s*min");
        var total = (hours.Success && int.TryParse(hours.Groups["n"].Value, out var h) ? h * 60 : 0) +
            (minutes.Success && int.TryParse(minutes.Groups["n"].Value, out var m) ? m : 0);
        return total is > 0 and <= 600 ? total : null;
    }

    static string? JoinedRefs(JsonElement item, string property)
    {
        var refs = Refs(item, property, 8);
        return refs.Count == 0 ? null : string.Join(" / ", refs);
    }

    static List<string> Refs(JsonElement item, string property, int limit)
        => item.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(value => Text(value, "name", 100))
                .Where(value => value is not null).Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToList()
            : [];

    static bool SameTitle(string candidateTitle, JsonElement item)
    {
        if (SameNormalized(candidateTitle, Text(item, "title", 240)) ||
            SameNormalized(candidateTitle, Text(item, "title_english", 240)) ||
            SameNormalized(candidateTitle, Text(item, "title_japanese", 240))) return true;
        return item.TryGetProperty("titles", out var titles) && titles.ValueKind == JsonValueKind.Array &&
            titles.EnumerateArray().Any(title => SameNormalized(candidateTitle, Text(title, "title", 240)));
    }

    static bool SameNormalized(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
            string.Equals(string.Concat(left.Normalize().Where(char.IsLetterOrDigit)),
                string.Concat(right.Normalize().Where(char.IsLetterOrDigit)), StringComparison.OrdinalIgnoreCase);

    static bool SameYear(string? candidate, string? detail)
        => candidate is not { Length: >= 4 } || detail is not { Length: >= 4 } ||
            candidate[..4].Equals(detail[..4], StringComparison.Ordinal);

    internal static bool TryAnimeId(string? value, out int id)
    {
        id = 0;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("myanimelist.net", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var match = Regex.Match(uri.AbsolutePath, @"^/anime/(?<id>[1-9][0-9]{0,8})/?$",
            RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["id"].Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    static bool TryReturnedAnimeId(string? value, out int id)
    {
        id = 0;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("myanimelist.net", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        // MyAnimeList's canonical response URL commonly includes a title slug.
        var match = Regex.Match(uri.AbsolutePath, @"^/anime/(?<id>[1-9][0-9]{0,8})(?:/[^/]+)?/?$",
            RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["id"].Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    static string? Text(JsonElement item, string property, int maxLength)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()?.Trim();
        return text is { Length: > 0 } && text.Length <= maxLength ? text : null;
    }

    static int? Number(JsonElement item, string property)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    static decimal? Decimal(JsonElement item, string property)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;

    static string? CleanSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var plain = WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " "));
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        return plain.Length > 2_000 ? plain[..2_000] : plain;
    }
}

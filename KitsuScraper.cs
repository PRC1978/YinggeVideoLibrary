using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// Kitsu's documented public JSON:API supplies anime candidates without a key.
// Search summaries never establish identity; only an ID, title and release-year
// match on a separately fetched detail can be sent to the review queue.
public static class KitsuScraper
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
        if (string.IsNullOrWhiteSpace(query)) return [];
        query = query.Trim();
        if (query.Length > 160) query = query[..160].TrimEnd();
        if (query.Any(char.IsControl) || query.StartsWith('/') || query.Contains('\\') ||
            Uri.TryCreate(query, UriKind.Absolute, out _) ||
            (query.Length >= 2 && char.IsLetter(query[0]) && query[1] == ':'))
            throw new ArgumentException("Kitsu 只接受作品标题，不接受文件路径。", nameof(query));
        var url = "https://kitsu.io/api/edge/anime?filter%5Btext%5D=" + Uri.EscapeDataString(query) +
            "&page%5Blimit%5D=10";
        using var document = await GetJsonAsync(url, MaxSearchBytes, cancellationToken);
        return ParseSearch(document.RootElement);
    }

    public static async Task<MovieMetadata> FetchFullDetailsAsync(MovieMetadata candidate,
        CancellationToken cancellationToken = default)
    {
        if (candidate is null || !candidate.Source.Equals("Kitsu", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(candidate.Code) ||
            !TryAnimeId(candidate.SourceUrl, out var id))
            throw new InvalidDataException("Kitsu 候选缺少有效的动画条目链接。");
        using var document = await GetJsonAsync($"https://kitsu.io/api/edge/anime/{id}?include=genres",
            MaxDetailBytes, cancellationToken);
        return ParseDetail(candidate, document.RootElement);
    }

    static async Task<JsonDocument> GetJsonAsync(string url, int maxBytes, CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            var delay = nextRequestAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            nextRequestAt = DateTimeOffset.UtcNow.AddSeconds(2);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.api+json"));
            var version = typeof(KitsuScraper).Assembly.GetName().Version?.ToString(3) ?? "1.5.3";
            request.Headers.TryAddWithoutValidation("User-Agent",
                $"PRC1978/YinggeVideoLibrary/{version} (https://github.com/PRC1978/YinggeVideoLibrary)");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var header = response.Headers.RetryAfter;
                var retry = header?.Delta ?? (header?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : null);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var fallback = response.StatusCode == HttpStatusCode.TooManyRequests
                        ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(10);
                    var pause = retry is { } specified && specified > TimeSpan.Zero ? specified : fallback;
                    nextRequestAt = DateTimeOffset.UtcNow.Add(pause > TimeSpan.FromHours(24)
                        ? TimeSpan.FromHours(24) : pause);
                }
                throw new ScrapeHttpException($"Kitsu 返回 HTTP {(int)response.StatusCode}，稍后重试。",
                    response.StatusCode, retry);
            }
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new InvalidDataException("Kitsu 响应超出解析上限。");
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
            throw new InvalidDataException("Kitsu 搜索响应缺少动画列表，不能判断为无结果。");
        var results = new List<MovieMetadata>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = data.EnumerateArray().Take(10).ToList();
        foreach (var item in entries)
        {
            var record = ParseAnimeSummary(item);
            if (record is null || !seen.Add(record.SourceUrl)) continue;
            record.DetailNote = "Kitsu 搜索摘要；需再次核对动画 ID、标题和年份并人工审核";
            record.StampAvailableFields("搜索结果");
            results.Add(record);
        }
        if (entries.Count > 0 && results.Count == 0)
            throw new InvalidDataException("Kitsu 返回的动画条目缺少可核对的身份或标题。");
        return results;
    }

    internal static MovieMetadata ParseDetail(MovieMetadata candidate, JsonElement root)
    {
        if (!candidate.Source.Equals("Kitsu", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(candidate.Code) ||
            !TryAnimeId(candidate.SourceUrl, out var requestedId) ||
            root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var item) ||
            ParseAnimeSummary(item) is not { } parsed ||
            !TryAnimeId(parsed.SourceUrl, out var returnedId) || requestedId != returnedId)
            throw new InvalidDataException("Kitsu 详情动画 ID 与选中候选不一致。");
        if (!SameTitle(candidate.Title, item) || !SameYear(candidate.ReleaseDate, parsed.ReleaseDate))
            throw new InvalidDataException("Kitsu 详情标题或发行年份与搜索候选不一致；资料未采用。");
        var record = candidate.Clone();
        record.Source = parsed.Source;
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
        record.Genres = IncludedGenres(root);
        record.RefreshChangedFieldProvenance(candidate, "Kitsu 动画详情");
        record.DetailFetched = true;
        record.DetailIdentityVerified = true;
        var subtype = Text(item.GetProperty("attributes"), "subtype", 30);
        record.DetailNote = $"已核对 Kitsu 动画 ID、标题和发行年份；{subtype ?? "类型未提供"}，时长为单集分钟数；未提供的字段保持空白。候选须人工审核";
        var rating = RatingAggregation.FromMetadata(record, "Kitsu 动画详情身份核对");
        if (rating is not null)
        {
            // The official media schema defines averageRating as a percentage
            // and ratingFrequencies as the per-value user rating counts.
            rating.VoteCount = RatingCount(item.GetProperty("attributes"));
            RatingAggregation.AddOrUpdate(record, rating);
        }
        return record;
    }

    static MovieMetadata? ParseAnimeSummary(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || Text(item, "type", 20) != "anime" ||
            !item.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
            !TryNumericId(idValue.GetString(), out var id) ||
            !item.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
            return null;
        var title = Text(attrs, "canonicalTitle", 240);
        if (string.IsNullOrWhiteSpace(title)) return null;
        var native = attrs.TryGetProperty("titles", out var titles) && titles.ValueKind == JsonValueKind.Object
            ? Text(titles, "ja_jp", 240) : null;
        var original = native is not null && !SameNormalized(title, native) ? native : null;
        var date = IsoDate(Text(attrs, "startDate", 20));
        var record = new MovieMetadata
        {
            Source = "Kitsu", SourceUrl = $"https://kitsu.io/api/edge/anime/{id}",
            Title = title, OriginalTitle = original, ReleaseDate = date,
            PosterUrl = Poster(attrs), Summary = CleanSummary(Text(attrs, "synopsis", 4_000)
                ?? Text(attrs, "description", 4_000)),
            RuntimeMinutes = Number(attrs, "episodeLength") is int minutes and > 0 and <= 600 ? minutes : null,
            TrailerUrl = Trailer(attrs), Rating = Rating(attrs)
        };
        return record;
    }

    static bool SameTitle(string candidateTitle, JsonElement item)
    {
        if (!item.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object) return false;
        if (SameNormalized(candidateTitle, Text(attrs, "canonicalTitle", 240))) return true;
        if (!attrs.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Object) return false;
        return titles.EnumerateObject().Any(title => title.Value.ValueKind == JsonValueKind.String &&
            SameNormalized(candidateTitle, title.Value.GetString()));
    }

    static bool SameNormalized(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
           string.Equals(string.Concat(left.Normalize().Where(char.IsLetterOrDigit)),
               string.Concat(right.Normalize().Where(char.IsLetterOrDigit)), StringComparison.OrdinalIgnoreCase);

    static bool SameYear(string? candidate, string? detail)
        => candidate is { Length: >= 4 } && detail is { Length: >= 4 } &&
           candidate[..4].Equals(detail[..4], StringComparison.Ordinal);

    internal static bool TryAnimeId(string? value, out long id)
    {
        id = 0;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("kitsu.io", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var match = Regex.Match(uri.AbsolutePath, @"^/api/edge/anime/(?<id>[1-9][0-9]{0,11})$", RegexOptions.IgnoreCase);
        return match.Success && long.TryParse(match.Groups["id"].Value, out id);
    }

    static bool TryNumericId(string? value, out long id)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
           id > 0 && id <= 999_999_999_999L;

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

    static string? IsoDate(string? value)
        => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    static string? Rating(JsonElement attrs)
    {
        var raw = Text(attrs, "averageRating", 8);
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var rating) &&
            rating is >= 10 and <= 100 ? raw + "%" : null;
    }

    static int? RatingCount(JsonElement attrs)
    {
        if (!attrs.TryGetProperty("ratingFrequencies", out var frequencies) ||
            frequencies.ValueKind != JsonValueKind.Object) return null;
        long count = 0;
        foreach (var frequency in frequencies.EnumerateObject())
        {
            if (!int.TryParse(frequency.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var key) ||
                key is < 2 or > 20 || frequency.Value.ValueKind != JsonValueKind.String ||
                !int.TryParse(frequency.Value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture,
                    out var votes) || votes < 0) continue;
            count += votes;
            if (count > int.MaxValue) return null;
        }
        return count > 0 ? (int)count : null;
    }

    static string? Poster(JsonElement attrs)
    {
        if (!attrs.TryGetProperty("posterImage", out var images) || images.ValueKind != JsonValueKind.Object) return null;
        var value = Text(images, "large", 600) ?? Text(images, "original", 600);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            (uri.Host.Equals("media.kitsu.app", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("media.kitsu.io", StringComparison.OrdinalIgnoreCase)) && uri.Port == 443 &&
            uri.UserInfo.Length == 0 ? uri.ToString() : null;
    }

    static string? Trailer(JsonElement attrs)
    {
        var id = Text(attrs, "youtubeVideoId", 20);
        return id is { Length: 11 } && Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$")
            ? "https://www.youtube.com/watch?v=" + id : null;
    }

    static string? CleanSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var plain = WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " "));
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        return plain.Length > 2_000 ? plain[..2_000] : plain;
    }

    static List<string> IncludedGenres(JsonElement root)
        => root.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Array
            ? included.EnumerateArray().Where(item => Text(item, "type", 20) == "genres" &&
                    item.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
                .Select(item => Text(item.GetProperty("attributes"), "name", 80))
                .Where(name => name is not null).Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList()
            : [];
}

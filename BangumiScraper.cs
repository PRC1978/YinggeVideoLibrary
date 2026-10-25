using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// Bangumi is a public catalogue for animation and live-action works. Its v0
// search endpoint is experimental, so response shape errors must be reported
// as source failures, never mistaken for an authoritative "not found" result.
public static class BangumiScraper
{
    const int MaxSearchBytes = 4_000_000;
    const int MaxDetailBytes = 2_000_000;
    static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }) { Timeout = TimeSpan.FromSeconds(18) };

    public static async Task<List<MovieMetadata>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        query = query.Trim();
        if (query.Length > 160) query = query[..160].TrimEnd();

        using var request = Request(HttpMethod.Post, "https://api.bgm.tv/v0/search/subjects?limit=10");
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            keyword = query,
            sort = "match",
            filter = new { type = new[] { 2, 6 } }
        }), Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureSuccess(response);
        using var document = await ReadDocumentAsync(response, MaxSearchBytes, cancellationToken);
        return ParseSearchResults(document.RootElement);
    }

    public static async Task<MovieMetadata> FetchFullDetailsAsync(MovieMetadata candidate, CancellationToken cancellationToken = default)
    {
        if (candidate is null || !string.Equals(candidate.Source, "Bangumi", StringComparison.OrdinalIgnoreCase) ||
            !TrySubjectId(candidate.SourceUrl, out var id))
            throw new InvalidOperationException("Bangumi 候选缺少有效的详情链接。");

        using var request = Request(HttpMethod.Get, $"https://api.bgm.tv/v0/subjects/{id}");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureSuccess(response);
        using var document = await ReadDocumentAsync(response, MaxDetailBytes, cancellationToken);
        return ParseDetail(candidate, document.RootElement);
    }

    static HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        var version = typeof(BangumiScraper).Assembly.GetName().Version?.ToString(3) ?? "1.4.0";
        // Bangumi specifically asks distributed, open-source clients to identify
        // their developer, app, version and project homepage in User-Agent.
        request.Headers.TryAddWithoutValidation("User-Agent",
            $"PRC1978/YinggeVideoLibrary/{version} (https://github.com/PRC1978/YinggeVideoLibrary)");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var retryHeader = response.Headers.RetryAfter;
        var retryAfter = retryHeader?.Delta ?? (retryHeader?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : null);
        throw new ScrapeHttpException($"Bangumi 返回 {(int)response.StatusCode}，请稍后重试。", response.StatusCode, retryAfter);
    }

    static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        await response.Content.LoadIntoBufferAsync(maxBytes, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 64 }, cancellationToken);
    }

    internal static List<MovieMetadata> ParseSearchResults(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            throw new JsonException("Bangumi 搜索响应缺少 data 数组。");

        var results = new List<MovieMetadata>();
        foreach (var subject in data.EnumerateArray().Take(10))
        {
            var candidate = ParseSubjectSummary(subject);
            if (candidate is not null) results.Add(candidate);
        }
        if (data.GetArrayLength() > 0 && results.Count == 0)
            throw new InvalidDataException("Bangumi 搜索条目缺少可核对的作品 ID、类型或标题，不能判定为无匹配。");
        return results;
    }

    internal static MovieMetadata? ParseSubjectSummary(JsonElement subject)
    {
        if (subject.ValueKind != JsonValueKind.Object ||
            !PositiveInt(subject, "id", out var id) || !SubjectTypeSupported(subject)) return null;
        var original = Text(subject, "name");
        var translated = Text(subject, "name_cn");
        var title = translated ?? original;
        if (string.IsNullOrWhiteSpace(title)) return null;

        var candidate = new MovieMetadata
        {
            Title = title,
            OriginalTitle = string.Equals(original, title, StringComparison.Ordinal) ? null : original,
            Source = "Bangumi",
            SourceUrl = $"https://bgm.tv/subject/{id}",
            PosterUrl = Poster(subject),
            ReleaseDate = Date(subject),
            Summary = Text(subject, "summary"),
            Rating = Rating(subject),
            Genres = Names(subject, "meta_tags", 30),
            Keywords = Names(subject, "tags", 30),
            DetailNote = "搜索摘要；选择后读取 Bangumi 完整条目"
        };
        candidate.StampAvailableFields("搜索结果");
        return candidate;
    }

    internal static MovieMetadata ParseDetail(MovieMetadata candidate, JsonElement subject)
    {
        if (!TrySubjectId(candidate.SourceUrl, out var id) ||
            subject.ValueKind != JsonValueKind.Object || !PositiveInt(subject, "id", out var returnedId) ||
            returnedId != id || !SubjectTypeSupported(subject))
            throw new JsonException("Bangumi 详情条目与候选链接不匹配。");

        var parsed = ParseSubjectSummary(subject) ?? throw new JsonException("Bangumi 详情条目缺少标题。");
        var record = candidate.Clone();
        record.Title = parsed.Title;
        record.OriginalTitle = parsed.OriginalTitle ?? record.OriginalTitle;
        record.PosterUrl = parsed.PosterUrl ?? record.PosterUrl;
        record.ReleaseDate = parsed.ReleaseDate ?? record.ReleaseDate;
        record.Summary = parsed.Summary ?? record.Summary;
        record.Rating = parsed.Rating ?? record.Rating;
        record.Genres = parsed.Genres;
        record.Keywords = parsed.Keywords;

        if (subject.TryGetProperty("infobox", out var infobox) && infobox.ValueKind == JsonValueKind.Array)
        {
            record.Director = Infobox(infobox, "导演", "導演", "监督", "監督") ?? record.Director;
            record.Studio = Infobox(infobox, "动画制作", "動畫製作", "制作公司", "製作公司", "出品公司", "出品", "制作", "製作") ?? record.Studio;
            record.Country = Infobox(infobox, "国家/地区", "國家/地區", "国家地区", "國家地區", "制作国家/地区", "製作國家/地區") ?? record.Country;
            record.Language = Infobox(infobox, "语言", "語言") ?? record.Language;
            record.Series = Infobox(infobox, "系列", "系列作品") ?? record.Series;
            var cast = Infobox(infobox, "主演", "演员", "演員", "配音", "声优", "聲優");
            if (!string.IsNullOrWhiteSpace(cast)) record.Actors = SplitNames(cast);
            record.RuntimeMinutes = DurationMinutes(Infobox(infobox, "片长", "片長", "时长", "時長", "播放时间")) ?? record.RuntimeMinutes;
            record.ReleaseDate ??= Infobox(infobox, "上映日期", "上映时间", "上映時間", "放送开始", "放送開始");
        }

        record.RefreshChangedFieldProvenance(candidate, "详情接口");
        record.DetailFetched = true;
        record.DetailIdentityVerified = true;
        record.DetailNote = "已读取 Bangumi 完整条目；来源未提供的字段保持空白";
        var platformRating = RatingAggregation.FromMetadata(record, "详情接口条目一致");
        if (platformRating is not null) RatingAggregation.AddOrUpdate(record, platformRating);
        return record;
    }

    static bool SubjectTypeSupported(JsonElement subject) =>
        subject.TryGetProperty("type", out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var type) && type is 2 or 6;

    static bool TrySubjectId(string? url, out int id)
    {
        id = 0;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("bgm.tv", StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length != 0 ||
            !uri.IsDefaultPort) return false;
        var match = Regex.Match(uri.AbsolutePath, @"^/subject/(?<id>[1-9][0-9]*)/?$", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["id"].Value, out id) && id > 0;
    }

    static string? Text(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() is { Length: > 0 } text ? text : null : null;

    static bool PositiveInt(JsonElement node, string name, out int number)
    {
        number = 0;
        return node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out number) && number > 0;
    }

    static string? Date(JsonElement subject)
    {
        var value = Text(subject, "date");
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? value : null;
    }

    static string? Poster(JsonElement subject)
    {
        if (!subject.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object) return null;
        foreach (var size in new[] { "large", "common", "medium", "small" })
        {
            var value = Text(images, size);
            if (Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps &&
                url.Host.Equals("lain.bgm.tv", StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    static string? Rating(JsonElement subject)
    {
        if (!subject.TryGetProperty("rating", out var rating) || rating.ValueKind != JsonValueKind.Object ||
            !PositiveInt(rating, "total", out var total) || !rating.TryGetProperty("score", out var score) ||
            score.ValueKind != JsonValueKind.Number || !score.TryGetDouble(out var value) || value is <= 0 or > 10)
            return null;
        return value.ToString("0.0", CultureInfo.InvariantCulture) + $"/10（{total} 票）";
    }

    static List<string> Names(JsonElement node, string property, int limit)
    {
        if (!node.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray().Select(value => value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()?.Trim(),
                JsonValueKind.Object => Text(value, "name"),
                _ => null
            })
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
    }

    static string? Infobox(JsonElement infobox, params string[] keys)
    {
        foreach (var key in keys)
            foreach (var entry in infobox.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var found = Text(entry, "key");
                if (!string.Equals(found?.Replace(" ", "").Replace("　", ""), key, StringComparison.OrdinalIgnoreCase) ||
                    !entry.TryGetProperty("value", out var value)) continue;
                if (value.ValueKind == JsonValueKind.String) return value.GetString()?.Trim() is { Length: > 0 } text ? text : null;
                if (value.ValueKind != JsonValueKind.Array) continue;
                var parts = value.EnumerateArray().Take(50).Select(item => Text(item, "v"))
                    .Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (parts.Count > 0) return string.Join("、", parts);
            }
        return null;
    }

    static List<string> SplitNames(string value) => Regex.Split(value, @"[、，,;；\r\n]+")
        .Select(name => name.Trim()).Where(name => name.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(60).ToList();

    static int? DurationMinutes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hours = Regex.Match(value, @"(?<n>\d+)\s*(?:小时|小時|hours?|hrs?)", RegexOptions.IgnoreCase);
        var minutes = Regex.Match(value, @"(?<n>\d+)\s*(?:分钟|分鐘|分|minutes?|mins?)", RegexOptions.IgnoreCase);
        if (hours.Success || minutes.Success)
        {
            if (hours.Success && !int.TryParse(hours.Groups["n"].Value, out _)) return null;
            if (minutes.Success && !int.TryParse(minutes.Groups["n"].Value, out _)) return null;
            var total = (hours.Success ? (long)int.Parse(hours.Groups["n"].Value, CultureInfo.InvariantCulture) * 60 : 0) +
                        (minutes.Success ? int.Parse(minutes.Groups["n"].Value, CultureInfo.InvariantCulture) : 0);
            return total is > 0 and <= 10_000 ? (int)total : null;
        }
        var clock = Regex.Match(value, @"^(?<hours>\d{1,2}):(?<minutes>[0-5]\d)(?::[0-5]\d)?$");
        if (clock.Success)
        {
            var total = (long)int.Parse(clock.Groups["hours"].Value, CultureInfo.InvariantCulture) * 60 +
                        int.Parse(clock.Groups["minutes"].Value, CultureInfo.InvariantCulture);
            return total is > 0 and <= 10_000 ? (int)total : null;
        }
        return null;
    }
}

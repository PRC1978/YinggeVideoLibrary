using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// AniList's public GraphQL API is used for individual anime title searches.
// It is not a bulk catalogue source; its published terms prohibit mass collection.
public static class AniListScraper
{
    const string Endpoint = "https://graphql.anilist.co";
    const int MaxResponseBytes = 2_000_000;
    const string SearchQuery = """
        query ($search: String!) {
          Page(page: 1, perPage: 10) {
            media(search: $search, type: ANIME) {
              id title { romaji english native }
              startDate { year month day }
              coverImage { large }
              genres
              siteUrl
            }
          }
        }
        """;
    const string DetailQuery = """
        query ($id: Int!) {
          Media(id: $id, type: ANIME) {
            id title { romaji english native }
            description(asHtml: false)
            startDate { year month day }
            duration episodes countryOfOrigin format
            genres synonyms averageScore
            tags { name rank isGeneralSpoiler isMediaSpoiler }
            coverImage { large extraLarge }
            studios { edges { isMain node { name } } }
            staff(perPage: 25) { edges { role node { name { full } } } }
            characters(perPage: 20) { edges { voiceActors { name { full } } } }
            siteUrl
          }
        }
        """;

    static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }) { Timeout = TimeSpan.FromSeconds(20) };
    static readonly SemaphoreSlim RequestGate = new(1, 1);
    static DateTimeOffset nextRequestAt;

    public static async Task<List<MovieMetadata>> SearchAsync(string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return [];
        title = title.Trim();
        if (title.Length > 160) title = title[..160].TrimEnd();
        using var document = await QueryAsync(SearchQuery, new { search = title }, cancellationToken);
        return ParseSearchResults(document.RootElement);
    }

    public static async Task<MovieMetadata> FetchFullDetailsAsync(MovieMetadata candidate, CancellationToken cancellationToken = default)
    {
        if (candidate is null || !string.Equals(candidate.Source, "AniList", StringComparison.OrdinalIgnoreCase) ||
            !TryMediaId(candidate.SourceUrl, out var id))
            throw new InvalidOperationException("AniList 候选缺少有效的动画详情链接。");
        using var document = await QueryAsync(DetailQuery, new { id }, cancellationToken);
        return ParseDetail(candidate, document.RootElement);
    }

    static async Task<JsonDocument> QueryAsync(string query, object variables, CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            // The official documentation currently notes a degraded 30/minute
            // limit. A single gate also avoids bursts from concurrent searches.
            var wait = nextRequestAt - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            nextRequestAt = DateTimeOffset.UtcNow.AddMilliseconds(2200);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var version = typeof(AniListScraper).Assembly.GetName().Version?.ToString(3) ?? "1.4.1";
            request.Headers.TryAddWithoutValidation("User-Agent",
                $"PRC1978/YinggeVideoLibrary/{version} (https://github.com/PRC1978/YinggeVideoLibrary)");
            request.Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1);
                if (retry < TimeSpan.FromSeconds(1)) retry = TimeSpan.FromMinutes(1);
                nextRequestAt = DateTimeOffset.UtcNow.Add(retry);
                throw new HttpRequestException("AniList 暂时限流，稍后重试。", null, response.StatusCode);
            }
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new HttpRequestException("AniList 当前拒绝公开 API 请求，请稍后重试。", null, response.StatusCode);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new InvalidDataException("AniList 返回的详情超过大小限制。");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                if (output.Length + count > MaxResponseBytes)
                    throw new InvalidDataException("AniList 返回的详情超过大小限制。");
                output.Write(buffer, 0, count);
            }
            output.Position = 0;
            var document = await JsonDocument.ParseAsync(output, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                document.Dispose();
                throw new InvalidDataException("AniList 返回查询错误，未将本次查询当作无匹配。");
            }
            return document;
        }
        finally { RequestGate.Release(); }
    }

    internal static List<MovieMetadata> ParseSearchResults(JsonElement response)
    {
        if (!TryObject(response, "data", out var data) || !TryObject(data, "Page", out var page) ||
            !page.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("AniList 搜索响应缺少动画列表。");
        var results = new List<MovieMetadata>();
        foreach (var item in media.EnumerateArray())
        {
            var id = Number(item, "id");
            if (id is null or <= 0) continue;
            var record = ParseBase(item, id.Value);
            if (string.IsNullOrWhiteSpace(record.Title)) continue;
            record.DetailNote = "搜索摘要；选定后读取 AniList 动画详情";
            record.StampAvailableFields("搜索结果");
            results.Add(record);
        }
        if (media.GetArrayLength() > 0 && results.Count == 0)
            throw new InvalidDataException("AniList 搜索条目缺少可核对的动画 ID 或标题，不能判定为无匹配。");
        return results;
    }

    internal static MovieMetadata ParseDetail(MovieMetadata candidate, JsonElement response)
    {
        if (!TryObject(response, "data", out var data) || !TryObject(data, "Media", out var item) ||
            Number(item, "id") is not int id || !TryMediaId(candidate.SourceUrl, out var requestedId) || id != requestedId)
            throw new InvalidDataException("AniList 详情与所选候选不一致。");
        var full = ParseBase(item, id);
        var record = candidate.Clone();
        record.Title = full.Title;
        record.OriginalTitle = full.OriginalTitle;
        record.ReleaseDate = full.ReleaseDate ?? record.ReleaseDate;
        record.PosterUrl = full.PosterUrl ?? record.PosterUrl;
        record.Genres = full.Genres;
        record.RuntimeMinutes = Number(item, "duration") is int minutes and > 0 ? minutes : null;
        record.Country = Text(item, "countryOfOrigin");
        record.Summary = CleanDescription(Text(item, "description"));
        record.Rating = Number(item, "averageScore") is int score and > 0
            ? (score / 10d).ToString("0.0", CultureInfo.InvariantCulture) + "/10" : null;
        record.Keywords = item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Where(tag => Number(tag, "rank") is >= 40 &&
                    !IsTrue(tag, "isGeneralSpoiler") && !IsTrue(tag, "isMediaSpoiler"))
                .Select(tag => Text(tag, "name")).Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList() : [];
        if (TryObject(item, "studios", out var studios) && studios.TryGetProperty("edges", out var studioEdges) && studioEdges.ValueKind == JsonValueKind.Array)
        {
            var ordered = studioEdges.EnumerateArray().OrderByDescending(edge => IsTrue(edge, "isMain"))
                .Select(edge => TryObject(edge, "node", out var node) ? Text(node, "name") : null)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8);
            record.Studio = string.Join(" / ", ordered);
            if (record.Studio.Length == 0) record.Studio = null;
        }
        if (TryObject(item, "staff", out var staff) && staff.TryGetProperty("edges", out var staffEdges) && staffEdges.ValueKind == JsonValueKind.Array)
        {
            var directors = staffEdges.EnumerateArray().Where(edge =>
                    (Text(edge, "role") ?? "").Contains("director", StringComparison.OrdinalIgnoreCase) ||
                    (Text(edge, "role") ?? "").Contains("監督", StringComparison.OrdinalIgnoreCase) ||
                    (Text(edge, "role") ?? "").Contains("导演", StringComparison.OrdinalIgnoreCase))
                .Select(edge => TryObject(edge, "node", out var node) && TryObject(node, "name", out var name)
                    ? Text(name, "full") : null)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8);
            record.Director = string.Join(" / ", directors);
            if (record.Director.Length == 0) record.Director = null;
        }
        if (TryObject(item, "characters", out var characters) && characters.TryGetProperty("edges", out var characterEdges) && characterEdges.ValueKind == JsonValueKind.Array)
            record.Actors = characterEdges.EnumerateArray().Where(edge => edge.TryGetProperty("voiceActors", out _))
                .SelectMany(edge => edge.GetProperty("voiceActors").ValueKind == JsonValueKind.Array
                    ? edge.GetProperty("voiceActors").EnumerateArray().Select(actor =>
                        TryObject(actor, "name", out var name) ? Text(name, "full") : null).ToArray()
                    : [])
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
        var episodes = Number(item, "episodes");
        var format = Text(item, "format");
        record.RefreshChangedFieldProvenance(candidate, "详情接口");
        record.DetailFetched = true;
        record.DetailIdentityVerified = true;
        record.DetailNote = $"已读取 AniList 动画详情、制作人员与配音；{(episodes is > 0 ? $"共 {episodes} 集，" : "")}时长为单集分钟数；{format ?? "格式未提供"}。未提供的字段保持空白";
        var platformRating = RatingAggregation.FromMetadata(record, "详情接口条目一致");
        if (platformRating is not null) RatingAggregation.AddOrUpdate(record, platformRating);
        return record;
    }

    static MovieMetadata ParseBase(JsonElement item, int id)
    {
        var titles = TryObject(item, "title", out var title) ? title : default;
        var english = Text(titles, "english");
        var romaji = Text(titles, "romaji");
        var native = Text(titles, "native");
        var primary = english ?? romaji ?? native ?? "";
        var original = native ?? (romaji != primary ? romaji : null);
        var record = new MovieMetadata
        {
            Title = primary, OriginalTitle = original, Source = "AniList",
            SourceUrl = $"https://anilist.co/anime/{id}",
            ReleaseDate = TryObject(item, "startDate", out var startDate) ? Date(startDate) : null,
            PosterUrl = TryObject(item, "coverImage", out var cover)
                ? Text(cover, "extraLarge") ?? Text(cover, "large") : null,
            Genres = Strings(item, "genres", 20)
        };
        return record;
    }

    internal static bool TryMediaId(string? url, out int id)
    {
        id = 0;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("anilist.co", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 || uri.UserInfo.Length != 0) return false;
        var match = Regex.Match(uri.AbsolutePath, @"^/anime/(?<id>[1-9][0-9]*)(?:/.*)?$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["id"].Value, out id);
    }

    static bool TryObject(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }

    static string? Text(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    static int? Number(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : null;

    static bool IsTrue(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    static string? Date(JsonElement date)
    {
        if (Number(date, "year") is not int year || year is < 1 or > 9999) return null;
        var month = Number(date, "month");
        var day = Number(date, "day");
        return month is >= 1 and <= 12
            ? day is >= 1 and <= 31 && System.DateTime.DaysInMonth(year, month.Value) >= day.Value
                ? $"{year:0000}-{month:00}-{day:00}" : $"{year:0000}-{month:00}"
            : $"{year:0000}";
    }

    static List<string> Strings(JsonElement parent, string name, int limit) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToList() : [];

    static string? CleanDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var plain = WebUtility.HtmlDecode(Regex.Replace(value, @"<[^>]*>|[*_`~]", " "));
        return Regex.Replace(plain, @"[ \t]{2,}", " ").Trim();
    }
}

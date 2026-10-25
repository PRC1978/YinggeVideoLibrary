using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// Wikidata supplies public structured facts for films, television series and
// animation. Search cards remain candidates: only a separately fetched item
// with the same Q ID and a supported work type is treated as verified detail.
public static class WikidataScraper
{
    const string Api = "https://www.wikidata.org/w/api.php?";
    const int MaxSearchBytes = 750_000;
    const int MaxEntitiesBytes = 5_000_000;
    static readonly HashSet<string> WorkTypes = new(StringComparer.Ordinal)
    {
        "Q11424",    // film
        "Q5398426",  // television series
        "Q20650540", // anime film
        "Q63952888", // anime television series
        "Q202866",   // animated film
        "Q581714",   // animated series
        "Q506240",   // television film
        "Q15416"     // television program
    };
    // Resolve every kind of fact before spending the bounded label budget on
    // large television casts or episode director lists.
    static readonly (string Property, int Limit)[] RelatedProperties =
        [("P57", 6), ("P136", 14), ("P272", 6), ("P495", 4), ("P364", 4), ("P161", 14)];
    static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }) { Timeout = TimeSpan.FromSeconds(20) };
    static readonly SemaphoreSlim RequestGate = new(1, 1);
    static DateTimeOffset nextRequestAt;

    public static async Task<List<MovieMetadata>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        query = NormalizeSearchQuery(query);
        var language = SearchLanguage(query);
        var searchUrl = Api + "action=wbsearchentities&type=item&format=json&maxlag=20&limit=10&language=" +
            language + "&search=" + Uri.EscapeDataString(query);
        using var search = await GetJsonAsync(searchUrl, MaxSearchBytes, cancellationToken);
        var hits = ParseSearchHits(search.RootElement);
        if (hits.Count == 0) return [];
        var ids = string.Join("%7C", hits.Select(hit => hit.Id));
        using var items = await GetJsonAsync(Api + "action=wbgetentities&format=json&maxlag=20&props=labels%7Cdescriptions%7Cclaims" +
            "&languages=zh%7Cen%7Cja%7Cko&ids=" + ids, MaxEntitiesBytes, cancellationToken);
        return ParseSearchResults(hits, items.RootElement, language);
    }

    internal static string NormalizeSearchQuery(string query)
    {
        query = query.Trim();
        if (query.Any(char.IsControl) || query.Contains('\\') || query.StartsWith('/') ||
            Uri.TryCreate(query, UriKind.Absolute, out _) ||
            (query.Length >= 2 && char.IsLetter(query[0]) && query[1] == ':'))
            throw new ArgumentException("Wikidata 只接受作品标题，不接受文件路径或网址。", nameof(query));
        return query.Length > 160 ? query[..160].TrimEnd() : query;
    }

    public static async Task<MovieMetadata> FetchFullDetailsAsync(MovieMetadata candidate,
        CancellationToken cancellationToken = default)
    {
        if (candidate is null || !string.Equals(candidate.Source, "Wikidata", StringComparison.OrdinalIgnoreCase) ||
            !TryItemId(candidate.SourceUrl, out var id))
            throw new InvalidOperationException("Wikidata 候选缺少有效的条目链接。");

        using var itemDocument = await GetJsonAsync(Api + "action=wbgetentities&format=json&maxlag=20" +
            "&props=labels%7Cdescriptions%7Cclaims&languages=zh%7Cen%7Cja%7Cko&ids=" + id,
            MaxEntitiesBytes, cancellationToken);
        var item = FindEntity(itemDocument.RootElement, id);
        if (item is null || !IsSupportedWork(item.Value))
            throw new InvalidDataException("Wikidata 详情条目与候选不一致，或并非电影、剧集、动画作品。");

        var relatedIds = RelatedProperties.SelectMany(entry => ItemIds(item.Value, entry.Property).Take(entry.Limit))
            .Distinct(StringComparer.Ordinal).Take(48).ToArray();
        JsonDocument? relatedDocument = null;
        try
        {
            if (relatedIds.Length > 0)
                relatedDocument = await GetJsonAsync(Api + "action=wbgetentities&format=json&maxlag=20" +
                    "&props=labels&languages=zh%7Cen%7Cja%7Cko&ids=" + string.Join("%7C", relatedIds),
                    MaxEntitiesBytes, cancellationToken);
            // P18 can be a logo or another illustrative image rather than a
            // cover. Keep the existing poster and leave Wikidata's image unset.
            return ParseDetail(candidate, item.Value, relatedDocument?.RootElement);
        }
        finally { relatedDocument?.Dispose(); }
    }

    static async Task<JsonDocument> GetJsonAsync(string url, int maxBytes, CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            // Keep requests from manual search and the background queue from
            // bursting into Wikimedia's shared public APIs.
            var wait = nextRequestAt - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            nextRequestAt = DateTimeOffset.UtcNow.AddMilliseconds(1300);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var version = typeof(WikidataScraper).Assembly.GetName().Version?.ToString(3) ?? "1.5.1";
            request.Headers.TryAddWithoutValidation("User-Agent",
                $"PRC1978/YinggeVideoLibrary/{version} (https://github.com/PRC1978/YinggeVideoLibrary)");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var header = response.Headers.RetryAfter;
                var retry = header?.Delta ?? (header?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : null);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    nextRequestAt = DateTimeOffset.UtcNow.Add(retry is { } delay && delay > TimeSpan.Zero
                        ? delay : TimeSpan.FromMinutes(1));
                throw new ScrapeHttpException($"Wikidata 返回 HTTP {(int)response.StatusCode}，稍后重试。", response.StatusCode, retry);
            }
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new InvalidDataException("Wikidata 返回内容超过大小限制。");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (output.Length + count > maxBytes)
                    throw new InvalidDataException("Wikidata 返回内容超过大小限制。");
                output.Write(buffer, 0, count);
            }
            output.Position = 0;
            var document = await JsonDocument.ParseAsync(output, new JsonDocumentOptions { MaxDepth = 96 }, cancellationToken);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                var code = Text(error, "code") ?? "unknown";
                document.Dispose();
                if (code == "maxlag")
                {
                    nextRequestAt = DateTimeOffset.UtcNow.AddSeconds(20);
                    throw new ScrapeHttpException("Wikidata 正在延迟同步，稍后重试。",
                        HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(20));
                }
                throw new InvalidDataException("Wikidata 接口返回错误：" + code);
            }
            return document;
        }
        finally { RequestGate.Release(); }
    }

    internal sealed record SearchHit(string Id, string Label);

    internal static List<SearchHit> ParseSearchHits(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("search", out var search) ||
            search.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Wikidata 搜索响应缺少结果数组，不能判定为无匹配。");
        var hits = search.EnumerateArray().Take(10).Where(hit => hit.ValueKind == JsonValueKind.Object)
            .Select(hit => new SearchHit(Text(hit, "id") ?? "", Text(hit, "label") ?? ""))
            .Where(hit => ValidItemId(hit.Id) && hit.Label.Length > 0)
            .DistinctBy(hit => hit.Id, StringComparer.Ordinal).ToList();
        if (search.GetArrayLength() > 0 && hits.Count == 0)
            throw new InvalidDataException("Wikidata 搜索结果缺少可核对的条目 ID，不能判定为无匹配。");
        return hits;
    }

    internal static List<MovieMetadata> ParseSearchResults(IReadOnlyList<SearchHit> hits, JsonElement response, string language)
    {
        if (!TryObject(response, "entities", out _))
            throw new InvalidDataException("Wikidata 条目响应缺少 entities，不能判定为无匹配。");
        var results = new List<MovieMetadata>();
        var entitiesFound = 0;
        foreach (var hit in hits)
        {
            var item = FindEntity(response, hit.Id);
            if (item is null) continue;
            entitiesFound++;
            if (!IsSupportedWork(item.Value)) continue;
            var label = Label(item.Value, language) ?? hit.Label;
            if (label.Length == 0) continue;
            var record = new MovieMetadata
            {
                Title = label, OriginalTitle = OriginalTitle(item.Value, label), Source = "Wikidata",
                SourceUrl = "https://www.wikidata.org/wiki/" + hit.Id,
                ReleaseDate = ReleaseDate(item.Value), Summary = Description(item.Value, language),
                DetailNote = "搜索摘要；选择后读取 Wikidata 结构化作品详情"
            };
            record.StampAvailableFields("搜索结果");
            results.Add(record);
        }
        if (hits.Count > 0 && entitiesFound == 0)
            throw new InvalidDataException("Wikidata 返回的条目与搜索 ID 均不一致，不能判定为无匹配。");
        return results;
    }

    internal static MovieMetadata ParseDetail(MovieMetadata candidate, JsonElement item, JsonElement? related)
    {
        if (!TryItemId(candidate.SourceUrl, out var requestedId) || Text(item, "id") != requestedId ||
            !IsSupportedWork(item))
            throw new InvalidDataException("Wikidata 详情 Q ID 与选中的作品候选不一致。");
        var record = candidate.Clone();
        var title = Label(item, SearchLanguage(candidate.Title)) ?? Label(item, "en") ?? candidate.Title;
        record.Title = title;
        record.OriginalTitle = OriginalTitle(item, title) ?? record.OriginalTitle;
        record.ReleaseDate = ReleaseDate(item) ?? record.ReleaseDate;
        record.Summary = Description(item, SearchLanguage(title)) ?? record.Summary;
        record.RuntimeMinutes = RuntimeMinutes(item);
        record.Director = JoinedNames(item, "P57", related, 8);
        record.Studio = JoinedNames(item, "P272", related, 8);
        record.Country = JoinedNames(item, "P495", related, 8);
        record.Language = JoinedNames(item, "P364", related, 8);
        record.Actors = Names(item, "P161", related, 24);
        record.Genres = Names(item, "P136", related, 20);
        record.RefreshChangedFieldProvenance(candidate, "Wikidata 条目与 Q ID 核验");
        record.DetailFetched = true;
        record.DetailIdentityVerified = true;
        record.DetailNote = "已核对 Wikidata 作品条目、类型和 Q ID；简介来自短描述，来源未提供的字段保持空白";
        return record;
    }

    internal static bool TryItemId(string? url, out string id)
    {
        id = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 || uri.UserInfo.Length != 0 ||
            !uri.Host.Equals("www.wikidata.org", StringComparison.OrdinalIgnoreCase)) return false;
        var match = Regex.Match(uri.AbsolutePath, @"^/wiki/(?<id>Q[1-9][0-9]*)/?$", RegexOptions.CultureInvariant);
        if (!match.Success || match.Groups["id"].Value.Length > 16) return false;
        id = match.Groups["id"].Value;
        return true;
    }

    static bool ValidItemId(string? id) => id is { Length: <= 16 } &&
        Regex.IsMatch(id, @"^Q[1-9][0-9]*$", RegexOptions.CultureInvariant);

    static JsonElement? FindEntity(JsonElement root, string id)
    {
        if (!TryObject(root, "entities", out var entities) || !entities.TryGetProperty(id, out var item) ||
            item.ValueKind != JsonValueKind.Object || Text(item, "id") != id) return null;
        return item;
    }

    static bool IsSupportedWork(JsonElement item) => ItemIds(item, "P31").Any(WorkTypes.Contains);

    static string SearchLanguage(string text) => text.Any(c => c is >= '\u3040' and <= '\u30ff') ? "ja" :
        text.Any(c => c is >= '\uac00' and <= '\ud7af') ? "ko" :
        text.Any(c => c is >= '\u3400' and <= '\u9fff') ? "zh" : "en";

    static string? Label(JsonElement item, string language)
    {
        if (!TryObject(item, "labels", out var labels)) return null;
        foreach (var key in new[] { language, "zh", "en", "ja", "ko" }.Distinct(StringComparer.Ordinal))
            if (TryObject(labels, key, out var label) && Text(label, "value") is { } value) return value;
        return null;
    }

    static string? Description(JsonElement item, string language)
    {
        if (!TryObject(item, "descriptions", out var descriptions)) return null;
        foreach (var key in new[] { language, "zh", "en", "ja", "ko" }.Distinct(StringComparer.Ordinal))
            if (TryObject(descriptions, key, out var description) && Text(description, "value") is { } value) return value;
        return null;
    }

    static string? OriginalTitle(JsonElement item, string displayTitle)
    {
        var title = ClaimValues(item, "P1476").Where(value => value.ValueKind == JsonValueKind.Object)
            .Select(value => Text(value, "text")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (title is null || title.Equals(displayTitle, StringComparison.OrdinalIgnoreCase)) return null;
        return title;
    }

    static string? ReleaseDate(JsonElement item)
    {
        foreach (var property in new[] { "P577", "P1191", "P580" })
            foreach (var value in ClaimValues(item, property))
            {
                var time = Text(value, "time");
                var match = Regex.Match(time ?? "", @"^\+(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})T", RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                var precision = Number(value, "precision") ?? 11;
                var date = match.Groups["year"].Value;
                if (precision >= 10 && match.Groups["month"].Value != "00") date += "-" + match.Groups["month"].Value;
                if (precision >= 11 && match.Groups["day"].Value != "00" && date.Length == 7) date += "-" + match.Groups["day"].Value;
                return date;
            }
        return null;
    }

    static int? RuntimeMinutes(JsonElement item)
    {
        foreach (var value in ClaimValues(item, "P2047"))
        {
            var amount = Text(value, "amount");
            var unit = Text(value, "unit");
            if (!double.TryParse(amount, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) continue;
            var minutes = unit switch
            {
                "http://www.wikidata.org/entity/Q7727" => number,
                "http://www.wikidata.org/entity/Q11574" => number / 60,
                "http://www.wikidata.org/entity/Q25235" => number * 60,
                _ => 0
            };
            if (minutes is > 0 and <= 1000) return (int)Math.Ceiling(minutes);
        }
        return null;
    }

    static string? JoinedNames(JsonElement item, string property, JsonElement? related, int limit)
    {
        var names = Names(item, property, related, limit);
        return names.Count == 0 ? null : string.Join("、", names);
    }

    static List<string> Names(JsonElement item, string property, JsonElement? related, int limit)
        => ItemIds(item, property).Take(limit).Select(id => related is { } root ? FindEntity(root, id) : null)
            .Where(entity => entity is not null).Select(entity => Label(entity!.Value, "zh"))
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static IEnumerable<string> ItemIds(JsonElement item, string property)
        => ClaimValues(item, property).Where(value => value.ValueKind == JsonValueKind.Object)
            .Select(value => Text(value, "id")).Where(ValidItemId).Select(id => id!);

    static IEnumerable<JsonElement> ClaimValues(JsonElement item, string property)
    {
        if (!TryObject(item, "claims", out var claims) || !claims.TryGetProperty(property, out var statements) ||
            statements.ValueKind != JsonValueKind.Array) yield break;
        foreach (var statement in statements.EnumerateArray())
        {
            if (Text(statement, "rank") == "deprecated" || !TryObject(statement, "mainsnak", out var snak) ||
                !TryObject(snak, "datavalue", out var data) || !data.TryGetProperty("value", out var value)) continue;
            yield return value;
        }
    }

    static bool TryObject(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }

    static string? Text(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() is { Length: > 0 } text ? text : null : null;

    static int? Number(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : null;
}

using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// OMDb is a key-backed manual title lookup. Only the public IMDb page URL is
// persisted; the API request (which necessarily contains the key) is transient.
internal static class OmdbScraper
{
    const int MaxResponseBytes = 1024 * 1024;
    static readonly Regex ImdbId = new(@"^tt[0-9]{7,10}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static OmdbScraper()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("YinggeVideoLibrary/1.5.1 (manual metadata lookup)");
    }

    internal static bool CredentialReady()
    {
        try { return !string.IsNullOrWhiteSpace(WindowsCredentialStore.Read("OmdbKey")); }
        catch { return false; }
    }

    static string RequireKey() => RequireKeyValue(WindowsCredentialStore.Read("OmdbKey"));

    internal static string RequireKeyValue(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OMDb 尚未设置 API Key；请在搜刮设置中保存后手动查询。");
        return key;
    }

    public static async Task<List<MovieMetadata>> SearchAsync(string title, CancellationToken cancellationToken = default)
    {
        title = title.Trim();
        if (title.Length is < 2 or > 160 || title.Contains('/') || title.Contains('\\'))
            throw new InvalidOperationException("OMDb 手动查询需要 2–160 字的作品标题，不接受文件路径。");
        using var response = await RequestAsync("s=" + Uri.EscapeDataString(title), RequireKey(), cancellationToken);
        try { return ParseSearch(response.RootElement); }
        catch (ScrapeNotFoundException) { return []; }
    }

    public static async Task<MovieMetadata> FetchFullDetailsAsync(MovieMetadata candidate,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(candidate.Source, "OMDb", StringComparison.OrdinalIgnoreCase) ||
            !TryImdbId(candidate.SourceUrl, out var id))
            throw new InvalidDataException("OMDb 候选缺少有效的 IMDb 作品链接。");
        using var response = await RequestAsync("i=" + id + "&plot=full", RequireKey(), cancellationToken);
        return ParseDetail(candidate, response.RootElement);
    }

    static async Task<JsonDocument> RequestAsync(string parameters, string key, CancellationToken cancellationToken)
    {
        // Never attach the request URI or the original exception to a visible
        // error. HttpClient exceptions can contain the apikey query string.
        try
        {
            var address = "https://www.omdbapi.com/?apikey=" + Uri.EscapeDataString(key) + "&r=json&" + parameters;
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SafeSourceException("OMDb 拒绝了 API Key；请检查凭据和账号状态。");
            if ((int)response.StatusCode == 429)
                throw new SafeSourceException("OMDb 请求限额已到，请稍后手动重试。");
            if (!response.IsSuccessStatusCode)
                throw new SafeSourceException("OMDb 暂时不可用（HTTP " + (int)response.StatusCode + "）。");
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                throw new InvalidDataException("OMDb 响应超出解析上限。");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (bounded.Length + count > MaxResponseBytes)
                    throw new InvalidDataException("OMDb 响应超出解析上限。");
                await bounded.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            bounded.Position = 0;
            return JsonDocument.Parse(bounded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SafeSourceException) { throw; }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or UriFormatException or InvalidOperationException)
        {
            throw new InvalidOperationException("OMDb 网络或响应解析失败；请稍后手动重试。");
        }
    }

    sealed class SafeSourceException(string message) : InvalidOperationException(message);

    internal static List<MovieMetadata> ParseSearch(JsonElement response)
    {
        RequireSuccess(response, search: true);
        if (!response.TryGetProperty("Search", out var cards) || cards.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("OMDb 搜索响应缺少作品列表，不能判定为无匹配。");
        var results = new List<MovieMetadata>();
        foreach (var card in cards.EnumerateArray().Take(10))
        {
            var id = Text(card, "imdbID", 20);
            var title = Text(card, "Title", 300);
            var kind = Text(card, "Type", 20);
            if (!ValidImdbId(id) || title.Length == 0 || kind is not ("movie" or "series")) continue;
            var year = FirstYear(Text(card, "Year", 40));
            var candidate = new MovieMetadata
            {
                Source = "OMDb", SourceUrl = PublicUrl(id), Title = WithYear(title, year),
                ReleaseDate = year, DetailNote = "手动标题搜索摘要；选择后按 IMDb ID 核对完整详情"
            };
            candidate.StampAvailableFields("搜索结果");
            results.Add(candidate);
        }
        if (cards.GetArrayLength() > 0 && results.Count == 0)
            throw new InvalidDataException("OMDb 返回的作品缺少可核对的 IMDb ID 或影视类型。");
        return results.DistinctBy(item => item.SourceUrl, StringComparer.Ordinal).ToList();
    }

    internal static MovieMetadata ParseDetail(MovieMetadata candidate, JsonElement response)
    {
        RequireSuccess(response, search: false);
        if (!string.Equals(candidate.Source, "OMDb", StringComparison.OrdinalIgnoreCase) ||
            !TryImdbId(candidate.SourceUrl, out var requested) ||
            Text(response, "imdbID", 20) != requested ||
            Text(response, "Type", 20) is not ("movie" or "series"))
            throw new InvalidDataException("OMDb 详情 IMDb ID 或影视类型与选中候选不一致；资料未采用。");
        var title = Text(response, "Title", 300);
        if (title.Length == 0)
            throw new InvalidDataException("OMDb 详情缺少作品标题；资料未采用。");
        var record = candidate.Clone();
        var year = FirstYear(Text(response, "Year", 40));
        record.Title = WithYear(title, year);
        record.ReleaseDate = ReleaseDate(Text(response, "Released", 40)) ?? year;
        record.RuntimeMinutes = Runtime(Text(response, "Runtime", 30));
        record.Director = OrNull(Text(response, "Director", 500));
        record.Actors = SplitList(Text(response, "Actors", 1200), 20);
        record.Genres = SplitList(Text(response, "Genre", 500), 20);
        record.Country = OrNull(Text(response, "Country", 200));
        record.Language = OrNull(Text(response, "Language", 200));
        record.Summary = OrNull(Text(response, "Plot", 5000));
        record.Studio = OrNull(Text(response, "Production", 300));
        record.PlatformRatings = ParseRatings(response, record.SourceUrl);
        // The structured IMDb score already has its true platform identity.
        // A generic OMDb Rating would be counted as a second rating source.
        record.Rating = null;
        record.RefreshChangedFieldProvenance(candidate, "OMDb IMDb ID 核验");
        record.DetailFetched = true;
        record.DetailIdentityVerified = true;
        record.DetailNote = "已核对 IMDb ID 与影视类型；由用户手动选择，进入人工审核";
        return record;
    }

    static void RequireSuccess(JsonElement root, bool search)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("OMDb 响应格式无效。");
        var response = Text(root, "Response", 8);
        if (response == "True") return;
        if (response != "False") throw new InvalidDataException("OMDb 响应缺少有效状态。");
        var error = Text(root, "Error", 200);
        if (search && error.Equals("Movie not found!", StringComparison.OrdinalIgnoreCase))
            throw new ScrapeNotFoundException("OMDb 没有找到对应标题。");
        if (error.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("No API key", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OMDb API Key 无效；请在搜刮设置中检查。");
        if (error.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("quota", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OMDb 请求限额已到，请稍后手动重试。");
        throw new InvalidOperationException("OMDb 返回来源错误；不能判定为无匹配，请稍后手动重试。");
    }

    internal static bool TryImdbId(string? publicUrl, out string id)
    {
        id = "";
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "www.imdb.com" ||
            uri.Query.Length != 0 || uri.UserInfo.Length != 0 || uri.Port != 443) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || parts[0] != "title" || !ValidImdbId(parts[1])) return false;
        id = parts[1];
        return true;
    }

    static bool ValidImdbId(string? id) => id is not null && ImdbId.IsMatch(id);
    static string PublicUrl(string id) => "https://www.imdb.com/title/" + id + "/";
    static string Text(JsonElement node, string key, int max)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out var value) ||
            value.ValueKind != JsonValueKind.String) return "";
        var text = value.GetString()?.Trim() ?? "";
        return text.Length > max || text.Equals("N/A", StringComparison.OrdinalIgnoreCase) ? "" : text;
    }
    static string? OrNull(string text) => text.Length == 0 ? null : text;
    static string? FirstYear(string raw) => Regex.Match(raw, @"\b(?:19|20)[0-9]{2}\b").Value is { Length: 4 } year ? year : null;
    static string WithYear(string title, string? year) => year is null || title.EndsWith("(" + year + ")", StringComparison.Ordinal)
        ? title : title + " (" + year + ")";
    static string? ReleaseDate(string raw) => DateTime.TryParseExact(raw, "dd MMM yyyy", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    static int? Runtime(string raw)
    {
        var match = Regex.Match(raw, @"^(?<minutes>[0-9]{1,4}) min$");
        return match.Success && int.TryParse(match.Groups["minutes"].Value, out var minutes) && minutes is > 0 and < 10000
            ? minutes : null;
    }
    static List<string> SplitList(string raw, int limit) => raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(part => part.Length is > 0 and <= 120).Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToList();

    static List<PlatformRating> ParseRatings(JsonElement root, string imdbUrl)
    {
        var ratings = new List<PlatformRating>();
        static bool ParseScore(string raw, string suffix, double maximum, out double value)
        {
            value = 0;
            return raw.EndsWith(suffix, StringComparison.Ordinal) &&
                double.TryParse(raw[..^suffix.Length], NumberStyles.Number, CultureInfo.InvariantCulture, out value) &&
                value >= 0 && value <= maximum;
        }
        var imdbRaw = Text(root, "imdbRating", 12);
        if (double.TryParse(imdbRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var imdb) && imdb is >= 0 and <= 10)
        {
            var votesRaw = Text(root, "imdbVotes", 20).Replace(",", "", StringComparison.Ordinal);
            int? votes = int.TryParse(votesRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedVotes) && parsedVotes >= 0
                ? parsedVotes : null;
            ratings.Add(new PlatformRating { Platform = "IMDb", SourceUrl = imdbUrl, Value = imdb, Maximum = 10,
                VoteCount = votes, MatchEvidence = "OMDb IMDb ID 详情" });
        }
        if (root.TryGetProperty("Ratings", out var values) && values.ValueKind == JsonValueKind.Array)
            foreach (var item in values.EnumerateArray().Take(8))
            {
                var source = Text(item, "Source", 80);
                var raw = Text(item, "Value", 20);
                if (source == "Rotten Tomatoes" && ParseScore(raw, "%", 100, out var tomato))
                    ratings.Add(new PlatformRating { Platform = "Rotten Tomatoes", Value = tomato, Maximum = 100,
                        MatchEvidence = "OMDb IMDb ID 详情中的平台评分" });
                else if (source == "Metacritic" && ParseScore(raw, "/100", 100, out var meta))
                    ratings.Add(new PlatformRating { Platform = "Metacritic", Value = meta, Maximum = 100,
                        MatchEvidence = "OMDb IMDb ID 详情中的平台评分" });
            }
        return RatingAggregation.DistinctValidRatings(ratings);
    }
}

using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// Trakt permits user-chosen discovery and scores, but prohibits catalogue
// harvesting. This adapter is deliberately reachable only by manual actions.
internal static class TraktScraper
{
    const int MaxSearchBytes = 600_000;
    const int MaxDetailBytes = 1_000_000;
    static readonly Regex Slug = new("^[a-z0-9][a-z0-9-]{0,179}$", RegexOptions.Compiled);
    static readonly Regex Imdb = new("^tt[0-9]{7,10}$", RegexOptions.Compiled);
    static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }) { Timeout = TimeSpan.FromSeconds(18) };
    static readonly SemaphoreSlim RequestGate = new(1, 1);
    static DateTimeOffset nextRequestAt;

    internal static bool CredentialReady()
    {
        try { return ValidClientId(WindowsCredentialStore.Read("TraktClientId")); }
        catch { return false; }
    }

    internal static bool ValidClientId(string? value) => value is { Length: >= 24 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    static string RequireClientId()
    {
        var value = WindowsCredentialStore.Read("TraktClientId");
        return ValidClientId(value) ? value! : throw new InvalidOperationException(
            "Trakt 尚未设置 Client ID；请在搜刮页保存后单部手动查询。");
    }

    public static async Task<List<MovieMetadata>> SearchAsync(string title,
        CancellationToken cancellationToken = default)
    {
        title = title.Trim();
        if (title.Length is < 2 or > 160 || title.Any(char.IsControl) ||
            title.Contains('\\') || Uri.TryCreate(title, UriKind.Absolute, out _) ||
            (title.Length >= 2 && char.IsLetter(title[0]) && title[1] == ':'))
            throw new InvalidOperationException("Trakt 手动查询需要 2–160 字的电影或剧集标题，不接受路径和网址。");
        var clientId = RequireClientId();
        using var document = await GetJsonAsync("search/movie,show?query=" +
            Uri.EscapeDataString(title) + "&page=1&limit=10", clientId, MaxSearchBytes, cancellationToken);
        return ParseSearch(document.RootElement);
    }

    public static async Task<MovieMetadata> FetchFullDetailsAsync(MovieMetadata candidate,
        CancellationToken cancellationToken = default)
    {
        if (!TryCandidateIdentity(candidate, out var type, out var id, out _))
            throw new InvalidDataException("Trakt 候选缺少可核对的作品编号或公开链接。");
        var clientId = RequireClientId();
        var path = type == "movie" ? "movies/" : "shows/";
        using var document = await GetJsonAsync(path + id.ToString(CultureInfo.InvariantCulture) +
            "?extended=full", clientId, MaxDetailBytes, cancellationToken);
        return ParseDetail(candidate, document.RootElement);
    }

    static async Task<JsonDocument> GetJsonAsync(string path, string clientId, int maxBytes,
        CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            var delay = nextRequestAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            nextRequestAt = DateTimeOffset.UtcNow.AddSeconds(2);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.trakt.tv/" + path);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
                request.Headers.TryAddWithoutValidation("trakt-api-key", clientId);
                request.Headers.UserAgent.ParseAdd("YinggeVideoLibrary/1.5.1");
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new InvalidOperationException("Trakt 拒绝了 Client ID；请检查凭据或账号权限。");
                if (!response.IsSuccessStatusCode)
                {
                    var retryHeader = response.Headers.RetryAfter;
                    var retry = retryHeader?.Delta ?? (retryHeader?.Date is DateTimeOffset date
                        ? date - DateTimeOffset.UtcNow : null);
                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                    {
                        var pause = retry is { } supplied && supplied > TimeSpan.Zero ? supplied
                            : response.StatusCode == HttpStatusCode.TooManyRequests
                                ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(20);
                        nextRequestAt = DateTimeOffset.UtcNow.Add(pause > TimeSpan.FromHours(24)
                            ? TimeSpan.FromHours(24) : pause);
                    }
                    throw new ScrapeHttpException("Trakt 返回 HTTP " + (int)response.StatusCode +
                        "，请稍后手动重试。", response.StatusCode, retry);
                }
                if (response.Content.Headers.ContentLength > maxBytes)
                    throw new InvalidDataException("Trakt 响应超出解析上限。");
                await response.Content.LoadIntoBufferAsync(maxBytes, cancellationToken);
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 48 },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ScrapeHttpException) { throw; }
            catch (InvalidDataException) { throw; }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Trakt 拒绝了", StringComparison.Ordinal)) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                // Never include provider response bodies, paths or credential-bearing headers.
                throw new InvalidOperationException("Trakt 网络或响应解析失败；请稍后手动重试。");
            }
        }
        finally { RequestGate.Release(); }
    }

    internal static List<MovieMetadata> ParseSearch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Trakt 搜索响应缺少作品列表，不能判断为无结果。");
        var entries = root.EnumerateArray().Take(10).ToList();
        var results = new List<MovieMetadata>();
        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var type = Text(entry, "type", 12);
            if (type is not ("movie" or "show") ||
                !entry.TryGetProperty(type, out var item) || item.ValueKind != JsonValueKind.Object ||
                !TryExternalIds(item, out var ids) || !ids.TryGetValue("trakt", out var traktId) ||
                !TrySlug(item, out var slug) || Text(item, "title", 300) is not { } title)
                continue;
            var year = Year(item);
            var candidate = new MovieMetadata
            {
                Code = "", Source = "Trakt", Title = WithYear(title, year),
                SourceUrl = PublicUrl(type, slug), ExternalIds = ids,
                ReleaseDate = year,
                DetailNote = "Trakt " + (type == "movie" ? "电影" : "剧集") + "搜索摘要 · ID " + traktId +
                    CrossIdNote(ids) + "；选择后另读详情核对，须人工审核"
            };
            candidate.StampAvailableFields("搜索结果");
            results.Add(candidate);
        }
        if (entries.Count > 0 && results.Count == 0)
            throw new InvalidDataException("Trakt 搜索条目缺少可核对的作品编号、标题或类型。");
        return results.DistinctBy(record => record.ExternalIds["trakt"] + "|" + record.SourceUrl,
            StringComparer.Ordinal).ToList();
    }

    internal static MovieMetadata ParseDetail(MovieMetadata candidate, JsonElement root)
    {
        if (!TryCandidateIdentity(candidate, out var type, out var expectedId, out var slug) ||
            root.ValueKind != JsonValueKind.Object || !TryExternalIds(root, out var returnedIds) ||
            !returnedIds.TryGetValue("trakt", out var returnedId) ||
            returnedId != expectedId.ToString(CultureInfo.InvariantCulture) ||
            !TrySlug(root, out var returnedSlug) || returnedSlug != slug)
            throw new InvalidDataException("Trakt 详情作品编号或公开链接与所选候选不一致；资料未采用。");
        foreach (var crossId in new[] { "imdb", "tmdb" })
            if (candidate.ExternalIds.TryGetValue(crossId, out var expected) &&
                (!returnedIds.TryGetValue(crossId, out var returned) || returned != expected))
                throw new InvalidDataException("Trakt 详情跨平台作品编号与所选候选冲突；资料未采用。");
        var title = Text(root, "title", 300);
        var year = Year(root);
        if (title is null || !SameTitle(candidate.Title, title) ||
            candidate.ReleaseDate is { Length: > 0 } candidateYear && candidateYear != year)
            throw new InvalidDataException("Trakt 详情标题或发行年份与搜索候选冲突；资料未采用。");
        var record = candidate.Clone();
        record.Title = WithYear(title, year);
        record.ReleaseDate = Date(root, type) ?? year;
        record.ExternalIds = returnedIds;
        record.Summary = Summary(root);
        record.RuntimeMinutes = PositiveInt(root, "runtime", 1_000);
        record.Language = Text(root, "language", 100);
        record.Country = Text(root, "country", 100);
        record.Studio = type == "show" ? Text(root, "network", 180) : null;
        record.Genres = TextArray(root, "genres", 25);
        record.TrailerUrl = SafeTrailer(Text(root, "trailer", 500));
        record.Rating = Rating(root);
        record.PlatformRatings = [];
        if (record.Rating is not null)
        {
            var rating = RatingAggregation.FromMetadata(record,
                "Trakt ID、公开链接、标题及年份核对；IMDb/TMDB 编号一致");
            if (rating is not null) RatingAggregation.AddOrUpdate(record, rating);
        }
        record.RefreshChangedFieldProvenance(candidate, "Trakt 详情作品身份核对");
        record.DetailFetched = true;
        record.DetailIdentityVerified = true;
        record.DetailNote = "已核对 Trakt " + (type == "movie" ? "电影" : "剧集") + " ID " + returnedId +
            "、标题、年份及可用的 IMDb/TMDB 编号" + CrossIdNote(returnedIds) +
            "；未提供的字段保持空白，须人工审核";
        return record;
    }

    internal static bool TryCandidateIdentity(MovieMetadata? candidate, out string type,
        out int id, out string slug)
    {
        type = slug = ""; id = 0;
        if (candidate is null || candidate.Source != "Trakt" ||
            !string.IsNullOrWhiteSpace(candidate.Code) || candidate.ExternalIds is null ||
            !candidate.ExternalIds.TryGetValue("trakt", out var idText) ||
            !int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0 ||
            !Uri.TryCreate(candidate.SourceUrl, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || url.Host != "trakt.tv" || url.Port != 443 ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0) return false;
        var parts = url.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || parts[0] is not ("movies" or "shows") || !Slug.IsMatch(parts[1])) return false;
        type = parts[0] == "movies" ? "movie" : "show";
        slug = parts[1];
        return true;
    }

    static bool TryExternalIds(JsonElement item, out Dictionary<string, string> ids)
    {
        ids = new(StringComparer.OrdinalIgnoreCase);
        if (!item.TryGetProperty("ids", out var node) || node.ValueKind != JsonValueKind.Object ||
            PositiveInt(node, "trakt", int.MaxValue) is not int traktId || traktId <= 0)
            return false;
        ids["trakt"] = traktId.ToString(CultureInfo.InvariantCulture);
        if (Text(node, "imdb", 20) is { } imdb && Imdb.IsMatch(imdb)) ids["imdb"] = imdb;
        if (PositiveInt(node, "tmdb", int.MaxValue) is int tmdbId)
            ids["tmdb"] = tmdbId.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    static bool TrySlug(JsonElement item, out string slug)
    {
        slug = "";
        if (!item.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Object ||
            Text(ids, "slug", 180) is not { } found || !Slug.IsMatch(found)) return false;
        slug = found;
        return true;
    }

    static string PublicUrl(string type, string slug) =>
        "https://trakt.tv/" + (type == "movie" ? "movies/" : "shows/") + slug;

    static string? Text(JsonElement item, string key, int maximum)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(key, out var value) ||
            value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()?.Trim();
        return text is { Length: > 0 } && text.Length <= maximum && !text.Any(char.IsControl)
            ? text : null;
    }

    static string? Summary(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("overview", out var value) ||
            value.ValueKind != JsonValueKind.String) return null;
        var source = value.GetString();
        if (string.IsNullOrWhiteSpace(source) || source.Length > 6_000) return null;
        var normalized = Regex.Replace(source, @"\s+", " ").Trim();
        return normalized.Length is > 0 and <= 6_000 && !normalized.Any(char.IsControl)
            ? normalized : null;
    }

    static int? PositiveInt(JsonElement item, string key, int maximum)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) &&
           value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) &&
           parsed is > 0 && parsed <= maximum ? parsed : null;

    static string? Year(JsonElement item)
        => PositiveInt(item, "year", 2200) is int year and >= 1800
            ? year.ToString(CultureInfo.InvariantCulture) : null;

    static string? Date(JsonElement item, string type)
    {
        var value = Text(item, type == "movie" ? "released" : "first_aired", 40);
        return value is not null && DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }

    static List<string> TextArray(JsonElement item, string key, int maximum)
    {
        if (!item.TryGetProperty(key, out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray().Take(maximum).Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()?.Trim()).Where(value => value is { Length: > 0 and <= 100 })
            .Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string? Rating(JsonElement item)
    {
        if (!item.TryGetProperty("rating", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var score) || !double.IsFinite(score) || score is < 0 or > 10)
            return null;
        var votes = PositiveInt(item, "votes", int.MaxValue);
        return score.ToString("0.##", CultureInfo.InvariantCulture) + "/10" +
            (votes is int count ? "（" + count.ToString("N0", CultureInfo.InvariantCulture) + " 票）" : "");
    }

    static string? SafeTrailer(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
           uri.Port == 443 && uri.UserInfo.Length == 0 &&
           (uri.Host == "www.youtube.com" || uri.Host == "youtu.be") ? uri.ToString() : null;

    static string WithYear(string title, string? year) => year is null ||
        title.EndsWith("(" + year + ")", StringComparison.Ordinal) ? title : title + " (" + year + ")";

    static bool SameTitle(string candidate, string detail)
    {
        candidate = Regex.Replace(candidate.Trim(), @"\s*\([0-9]{4}\)$", "");
        static string Canonical(string value) => new(value.Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant).ToArray());
        return Canonical(candidate) == Canonical(detail);
    }

    static string CrossIdNote(IReadOnlyDictionary<string, string> ids)
        => (ids.TryGetValue("imdb", out var imdb) ? " · IMDb " + imdb : "") +
           (ids.TryGetValue("tmdb", out var tmdb) ? " · TMDB " + tmdb : "");
}

using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

public static class AdditionalScrapers
{
    const int MaxPosterBytes = 8_000_000;
    static readonly HttpClient Client = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(18) };
    // Poster hosts are allowlisted before the request; a redirect must not escape that allowlist.
    static readonly HttpClient PosterClient = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(18) };

    static string? Text(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()?.Trim() : null;

    static int? PositiveInt(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number) && number > 0 ? number : null;

    static List<string> NamedArray(JsonElement node, string property, string name = "name", int limit = 80)
    {
        if (!node.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object ? Text(item, name) : item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
    }

    static string? Joined(IEnumerable<string> values)
    {
        var list = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? null : string.Join("、", list);
    }

    static string? Rating(JsonElement node, string averageProperty, string countProperty)
    {
        if (!node.TryGetProperty(countProperty, out var count) || count.ValueKind != JsonValueKind.Number || count.GetInt32() <= 0 ||
            !node.TryGetProperty(averageProperty, out var average) || average.ValueKind != JsonValueKind.Number) return null;
        return average.GetDouble().ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + $"/10（{count.GetInt32()} 票）";
    }

    public static string CleanTitle(string filename)
    {
        var name = Regex.Replace(filename, @"\.(19|20)\d{2}.*$", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\b(1080p|2160p|720p|bluray|webrip|web-dl|h264|h265|x264|x265)\b", " ", RegexOptions.IgnoreCase);
        return Regex.Replace(name, @"[._\[\]()]+", " ").Trim();
    }

    public static async Task<List<MovieMetadata>> SearchTmdbAsync(string query, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("请先在设置中填写 TMDB API Read Access Token。");
        var results = new List<MovieMetadata>();
        foreach (var type in new[] { "movie", "tv" })
        {
            var url = $"https://api.themoviedb.org/3/search/{type}?query={Uri.EscapeDataString(query)}&language=zh-CN&include_adult=false";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Client.SendAsync(request);
            EnsureSourceSuccess(response, "TMDB");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            results.AddRange(ParseTmdbSearchResults(document.RootElement, type));
        }
        return results;
    }

    internal static List<MovieMetadata> ParseTmdbSearchResults(JsonElement root, string type)
    {
        if (type is not ("movie" or "tv") || root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("results", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("TMDB 搜索响应缺少作品列表，不能判定为无匹配。");
        var results = new List<MovieMetadata>();
        var adultEntries = 0;
        foreach (var entry in entries.EnumerateArray().Take(8))
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (entry.TryGetProperty("adult", out var searchAdult) && searchAdult.ValueKind == JsonValueKind.True)
            { adultEntries++; continue; }
            var titleName = type == "movie" ? "title" : "name";
            var title = Text(entry, titleName);
            var id = PositiveInt(entry, "id");
            if (string.IsNullOrWhiteSpace(title) || id is null) continue;
            if (TmdbUsagePolicy.HasAdultCue(title) ||
                TmdbUsagePolicy.HasAdultCue(Text(entry, "overview")))
            { adultEntries++; continue; }
            var dateName = type == "movie" ? "release_date" : "first_air_date";
            var releaseDate = Text(entry, dateName);
            var year = releaseDate is { Length: >= 4 } ? releaseDate[..4] : "";
            var posterPath = Text(entry, "poster_path");
            var result = new MovieMetadata {
                Title = year.Length == 4 ? $"{title} ({year})" : title, Code = "",
                Source = type == "movie" ? "TMDB 电影" : "TMDB 剧集",
                SourceUrl = $"https://www.themoviedb.org/{type}/{id}",
                PosterUrl = posterPath is not null && posterPath.StartsWith('/')
                    ? "https://image.tmdb.org/t/p/w500" + posterPath : null,
                OriginalTitle = Text(entry, type == "movie" ? "original_title" : "original_name"),
                ReleaseDate = releaseDate, Summary = Text(entry, "overview"),
                Rating = Rating(entry, "vote_average", "vote_count"),
                Language = Text(entry, "original_language"),
                DetailNote = "搜索摘要；选择后将读取完整详情"
            };
            result.StampAvailableFields("搜索结果");
            results.Add(result);
        }
        if (Math.Min(8, entries.GetArrayLength()) > adultEntries && results.Count == 0)
            throw new InvalidDataException("TMDB 搜索条目缺少可核对的作品标题或 ID，不能判定为无匹配。");
        return results;
    }

    public static async Task<List<MovieMetadata>> SearchPornhubAsync(string query)
    {
        var url = "https://www.pornhub.com/webmasters/search?search=" + Uri.EscapeDataString(query) + "&page=1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
        using var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ParsePornhubSearch(document.RootElement);
    }

    internal static List<MovieMetadata> ParsePornhubSearch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array)
            throw new JsonException("Pornhub 搜索接口未返回 videos 数组；无法确认没有匹配结果。");
        var results = new List<MovieMetadata>();
        foreach (var item in videos.EnumerateArray().Take(15))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var sourceUrl = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var posterUrl = item.TryGetProperty("default_thumb", out var p) ? p.GetString() : null;
            if (title.Length == 0 || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) || source.Host != "www.pornhub.com" || source.Scheme != "https") continue;
            var result = new MovieMetadata {
                Title = title, Source = "Pornhub", SourceUrl = sourceUrl, PosterUrl = posterUrl,
                ReleaseDate = Text(item, "publish_date"),
                RuntimeMinutes = ParseDurationMinutes(Text(item, "duration")),
                Genres = NamedArray(item, "categories", "category"),
                Keywords = NamedArray(item, "tags", "tag_name"),
                Actors = NamedArray(item, "pornstars", "pornstar_name"),
                DetailFetched = true,
                DetailNote = "公开搜索接口所提供的信息；未公开的字段保持空白"
            };
            if (item.TryGetProperty("rating", out var rating))
            {
                var value = rating.ValueKind == JsonValueKind.String ? rating.GetString() : rating.ValueKind == JsonValueKind.Number ? rating.ToString() : null;
                if (!string.IsNullOrWhiteSpace(value)) result.Rating = value.Trim();
            }
            result.StampAvailableFields("公开搜索接口");
            results.Add(result);
        }
        if (videos.GetArrayLength() > 0 && results.Count == 0)
            throw new JsonException("Pornhub 搜索结果缺少可验证的标题或链接；无法确认没有匹配结果。");
        return results;
    }

    public static async Task<List<MovieMetadata>> SearchTvMazeAsync(string query)
    {
        using var response = await Client.GetAsync("https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(query));
        EnsureSourceSuccess(response, "TVmaze");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ParseTvMazeSearchResults(document.RootElement);
    }

    internal static List<MovieMetadata> ParseTvMazeSearchResults(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("TVmaze 搜索响应缺少剧集列表，不能判定为无匹配。");
        var results = new List<MovieMetadata>();
        foreach (var entry in root.EnumerateArray().Take(10))
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("show", out var show) ||
                show.ValueKind != JsonValueKind.Object) continue;
            var title = Text(show, "name");
            var id = PositiveInt(show, "id");
            var image = show.TryGetProperty("image", out var imageNode) && imageNode.ValueKind == JsonValueKind.Object
                ? Text(imageNode, "original") : null;
            if (string.IsNullOrWhiteSpace(title) || id is null) continue;
            var result = new MovieMetadata {
                Title = title, Source = "TVmaze", SourceUrl = $"https://www.tvmaze.com/shows/{id}", PosterUrl = image,
                ReleaseDate = Text(show, "premiered"), Genres = NamedArray(show, "genres"),
                Summary = PlainText(Text(show, "summary")), RuntimeMinutes = PositiveInt(show, "averageRuntime") ?? PositiveInt(show, "runtime"),
                Language = Text(show, "language"), DetailNote = "搜索摘要；选择后将读取完整详情"
            };
            result.StampAvailableFields("搜索结果");
            results.Add(result);
        }
        if (root.GetArrayLength() > 0 && results.Count == 0)
            throw new InvalidDataException("TVmaze 搜索条目缺少可核对的剧集标题或 ID，不能判定为无匹配。");
        return results;
    }

    internal static void EnsureSourceSuccess(HttpResponseMessage response, string source)
    {
        if (response.IsSuccessStatusCode) return;
        if (source == "TMDB" && response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("TMDB 凭据无效，请到搜刮页检查。");
        var header = response.Headers.RetryAfter;
        var retry = header?.Delta ?? (header?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : null);
        if (retry <= TimeSpan.Zero) retry = null;
        throw new ScrapeHttpException($"{source} 返回 HTTP {(int)response.StatusCode}，请检查来源状态。",
            response.StatusCode, retry);
    }

    static int? ParseDurationMinutes(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration)) return null;
        var parts = duration.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(part => !int.TryParse(part, out _))) return null;
        var numbers = parts.Select(int.Parse).ToArray();
        var seconds = numbers.Length == 3 ? numbers[0] * 3600 + numbers[1] * 60 + numbers[2] : numbers[0] * 60 + numbers[1];
        return seconds > 0 ? (int)Math.Ceiling(seconds / 60d) : null;
    }

    static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var paragraphs = Regex.Replace(html, @"</(?:p|div|br)>|<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        var cleaned = WebUtility.HtmlDecode(Regex.Replace(paragraphs, "<[^>]+>", " "));
        cleaned = Regex.Replace(cleaned, @"[ \t]+", " ");
        return cleaned.Trim();
    }

    // Search responses are intentionally small. Enrich only the chosen match to
    // avoid 10–20 extra network requests and UI stalls for each search.
    public static async Task<MovieMetadata> FetchFullDetailsAsync(MovieMetadata candidate, string tmdbToken,
        string javinfoKey = "", string javinfoDataDir = "", int javinfoDailyLimit = JavinfoScraper.DefaultDailyRequestLimit,
        JavinfoSearchEvidence? javinfoEvidence = null)
    {
        if (candidate.Source.StartsWith("TMDB", StringComparison.OrdinalIgnoreCase))
            return await FetchTmdbDetailsAsync(candidate, tmdbToken);
        if (candidate.Source.Equals("TVmaze", StringComparison.OrdinalIgnoreCase))
            return await FetchTvMazeDetailsAsync(candidate);
        if (candidate.Source.Equals("Wikidata", StringComparison.OrdinalIgnoreCase))
            return await WikidataScraper.FetchFullDetailsAsync(candidate);
        if (candidate.Source.Equals("Kitsu", StringComparison.OrdinalIgnoreCase))
            return await KitsuScraper.FetchFullDetailsAsync(candidate);
        if (candidate.Source.Equals("Tenrai", StringComparison.OrdinalIgnoreCase))
            return await TenraiScraper.FetchFullDetailsAsync(candidate);
        if (candidate.Source.Equals("OMDb", StringComparison.OrdinalIgnoreCase))
            return await OmdbScraper.FetchFullDetailsAsync(candidate);
        if (candidate.Source.Equals("Trakt", StringComparison.OrdinalIgnoreCase))
            return await TraktScraper.FetchFullDetailsAsync(candidate);
        if (candidate.Source.Equals("Bangumi", StringComparison.OrdinalIgnoreCase))
            return await BangumiScraper.FetchFullDetailsAsync(candidate);
        if (candidate.Source.Equals("AniList", StringComparison.OrdinalIgnoreCase))
            return await AniListScraper.FetchFullDetailsAsync(candidate);
        if (candidate.Source.Equals("JavDB", StringComparison.OrdinalIgnoreCase))
            return await JavDbScraper.FetchCandidateDetailsAsync(candidate);
        if (candidate.Source.Equals("JAVDatabase", StringComparison.OrdinalIgnoreCase))
            return await JavDatabaseScraper.FetchCandidateDetailsAsync(candidate);
        if (candidate.Source.StartsWith("Javinfo", StringComparison.OrdinalIgnoreCase))
            return await JavinfoScraper.FetchCandidateDetailsAsync(candidate, javinfoKey, javinfoDataDir,
                javinfoEvidence, javinfoDailyLimit);
        return candidate.Clone();
    }

    static async Task<MovieMetadata> FetchTmdbDetailsAsync(MovieMetadata candidate, string token)
    {
        var match = Regex.Match(candidate.SourceUrl, @"^https://www\.themoviedb\.org/(?<type>movie|tv)/(?<id>[0-9]+)(?:[/?#].*)?$", RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidOperationException("TMDB 候选缺少有效的详情链接。");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("请先保存 TMDB API Read Access Token。");
        var type = match.Groups["type"].Value.ToLowerInvariant();
        var url = $"https://api.themoviedb.org/3/{type}/{match.Groups["id"].Value}?language=zh-CN&append_to_response=credits,keywords";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Client.SendAsync(request);
        EnsureSourceSuccess(response, "TMDB");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ParseTmdbDetails(candidate, document.RootElement, type);
    }

    internal static MovieMetadata ParseTmdbDetails(MovieMetadata candidate, JsonElement detail, string type)
    {
        var match = Regex.Match(candidate.SourceUrl, @"^https://www\.themoviedb\.org/(?<type>movie|tv)/(?<id>[0-9]+)(?:[/?#].*)?$", RegexOptions.IgnoreCase);
        if (!match.Success || !match.Groups["type"].Value.Equals(type, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(match.Groups["id"].Value, out var requestedId) ||
            detail.ValueKind != JsonValueKind.Object || !detail.TryGetProperty("id", out var idNode) ||
            idNode.ValueKind != JsonValueKind.Number ||
            !idNode.TryGetInt32(out var returnedId) || requestedId != returnedId)
            throw new InvalidDataException("TMDB 详情 ID 与选中的候选不一致；资料未采用。");
        if (detail.TryGetProperty("adult", out var adultFlag) && adultFlag.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("TMDB 返回成人作品；资料和海报均未采用。");
        if (TmdbUsagePolicy.HasAdultCue(Text(detail, type == "movie" ? "title" : "name")) ||
            TmdbUsagePolicy.HasAdultCue(Text(detail, "overview")))
            throw new InvalidDataException("TMDB 详情包含成人内容标识；资料和海报均未采用。");
        var record = candidate.Clone();
        record.IsAdultContent = detail.TryGetProperty("adult", out var adultNode) &&
            adultNode.ValueKind == JsonValueKind.True;
        var title = Text(detail, type == "movie" ? "title" : "name");
        record.ReleaseDate = Text(detail, type == "movie" ? "release_date" : "first_air_date") ?? record.ReleaseDate;
        if (!string.IsNullOrWhiteSpace(title))
        {
            var year = !string.IsNullOrWhiteSpace(record.ReleaseDate) && record.ReleaseDate.Length >= 4 ? record.ReleaseDate[..4] : null;
            record.Title = year is not null && !title.EndsWith($"({year})", StringComparison.Ordinal) ? $"{title} ({year})" : title;
        }
        record.OriginalTitle = Text(detail, type == "movie" ? "original_title" : "original_name") ?? record.OriginalTitle;
        record.Summary = Text(detail, "overview") ?? record.Summary;
        var posterPath = Text(detail, "poster_path");
        if (!string.IsNullOrWhiteSpace(posterPath) && posterPath.StartsWith('/')) record.PosterUrl = "https://image.tmdb.org/t/p/w500" + posterPath;
        record.Genres = NamedArray(detail, "genres");
        record.RuntimeMinutes = PositiveInt(detail, "runtime") ?? (detail.TryGetProperty("episode_run_time", out var runTimes) && runTimes.ValueKind == JsonValueKind.Array
            ? runTimes.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number).Select(item => (int?)item.GetInt32()).FirstOrDefault(value => value > 0) : null);
        record.Studio = type == "movie" ? Joined(NamedArray(detail, "production_companies")) : Joined(NamedArray(detail, "networks"));
        if (detail.TryGetProperty("production_countries", out var countries) && countries.ValueKind == JsonValueKind.Array)
            record.Country = Joined(NamedArray(detail, "production_countries"));
        record.Country ??= Joined(NamedArray(detail, "origin_country"));
        record.Language = Text(detail, "original_language") ?? record.Language;
        if (detail.TryGetProperty("spoken_languages", out var languages) && languages.ValueKind == JsonValueKind.Array)
            record.Language = Joined(NamedArray(detail, "spoken_languages", "english_name")) ?? record.Language;
        record.Rating = Rating(detail, "vote_average", "vote_count") ?? record.Rating;
        if (detail.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
        {
            record.Actors = NamedArray(credits, "cast", limit: 100);
            record.PersonCredits = !record.IsAdultContent && credits.TryGetProperty("cast", out var cast) &&
                cast.ValueKind == JsonValueKind.Array
                ? cast.EnumerateArray().Select(item =>
                {
                    var id = PositiveInt(item, "id");
                    var name = Text(item, "name");
                    return id is > 0 && !string.IsNullOrWhiteSpace(name)
                        ? new VerifiedPersonCredit
                        {
                            Provider = "TMDB", PersonId = id.Value.ToString(),
                            PersonUrl = $"https://www.themoviedb.org/person/{id.Value}",
                            Name = name, CharacterName = Text(item, "character") ?? "",
                            VerifiedFromWorkUrl = record.SourceUrl
                        } : null;
                }).Where(item => item is not null).Select(item => item!).DistinctBy(item => item.PersonId)
                    .ToList() : [];
            if (credits.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array)
                record.Director = Joined(crew.EnumerateArray().Where(item => string.Equals(Text(item, "job"), "Director", StringComparison.OrdinalIgnoreCase))
                    .Select(item => Text(item, "name") ?? ""));
        }
        if (string.IsNullOrWhiteSpace(record.Director) && type == "tv") record.Director = Joined(NamedArray(detail, "created_by"));
        if (detail.TryGetProperty("keywords", out var keywords) && keywords.ValueKind == JsonValueKind.Object)
        {
            var key = type == "movie" ? "keywords" : "results";
            record.Keywords = NamedArray(keywords, key);
        }
        record.RefreshChangedFieldProvenance(candidate, "详情接口");
        record.DetailFetched = true;
        record.DetailIdentityVerified = true;
        record.DetailNote = "已读取 TMDB 详情和演职员表；来源未提供的字段保持空白";
        var tmdbRating = RatingAggregation.FromMetadata(record, "详情接口作品一致");
        if (tmdbRating is not null) RatingAggregation.AddOrUpdate(record, tmdbRating);
        return record;
    }

    static async Task<MovieMetadata> FetchTvMazeDetailsAsync(MovieMetadata candidate)
    {
        var match = Regex.Match(candidate.SourceUrl, @"^https://www\.tvmaze\.com/shows/(?<id>[0-9]+)(?:[/?#].*)?$", RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidOperationException("TVmaze 候选缺少有效的详情链接。");
        var url = $"https://api.tvmaze.com/shows/{match.Groups["id"].Value}?embed[]=cast&embed[]=crew";
        using var response = await Client.GetAsync(url);
        EnsureSourceSuccess(response, "TVmaze");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ParseTvMazeDetails(candidate, document.RootElement);
    }

    internal static MovieMetadata ParseTvMazeDetails(MovieMetadata candidate, JsonElement detail)
    {
        var match = Regex.Match(candidate.SourceUrl, @"^https://www\.tvmaze\.com/shows/(?<id>[0-9]+)(?:[/?#].*)?$", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["id"].Value, out var requestedId) ||
            detail.ValueKind != JsonValueKind.Object || !detail.TryGetProperty("id", out var idNode) ||
            idNode.ValueKind != JsonValueKind.Number ||
            !idNode.TryGetInt32(out var returnedId) || requestedId != returnedId)
            throw new InvalidDataException("TVmaze 详情 ID 与选中的候选不一致；资料未采用。");
        var record = candidate.Clone();
        record.Title = Text(detail, "name") ?? record.Title;
        record.ReleaseDate = Text(detail, "premiered") ?? record.ReleaseDate;
        record.Summary = PlainText(Text(detail, "summary")) ?? record.Summary;
        if (detail.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object)
            record.PosterUrl = Text(image, "original") ?? record.PosterUrl;
        record.Genres = NamedArray(detail, "genres");
        record.RuntimeMinutes = PositiveInt(detail, "averageRuntime") ?? PositiveInt(detail, "runtime") ?? record.RuntimeMinutes;
        record.Language = Text(detail, "language") ?? record.Language;
        if (detail.TryGetProperty("rating", out var rating) && rating.ValueKind == JsonValueKind.Object &&
            rating.TryGetProperty("average", out var average) && average.ValueKind == JsonValueKind.Number)
            record.Rating = average.GetDouble().ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "/10";
        foreach (var channel in new[] { "network", "webChannel" })
            if (detail.TryGetProperty(channel, out var network) && network.ValueKind == JsonValueKind.Object)
            {
                record.Studio ??= Text(network, "name");
                if (network.TryGetProperty("country", out var country) && country.ValueKind == JsonValueKind.Object)
                    record.Country ??= Text(country, "name");
            }
        if (detail.TryGetProperty("_embedded", out var embedded) && embedded.ValueKind == JsonValueKind.Object)
        {
            if (embedded.TryGetProperty("cast", out var cast) && cast.ValueKind == JsonValueKind.Array)
            {
                record.Actors = cast.EnumerateArray().Where(item => item.TryGetProperty("person", out _))
                    .Select(item => Text(item.GetProperty("person"), "name")).Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
                record.PersonCredits = cast.EnumerateArray().Select(item =>
                {
                    if (!item.TryGetProperty("person", out var person) || person.ValueKind != JsonValueKind.Object)
                        return null;
                    var id = PositiveInt(person, "id");
                    var name = Text(person, "name");
                    if (id is not > 0 || string.IsNullOrWhiteSpace(name)) return null;
                    var character = item.TryGetProperty("character", out var role) &&
                        role.ValueKind == JsonValueKind.Object ? Text(role, "name") : null;
                    return new VerifiedPersonCredit
                    {
                        Provider = "TVmaze", PersonId = id.Value.ToString(),
                        PersonUrl = $"https://www.tvmaze.com/people/{id.Value}",
                        Name = name, CharacterName = character ?? "", VerifiedFromWorkUrl = record.SourceUrl
                    };
                }).Where(item => item is not null).Select(item => item!).DistinctBy(item => item.PersonId)
                    .ToList();
            }
            if (embedded.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array)
                record.Director = Joined(crew.EnumerateArray().Where(item => string.Equals(Text(item, "type"), "Director", StringComparison.OrdinalIgnoreCase))
                    .Where(item => item.TryGetProperty("person", out _)).Select(item => Text(item.GetProperty("person"), "name") ?? ""));
        }
        record.RefreshChangedFieldProvenance(candidate, "详情接口");
        record.DetailFetched = true;
        record.DetailIdentityVerified = true;
        record.DetailNote = "已读取 TVmaze 剧集详情及公开演职员信息；未提供的字段保持空白";
        var tvMazeRating = RatingAggregation.FromMetadata(record, "详情接口作品一致");
        if (tvMazeRating is not null) RatingAggregation.AddOrUpdate(record, tvMazeRating);
        return record;
    }

    public static async Task DownloadPosterAsync(MovieMetadata record, string videoId, string dataDir,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(record.PosterUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0) return;
        var allowed = record.Source.StartsWith("TMDB", StringComparison.OrdinalIgnoreCase)
            ? uri.Host.Equals("image.tmdb.org", StringComparison.OrdinalIgnoreCase)
            : record.Source.Equals("TVmaze", StringComparison.OrdinalIgnoreCase)
                ? uri.Host.Equals("static.tvmaze.com", StringComparison.OrdinalIgnoreCase)
                : record.Source.Equals("Bangumi", StringComparison.OrdinalIgnoreCase)
                    ? uri.Host.Equals("lain.bgm.tv", StringComparison.OrdinalIgnoreCase)
                    : record.Source.Equals("AniList", StringComparison.OrdinalIgnoreCase)
                        ? uri.Host.EndsWith(".anilist.co", StringComparison.OrdinalIgnoreCase)
                    : record.Source.Equals("Kitsu", StringComparison.OrdinalIgnoreCase)
                        ? uri.Host.Equals("media.kitsu.app", StringComparison.OrdinalIgnoreCase)
                          || uri.Host.Equals("media.kitsu.io", StringComparison.OrdinalIgnoreCase)
                    : record.Source.Equals("Tenrai", StringComparison.OrdinalIgnoreCase)
                        ? uri.Host.Equals("cdn.myanimelist.net", StringComparison.OrdinalIgnoreCase)
                    : record.Source.Equals("JavDB", StringComparison.OrdinalIgnoreCase)
                        ? uri.Host.Equals("jdbstatic.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".jdbstatic.com", StringComparison.OrdinalIgnoreCase)
                        : record.Source.Equals("JAVDatabase", StringComparison.OrdinalIgnoreCase)
                            ? uri.Host.Equals("www.javdatabase.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("javdatabase.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("pics.dmm.co.jp", StringComparison.OrdinalIgnoreCase)
                            : record.Source.StartsWith("Javinfo", StringComparison.OrdinalIgnoreCase)
                                ? uri.Host.Equals("pics.dmm.co.jp", StringComparison.OrdinalIgnoreCase)
                                  || uri.Host.Equals("www.javdatabase.com", StringComparison.OrdinalIgnoreCase)
                                  || uri.Host.Equals("javdatabase.com", StringComparison.OrdinalIgnoreCase)
                                  || uri.Host.Equals("jdbstatic.com", StringComparison.OrdinalIgnoreCase)
                                  || uri.Host.EndsWith(".jdbstatic.com", StringComparison.OrdinalIgnoreCase)
                            : record.Source.Equals("Pornhub", StringComparison.OrdinalIgnoreCase) &&
                              (uri.Host.EndsWith(".phncdn.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("phncdn.com", StringComparison.OrdinalIgnoreCase));
        if (!allowed) return;
        try
        {
            using var response = await PosterClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var bytes = await ReadBoundedPosterAsync(response, cancellationToken);
            if (bytes is null) return;
            record.Poster = await CandidatePosterStore.StoreAsync(dataDir, videoId, bytes, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { /* The title remains useful if cover retrieval fails. */ }
    }

    internal static async Task<byte[]?> ReadBoundedPosterAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
            || response.Content.Headers.ContentLength > MaxPosterBytes) return null;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + count > MaxPosterBytes) return null;
            output.Write(buffer, 0, count);
        }
        return output.Length > 0 ? output.ToArray() : null;
    }
}

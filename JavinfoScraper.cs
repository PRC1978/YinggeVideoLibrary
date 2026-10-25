using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Paid, opt-in metadata source. Every network request goes through the durable
// daily reservation below. An attempted request consumes a reservation even if
// it fails; this is deliberately more conservative than the provider's billing.
public static class JavinfoScraper
{
    public const int DefaultDailyRequestLimit = 50;
    const int MaxResponseBytes = 1_000_000;
    static readonly Uri ApiRoot = new("https://api.javinfo.dev/");
    static readonly HttpClient Client = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("YinggeVideoLibrary/1.5");
        return client;
    }

    public static async Task<List<MovieMetadata>> SearchAsync(JavinfoSearchEvidence evidence, string apiKey, string dataDir,
        int dailyLimit = DefaultDailyRequestLimit, CancellationToken cancellationToken = default)
    {
        if (evidence is null) throw new InvalidOperationException("Javinfo 需要明确番号和已核对的日文作品原名；本次不会发出付费请求。");
        // The paid API receives only a verified number. The Japanese title is
        // retained for validating the selected detail, never used as a fuzzy
        // paid search query.
        using var response = await PostAsync("query", new { q = evidence.Code, num = 10 }, apiKey, dataDir, dailyLimit, cancellationToken);
        using var json = await ReadBoundedJsonAsync(response, cancellationToken);
        return ExactNumberCandidates(ParseSearchResults(json.RootElement), evidence);
    }

    internal static List<MovieMetadata> ExactNumberCandidates(IEnumerable<MovieMetadata> candidates,
        JavinfoSearchEvidence evidence) => candidates
        .Where(item => item.Source?.StartsWith("Javinfo", StringComparison.OrdinalIgnoreCase) == true &&
            string.Equals(item.Code, evidence.Code, StringComparison.OrdinalIgnoreCase)).ToList();

    public static async Task<MovieMetadata> FetchCandidateDetailsAsync(MovieMetadata candidate, string apiKey, string dataDir,
        JavinfoSearchEvidence? evidence, int dailyLimit = DefaultDailyRequestLimit, CancellationToken cancellationToken = default)
    {
        if (evidence is null) throw new InvalidOperationException("Javinfo 需要明确番号和已核对的日文作品原名；本次不会发出付费请求。");
        if (candidate.Source?.StartsWith("Javinfo", StringComparison.OrdinalIgnoreCase) != true
            || !string.Equals(candidate.Code, evidence.Code, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Javinfo 候选番号与当前视频已核对番号不一致；没有发出详情付费请求。");
        using var response = await PostAsync("movie", new { q = candidate.Code }, apiKey, dataDir, dailyLimit, cancellationToken);
        using var json = await ReadBoundedJsonAsync(response, cancellationToken);
        var details = ParseMovieDetails(candidate, json.RootElement);
        var returnedTitleJa = json.RootElement.TryGetProperty("result", out var item) && item.ValueKind == JsonValueKind.Object
            ? Text(item, "titleJa") : null;
        if (!evidence.MatchesTitleJa(returnedTitleJa))
            throw new InvalidDataException("Javinfo 详情未返回匹配的日文原名；详情未写入，请人工核对。此请求可能已消耗来源额度。");
        return details;
    }

    static async Task<HttpResponseMessage> PostAsync(string endpoint, object body, string apiKey, string dataDir,
        int dailyLimit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || !apiKey.Trim().StartsWith("jvi_", StringComparison.Ordinal))
            throw new InvalidOperationException("请先在搜刮页保存有效的 Javinfo API Key。");
        if (endpoint is not ("query" or "movie")) throw new ArgumentOutOfRangeException(nameof(endpoint));
        cancellationToken.ThrowIfCancellationRequested();
        var wait = JavinfoUsageLedger.Reserve(dataDir, dailyLimit);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoot, endpoint));
        if (!request.Headers.TryAddWithoutValidation("x-javinfo-key", apiKey.Trim()))
            throw new InvalidOperationException("Javinfo API Key 格式无效。");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); throw new ScrapeNotFoundException("Javinfo 没有找到匹配资料。"); }
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            var retry = response.Headers.RetryAfter?.Delta;
            response.Dispose();
            throw status switch
            {
                HttpStatusCode.Unauthorized => new InvalidOperationException("Javinfo API Key 无效，请在搜刮页重新保存。"),
                HttpStatusCode.PaymentRequired => new InvalidOperationException("Javinfo 额度不足，请检查账户余额。"),
                HttpStatusCode.TooManyRequests => new ScrapeHttpException("Javinfo 已达到接口限速，请稍后重试。", status, retry),
                HttpStatusCode.ServiceUnavailable => new ScrapeHttpException("Javinfo 来源暂时不可用，请稍后重试。", status, retry),
                _ => new HttpRequestException($"Javinfo 返回 HTTP {(int)status}。", null, status)
            };
        }
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            response.Dispose();
            throw new InvalidDataException("Javinfo 响应过大，已停止读取。");
        }
        return response;
    }

    static async Task<JsonDocument> ReadBoundedJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not ("application/json" or "application/problem+json"))
            throw new InvalidDataException("Javinfo 未返回 JSON，可能是访问限制或临时错误。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int length;
        while ((length = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + length > MaxResponseBytes) throw new InvalidDataException("Javinfo 响应过大，已停止读取。");
            memory.Write(buffer, 0, length);
        }
        memory.Position = 0;
        return await JsonDocument.ParseAsync(memory, cancellationToken: cancellationToken);
    }

    internal static List<MovieMetadata> ParseSearchResults(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var hits) || hits.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Javinfo 候选响应缺少 results；此次不会标记为无匹配。");
        var provider = Text(root, "source") ?? "聚合来源";
        var found = new List<MovieMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits.EnumerateArray().Take(10))
        {
            if (hit.ValueKind != JsonValueKind.Object) continue;
            var code = (Text(hit, "dvdId") ?? "").Trim().ToUpperInvariant();
            var title = Text(hit, "title");
            if (code.Length == 0 || string.IsNullOrWhiteSpace(title) || !seen.Add(code)) continue;
            var sourceUrl = ApiMovieUrl(code);
            if (hit.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Object)
                sourceUrl = SafeHttpsUrl(Text(extra, "url")) ?? sourceUrl;
            var candidate = new MovieMetadata
            {
                Code = code, Title = title, Source = "Javinfo · " + provider, SourceUrl = sourceUrl,
                PosterUrl = SafeHttpsUrl(Text(hit, "cover")), ReleaseDate = Date(Text(hit, "releaseDate")),
                Studio = hit.TryGetProperty("extra", out extra) && extra.ValueKind == JsonValueKind.Object ? Text(extra, "studio") : null
            };
            candidate.StampAvailableFields("搜索结果");
            found.Add(candidate);
        }
        if (hits.GetArrayLength() > 0 && found.Count == 0)
            throw new InvalidDataException("Javinfo 返回了候选，但未包含可核对的番号；此次不会标记为无匹配。");
        return found;
    }

    internal static MovieMetadata ParseMovieDetails(MovieMetadata candidate, JsonElement root)
    {
        if (!root.TryGetProperty("result", out var item) || item.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Javinfo 详情响应缺少 result；不会写入不完整资料。");
        var code = (Text(item, "dvdId") ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0 || !code.Equals(candidate.Code, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Javinfo 详情番号与选中的候选不一致；已拒绝写入。");
        if (Text(item, "titleEn") is null && Text(item, "titleJa") is null)
            throw new InvalidDataException("Javinfo 详情缺少作品标题；已拒绝写入不完整资料。");
        var result = candidate.Clone();
        result.Title = Text(item, "titleEn") ?? Text(item, "titleJa") ?? result.Title;
        result.OriginalTitle = Text(item, "titleJa") ?? result.OriginalTitle;
        result.Summary = Text(item, "commentEn") ?? Text(item, "commentJa") ?? result.Summary;
        result.ReleaseDate = Date(Text(item, "releaseDate")) ?? result.ReleaseDate;
        if (item.TryGetProperty("runtimeMins", out var runtime) && runtime.ValueKind == JsonValueKind.Number
            && runtime.TryGetInt32(out var minutes) && minutes is > 0 and < 10000) result.RuntimeMinutes = minutes;
        result.Studio = FirstString(item, "makers") ?? Text(item, "label") ?? result.Studio;
        result.Series = Text(item, "series") ?? result.Series;
        result.Director = JoinStrings(item, "directors") ?? result.Director;
        result.Actors = JoinStringArrays(item, "actresses", "actors");
        result.Genres = StringArray(item, "categories");
        result.PosterUrl = SafeHttpsUrl(Text(item, "jacketFullUrl"))
            ?? SafeHttpsUrl(Text(item, "jacketThumbUrl")) ?? result.PosterUrl;
        var provider = Text(root, "source") ?? "聚合来源";
        result.Source = "Javinfo · " + provider;
        // The selected card can link to a provider's search page, whereas
        // these fields came from the authenticated Javinfo detail response.
        // Keep existing search-stage provenance on unchanged fields.
        result.SourceUrl = ApiMovieUrl(code);
        result.DetailFetched = true;
        result.DetailIdentityVerified = true;
        result.DetailNote = "Javinfo 汇总的公开作品资料；请核对来源与匹配结果。";
        result.RefreshChangedFieldProvenance(candidate, "详情接口");
        return result;
    }

    static string ApiMovieUrl(string code) => new Uri(ApiRoot, "movie?q=" + Uri.EscapeDataString(code)).AbsoluteUri;
    static string? SafeHttpsUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.HostNameType != UriHostNameType.Dns
            || !uri.Host.Contains('.') || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return null;
        return uri.AbsoluteUri;
    }
    static string? Text(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
    static string? Date(string? value) => value is { Length: >= 10 }
        && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    static List<string> StringArray(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return [];
        return array.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()?.Trim() ?? "").Where(value => value.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    static string? FirstString(JsonElement item, string name) => StringArray(item, name).FirstOrDefault();
    static string? JoinStrings(JsonElement item, string name)
    {
        var values = StringArray(item, name);
        return values.Count == 0 ? null : string.Join(" / ", values);
    }
    static List<string> JoinStringArrays(JsonElement item, params string[] names) => names
        .SelectMany(name => StringArray(item, name)).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
}

internal static class JavinfoUsageLedger
{
    sealed class Ledger
    {
        public string UtcDay { get; set; } = "";
        public int ReservedCalls { get; set; }
        public DateTimeOffset NextAllowedUtc { get; set; }
    }
    static readonly Mutex WriteMutex = new(false, "Local\\YinggeVideoLibrary.JavinfoUsage");

    internal static TimeSpan Reserve(string dataDir, int dailyLimit, DateTimeOffset? now = null)
    {
        dailyLimit = Math.Clamp(dailyLimit, 1, 100);
        var utcNow = now ?? DateTimeOffset.UtcNow;
        var path = Path.Combine(dataDir, "javinfo-usage.json");
        var ownsMutex = false;
        try
        {
            try { WriteMutex.WaitOne(); ownsMutex = true; }
            catch (AbandonedMutexException) { ownsMutex = true; }
            Directory.CreateDirectory(dataDir);
            Ledger ledger;
            if (File.Exists(path))
            {
                // A corrupt ledger must not reset a paid-source quota.
                ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("Javinfo 用量记录无法读取；已停止付费请求。");
                if (ledger.UtcDay.Length != 10 || ledger.ReservedCalls < 0)
                    throw new InvalidDataException("Javinfo 用量记录无效；已停止付费请求。");
            }
            else ledger = new Ledger();
            var today = utcNow.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.CompareOrdinal(today, ledger.UtcDay) > 0)
            {
                ledger.UtcDay = today;
                ledger.ReservedCalls = 0;
            }
            if (ledger.ReservedCalls >= dailyLimit)
                throw new InvalidOperationException($"Javinfo 今日请求已达到 {dailyLimit} 次上限；明日再试或调整上限。");
            var slot = ledger.NextAllowedUtc > utcNow ? ledger.NextAllowedUtc : utcNow;
            ledger.NextAllowedUtc = slot.AddSeconds(13); // Safe for the provider's initial 5/minute account tier.
            ledger.ReservedCalls++;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(ledger));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return slot - utcNow;
        }
        finally { if (ownsMutex) WriteMutex.ReleaseMutex(); }
    }

    internal static (string utcDay, int reservedCalls) ReadForDiagnostics(string dataDir)
    {
        var ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(Path.Combine(dataDir, "javinfo-usage.json")))!;
        return (ledger.UtcDay, ledger.ReservedCalls);
    }
}

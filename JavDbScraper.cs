using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

public sealed class MetadataFieldProvenance
{
    public string Source { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Stage { get; set; } = "";

    public MetadataFieldProvenance Clone() => new() { Source = Source, SourceUrl = SourceUrl, Stage = Stage };
}

public sealed class JavDbCatalogLink
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string DisplayName => $"{Kind} · {Name}";
    public JavDbCatalogLink Clone() => new() { Kind = Kind, Name = Name, Url = Url };
}

public sealed class MovieMetadata
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "JavDB";
    public string SourceUrl { get; set; } = "";
    public string? Poster { get; set; }
    public string? PosterUrl { get; set; }
    public string? OriginalTitle { get; set; }
    public string? ReleaseDate { get; set; }
    public int? RuntimeMinutes { get; set; }
    public string? Director { get; set; }
    public string? Studio { get; set; }
    public string? Series { get; set; }
    public string? Country { get; set; }
    public string? Language { get; set; }
    public string? Summary { get; set; }
    public string? Rating { get; set; }
    // A rating is kept with its own scale and provenance. Old records retain
    // Rating until a source supplies structured ratings.
    public List<PlatformRating> PlatformRatings { get; set; } = [];
    // Only links that occurred on the verified JavDB detail page are kept.
    // They allow manual actor/series/tag browsing without guessing site IDs.
    public List<JavDbCatalogLink> CatalogLinks { get; set; } = [];
    public List<string> Actors { get; set; } = [];
    public List<string> Genres { get; set; } = [];
    public List<string> Keywords { get; set; } = [];
    // Existing metadata.json records have no field provenance. Leave those
    // fields unmarked rather than presenting a guessed source as evidence.
    public Dictionary<string, MetadataFieldProvenance> FieldProvenance { get; set; } = [];
    // The source record is complete only when its detail page/API was read.
    // Missing fields remain empty: a source may not publish them at all.
    public bool DetailFetched { get; set; }
    public string? DetailNote { get; set; }
    public DateTime ScrapedAt { get; set; } = DateTime.Now;

    public MovieMetadata Clone() => new()
    {
        Code = Code, Title = Title, Source = Source, SourceUrl = SourceUrl,
        Poster = Poster, PosterUrl = PosterUrl, OriginalTitle = OriginalTitle,
        ReleaseDate = ReleaseDate, RuntimeMinutes = RuntimeMinutes,
        Director = Director, Studio = Studio, Series = Series, Country = Country,
        Language = Language, Summary = Summary, Rating = Rating,
        PlatformRatings = PlatformRatings?.Where(value => value is not null).Select(value => value.Clone()).ToList() ?? [],
        CatalogLinks = CatalogLinks?.Where(value => value is not null).Select(value => value.Clone()).ToList() ?? [],
        Actors = Actors?.ToList() ?? [], Genres = Genres?.ToList() ?? [],
        Keywords = Keywords?.ToList() ?? [],
        FieldProvenance = FieldProvenance?.Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal) ?? [],
        DetailFetched = DetailFetched, DetailNote = DetailNote, ScrapedAt = ScrapedAt
    };

    Dictionary<string, string> PresentFieldValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) values[name] = value;
        }
        Add(nameof(Code), Code); Add(nameof(Title), Title); Add(nameof(PosterUrl), PosterUrl);
        Add(nameof(OriginalTitle), OriginalTitle); Add(nameof(ReleaseDate), ReleaseDate);
        if (RuntimeMinutes is > 0) values[nameof(RuntimeMinutes)] = RuntimeMinutes.Value.ToString();
        Add(nameof(Director), Director); Add(nameof(Studio), Studio); Add(nameof(Series), Series);
        Add(nameof(Country), Country); Add(nameof(Language), Language); Add(nameof(Summary), Summary);
        Add(nameof(Rating), Rating);
        Add(nameof(Actors), string.Join("\u001f", Actors ?? []));
        Add(nameof(Genres), string.Join("\u001f", Genres ?? []));
        Add(nameof(Keywords), string.Join("\u001f", Keywords ?? []));
        return values;
    }

    public void StampAvailableFields(string stage)
    {
        FieldProvenance ??= [];
        foreach (var name in PresentFieldValues().Keys)
            FieldProvenance[name] = new MetadataFieldProvenance { Source = Source, SourceUrl = SourceUrl, Stage = stage };
    }

    // Only mark a detail field when the detail response actually changed or
    // supplied it. An unchanged search value keeps its honest search origin.
    public void RefreshChangedFieldProvenance(MovieMetadata before, string stage)
    {
        FieldProvenance ??= [];
        var previous = before.PresentFieldValues();
        var current = PresentFieldValues();
        foreach (var (name, value) in current)
            if (!previous.TryGetValue(name, out var oldValue) || !string.Equals(value, oldValue, StringComparison.Ordinal))
                FieldProvenance[name] = new MetadataFieldProvenance { Source = Source, SourceUrl = SourceUrl, Stage = stage };
        foreach (var name in FieldProvenance.Keys.Where(name => !current.ContainsKey(name)).ToList())
            FieldProvenance.Remove(name);
    }
}

// A scrape candidate is kept separately from accepted metadata until a
// confidence decision is made.  This lets the user approve or reject a low
// confidence match later without repeating the network request.
public sealed class AiReviewRecord
{
    public string VideoId { get; set; } = "";
    public string CandidateCode { get; set; } = "";
    public string CandidateTitle { get; set; } = "";
    public string CandidateSource { get; set; } = "";
    public string CandidateSourceUrl { get; set; } = "";
    public string? CandidatePoster { get; set; }
    public string? CandidatePosterUrl { get; set; }
    public MovieMetadata? CandidateDetails { get; set; }
    public int Confidence { get; set; }
    public string Status { get; set; } = "pending";
    // ai/rules describe the automatic verdict; manual marks a later human decision.
    public string DecisionSource { get; set; } = "ai";
    public string Reason { get; set; } = "";
    public string Model { get; set; } = "local-rules";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? ReviewedAt { get; set; }

    public MovieMetadata ToMetadata()
    {
        // Legacy reviews have only the six original candidate fields.
        var result = CandidateDetails?.Clone() ?? new MovieMetadata();
        result.Code = CandidateCode;
        result.Title = CandidateTitle;
        result.Source = CandidateSource;
        result.SourceUrl = CandidateSourceUrl;
        result.Poster = CandidatePoster ?? result.Poster;
        result.PosterUrl = CandidatePosterUrl ?? result.PosterUrl;
        result.ScrapedAt = CreatedAt;
        return result;
    }
}

public static class MetadataStore
{
    // Metadata is shared by published copies opened from different working
    // folders.  Keep the replacement write atomic and serialize it across
    // processes so a second copy cannot overwrite the first copy's temporary
    // file while it is being moved into place.
    static readonly Mutex WriteMutex = new(false, "Local\\YinggeVideoLibrary.MetadataWrite");

    public static Dictionary<string, MovieMetadata> Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "metadata.json");
        if (TryLoad(path, out var entries)) return entries;
        // A previous process may have been interrupted between the backup and
        // the final move.  Recovering the last known-good file is preferable
        // to silently starting with an empty metadata dictionary.
        if (TryLoad(path + ".bak", out entries)) return entries;
        return [];
    }

    static bool TryLoad(string path, out Dictionary<string, MovieMetadata> entries)
    {
        entries = [];
        try
        {
            if (!File.Exists(path)) return false;
            entries = JsonSerializer.Deserialize<Dictionary<string, MovieMetadata>>(File.ReadAllText(path)) ?? [];
            return true;
        }
        catch { return false; }
    }

    public static void Save(string dataDir, Dictionary<string, MovieMetadata> entries)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, "metadata.json");
        var backup = path + ".bak";
        var temporary = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.{Guid.NewGuid():N}.tmp";
        var ownsMutex = false;
        try
        {
            try { WriteMutex.WaitOne(); ownsMutex = true; }
            catch (AbandonedMutexException) { ownsMutex = true; }

            // Merge entries that another copy added while this instance was
            // open.  Metadata is append/update-only, so preserving those keys
            // avoids losing accepted matches when two copies save together.
            if (TryLoad(path, out var latest))
            {
                foreach (var pair in latest)
                    if (!entries.ContainsKey(pair.Key)) entries[pair.Key] = pair.Value;
            }

            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporary, json);
            if (File.Exists(path)) File.Copy(path, backup, true);
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            if (ownsMutex) try { WriteMutex.ReleaseMutex(); } catch { }
        }
    }
}

public sealed class ScrapeHttpException(string message, HttpStatusCode statusCode, TimeSpan? retryAfter)
    : HttpRequestException(message, null, statusCode)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class ScrapeNotFoundException(string message) : Exception(message);

public static partial class JavDbScraper
{
    // Both attribute orders and quote styles occur in public result cards.
    static readonly Regex Cards = new("""<a\b(?=[^>]*\bhref\s*=\s*['\"](?<href>/v/[^'\"]+)['\"])(?=[^>]*\bclass\s*=\s*['\"][^'\"]*\bbox\b[^'\"]*['\"])[^>]*>(?<body>.*?)</a>""", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Codes = new("<strong\\b[^>]*>\\s*(?<code>[^<]+)\\s*</strong>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Title = new("""\btitle\s*=\s*['\"](?<value>[^'\"]+)['\"]""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Image = new("""<img\b[^>]*\b(?:data-src|src)\s*=\s*['\"](?<value>[^'\"]+)['\"]""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FileCode = new(@"(?<![A-Za-z0-9])([A-Za-z]{2,10})[-_ ]?(\d{2,5})(?![A-Za-z0-9])", RegexOptions.Compiled);
    static readonly Regex DetailBlocks = new("<div\\b[^>]*class=[\"'][^\"']*\\bpanel-block\\b[^\"']*[\"'][^>]*>(?<body>.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DetailLabel = new("<strong\\b[^>]*>(?<value>.*?)</strong>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DetailValue = new("<(?:span|div)\\b[^>]*class=[\"'][^\"']*\\bvalue\\b[^\"']*[\"'][^>]*>(?<value>.*?)</(?:span|div)>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DetailLinks = new("<a\\b[^>]*>(?<value>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex EmptySearchMessage = new(@"(?:沒有|没有|未能|找不到|未找到|暂无|暫無).{0,24}(?:搜尋結果|搜索结果|影片|视频)|(?:no|zero)\s+(?:matching\s+)?(?:results|videos)(?:\s+found)?", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex DetailPath = new(@"^/v/[A-Za-z0-9_-]{2,64}/?$", RegexOptions.Compiled);
    static readonly HttpClient Client = CreateClient();
    static readonly HttpClient PosterClient = CreateClient(allowRedirect: false);

    static HttpClient CreateClient(bool allowRedirect = true)
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli, AllowAutoRedirect = allowRedirect };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        return client;
    }

    public static string? GuessCode(string filename)
    {
        var fc2 = Regex.Match(filename, @"FC2[-_ ]?PPV[-_ ]?(\d{5,8})", RegexOptions.IgnoreCase);
        if (fc2.Success) return "FC2-PPV-" + fc2.Groups[1].Value;
        var normal = FileCode.Match(filename);
        return normal.Success ? normal.Groups[1].Value.ToUpperInvariant() + "-" + normal.Groups[2].Value : null;
    }

    // Title searches return review candidates only. They never download a cover
    // or accept a match automatically: a title alone cannot prove identity.
    public static async Task<List<MovieMetadata>> SearchTitleAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("请填写标题关键词。", nameof(query));
        query = query.Trim();
        if (query.Length > 200) query = query[..200];
        var url = "https://javdb.com/search?q=" + Uri.EscapeDataString(query) + "&f=all";
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken);
        EnsureSuccessfulResponse(response);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseTitleSearch(html);
    }

    internal static List<MovieMetadata> ParseTitleSearch(string html)
        => ParseCandidateCards(html, 12, true);

    internal static List<MovieMetadata> ParseCatalogPage(string html)
        => ParseCandidateCards(html, 48, false);

    static List<MovieMetadata> ParseCandidateCards(string html, int limit, bool searchPage)
    {
        EnsureReadablePage(html);
        var cards = Cards.Matches(html);
        var results = new List<MovieMetadata>(Math.Min(cards.Count, limit));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match card in cards)
        {
            var href = WebUtility.HtmlDecode(card.Groups["href"].Value);
            if (!TryCandidateUrl(href, out var sourceUrl) || !seen.Add(sourceUrl)) continue;
            var title = WebUtility.HtmlDecode(Title.Match(card.Value).Groups["value"].Value.Trim());
            var number = WebUtility.HtmlDecode(Codes.Match(card.Groups["body"].Value).Groups["code"].Value.Trim());
            // A missing number or title makes this unsuitable for approval.
            if (title.Length == 0 || number.Length is < 2 or > 40) continue;
            var poster = WebUtility.HtmlDecode(Image.Match(card.Groups["body"].Value).Groups["value"].Value.Trim());
            var candidate = new MovieMetadata
            {
                Code = number, Title = title, Source = "JavDB", SourceUrl = sourceUrl,
                PosterUrl = SafePosterUrl(poster),
                DetailNote = searchPage ? "搜索摘要；选择候选后读取公开详情，标题匹配须人工审核" : "关系目录摘要；选择后读取公开详情并人工审核"
            };
            candidate.StampAvailableFields("搜索结果");
            results.Add(candidate);
            if (results.Count == limit) break;
        }
        if (results.Count > 0) return results;
        if (cards.Count > 0) throw new InvalidOperationException("JavDB 搜索结果缺少可核对的标题或番号，页面结构可能已变化。");

        // A blank/blocked/changed page is not evidence that the title was not
        // found. Return an empty list only for a recognizable JavDB empty page.
        var plain = CleanHtml(html);
        var looksLikeJavDb = html.Contains("javdb", StringComparison.OrdinalIgnoreCase);
        if (looksLikeJavDb && EmptySearchMessage.IsMatch(plain)) return results;
        throw new InvalidOperationException(searchPage
            ? "JavDB 未返回可识别的搜索结果页，无法判断是否没有匹配。"
            : "JavDB 未返回可识别的关系目录页，无法判断是否没有作品。");
    }

    public static async Task<MovieMetadata> FetchCandidateDetailsAsync(MovieMetadata candidate, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(candidate.Source, "JavDB", StringComparison.OrdinalIgnoreCase)
            || !TryCandidateUrl(candidate.SourceUrl, out var detailUrl)
            || string.IsNullOrWhiteSpace(candidate.Code))
            throw new InvalidOperationException("JavDB 候选缺少有效的详情链接或番号。");
        using var response = await Client.GetAsync(detailUrl, HttpCompletionOption.ResponseContentRead, cancellationToken);
        EnsureSuccessfulResponse(response);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureReadablePage(html);
        var result = ParseJavDbDetails(candidate, html);
        if (!result.DetailFetched)
            throw new InvalidOperationException(result.DetailNote?.Contains("不一致", StringComparison.Ordinal) == true
                ? "JavDB 详情页番号与搜索候选不一致，请人工核对。"
                : "JavDB 详情页没有可识别的字段，无法确认完整资料。");
        return result;
    }

    static void EnsureSuccessfulResponse(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var retryHeader = response.Headers.RetryAfter;
            var retryAfter = retryHeader?.Delta ?? (retryHeader?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : null);
            throw new ScrapeHttpException($"JavDB 返回 {(int)response.StatusCode}，请稍后重试。", response.StatusCode, retryAfter);
        }
        var finalUrl = response.RequestMessage?.RequestUri;
        if (finalUrl is null || finalUrl.Scheme != Uri.UriSchemeHttps
            || !finalUrl.Host.Equals("javdb.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("JavDB 将请求转向了其他站点，未读取该页面。");
    }

    static void EnsureReadablePage(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) throw new InvalidOperationException("JavDB 返回了空白页面，无法判断匹配情况。");
        if (html.Contains("操作過於頻繁", StringComparison.OrdinalIgnoreCase)
            || html.Contains("操作过于频繁", StringComparison.OrdinalIgnoreCase)
            || html.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("JavDB 提示操作过于频繁，请稍后再试；当前不会把它当作未找到作品。");
        if (html.Contains("Due to copyright restrictions", StringComparison.OrdinalIgnoreCase)
            || html.Contains("版權限制", StringComparison.OrdinalIgnoreCase)
            || html.Contains("版权限制", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("JavDB 限制了当前地区的访问，暂时无法搜刮此来源。");
        var hasSourceContent = Cards.IsMatch(html) || DetailBlocks.IsMatch(html);
        if (!hasSourceContent && (html.Contains("cf-chl-", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
            || html.Contains("captcha", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("JavDB 返回了访问验证页面，暂时无法读取公开资料。");
        if (!hasSourceContent && (html.Contains("age verification", StringComparison.OrdinalIgnoreCase)
            || html.Contains("age gate", StringComparison.OrdinalIgnoreCase)
            || html.Contains("年齡驗證", StringComparison.OrdinalIgnoreCase)
            || html.Contains("年龄验证", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("JavDB 返回了年龄验证页面，暂时无法读取公开资料。");
    }

    static bool TryCandidateUrl(string value, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(new Uri("https://javdb.com"), value, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("javdb.com", StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443 || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !DetailPath.IsMatch(uri.AbsolutePath)) return false;
        url = uri.GetLeftPart(UriPartial.Path);
        return true;
    }

    static string? SafePosterUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.Host.Equals("jdbstatic.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".jdbstatic.com", StringComparison.OrdinalIgnoreCase) ? uri.ToString() : null;
    }

    public static async Task<MovieMetadata> ScrapeAsync(string code, string videoId, string dataDir, CancellationToken cancellationToken = default)
    {
        code = code.Trim().ToUpperInvariant();
        if (code.Length is < 3 or > 35 || !Regex.IsMatch(code, @"^[A-Z0-9-]+$")) throw new InvalidOperationException("番号格式无效。请使用字母、数字和连字符。");
        var searchUrl = "https://javdb.com/search?q=" + Uri.EscapeDataString(code) + "&f=all";
        using var response = await Client.GetAsync(searchUrl, HttpCompletionOption.ResponseContentRead, cancellationToken);
        EnsureSuccessfulResponse(response);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureReadablePage(html);
        Match? matched = null;
        foreach (Match card in Cards.Matches(html))
        {
            var number = Codes.Match(card.Groups["body"].Value);
            if (number.Success && string.Equals(WebUtility.HtmlDecode(number.Groups["code"].Value.Trim()), code, StringComparison.OrdinalIgnoreCase)) { matched = card; break; }
        }
        if (matched is null)
        {
            if (Cards.Matches(html).Count > 0) throw new ScrapeNotFoundException("JavDB 返回了结果，但没有完全匹配的番号。");
            throw new InvalidOperationException("JavDB 没有返回可识别的结果，可能无结果或页面结构已变化。");
        }
        var title = WebUtility.HtmlDecode(Title.Match(matched.Value).Groups["value"].Value.Trim());
        if (title.Length == 0) title = code;
        if (!TryCandidateUrl(matched.Groups["href"].Value, out var detail))
            throw new InvalidOperationException("JavDB 结果卡片包含无效详情链接，未读取该页面。");
        var posterUrl = WebUtility.HtmlDecode(Image.Match(matched.Groups["body"].Value).Groups["value"].Value);
        var record = new MovieMetadata { Code = code, Title = title, Source = "JavDB", SourceUrl = detail, PosterUrl = SafePosterUrl(posterUrl) };
        record.StampAvailableFields("搜索结果");
        try
        {
            using var detailResponse = await Client.GetAsync(detail, HttpCompletionOption.ResponseContentRead, cancellationToken);
            EnsureSuccessfulResponse(detailResponse);
            var detailHtml = await detailResponse.Content.ReadAsStringAsync(cancellationToken);
            EnsureReadablePage(detailHtml);
            record = ParseJavDbDetails(record, detailHtml);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { record.DetailNote = $"详情暂不可读（{ex.GetType().Name}）；当前仅有搜索摘要"; }
        string? poster = null;
        if (Uri.TryCreate(SafePosterUrl(posterUrl), UriKind.Absolute, out var imageUri) && imageUri.Port == 443 && imageUri.UserInfo.Length == 0)
        {
            try
            {
                using var imageResponse = await PosterClient.GetAsync(imageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                imageResponse.EnsureSuccessStatusCode();
                var bytes = await AdditionalScrapers.ReadBoundedPosterAsync(imageResponse, cancellationToken);
                if (bytes is not null)
                {
                    var folder = Path.Combine(dataDir, "posters"); Directory.CreateDirectory(folder);
                    poster = Path.Combine(folder, videoId + ".jpg");
                    await File.WriteAllBytesAsync(poster, bytes, cancellationToken);
                }
            }
            catch { /* Keep the title when the image host is unavailable. */ }
        }
        record.Poster = poster;
        return record;
    }

    static string CleanHtml(string html)
    {
        var withBreaks = Regex.Replace(html, @"<br\s*/?>|</(?:p|div)>", "\n", RegexOptions.IgnoreCase);
        return Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(withBreaks, "<[^>]+>", " ")), @"\s+", " ").Trim();
    }

    static List<string> DetailNames(string html)
    {
        var names = DetailLinks.Matches(html).Select(match => CleanHtml(match.Groups["value"].Value))
            .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count > 0) return names;
        return CleanHtml(html).Split(new[] { '、', ',', '，', '/' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Parse only labels actually present on the public detail page. A missing
    // block never becomes a guessed date, performer, studio or category.
    internal static MovieMetadata ParseJavDbDetails(MovieMetadata candidate, string html)
    {
        var record = candidate.Clone();
        var recognized = 0;
        var codeConflict = false;
        foreach (Match block in DetailBlocks.Matches(html))
        {
            var body = block.Groups["body"].Value;
            var labelMatch = DetailLabel.Match(body);
            if (!labelMatch.Success) continue;
            var label = CleanHtml(labelMatch.Groups["value"].Value).TrimEnd(':', '：').Replace(" ", "").ToLowerInvariant();
            var valueMatch = DetailValue.Match(body);
            var raw = valueMatch.Success ? valueMatch.Groups["value"].Value : body[(labelMatch.Index + labelMatch.Length)..];
            var value = CleanHtml(raw);
            if (value.Length == 0) continue;
            if (label is "番號" or "番号" or "number" or "識別碼")
            {
                // Do not replace the code verified on the search result card.
                if (string.Equals(value.Replace(" ", ""), record.Code, StringComparison.OrdinalIgnoreCase)) recognized++;
                else codeConflict = true;
            }
            else if (label.Contains("發行日期") || label.Contains("发行日期") || label.Contains("上映日期") || label is "releasedate")
            { record.ReleaseDate = value; recognized++; }
            else if (label.Contains("時長") || label.Contains("时长") || label.Contains("片長") || label.Contains("片长") || label is "duration")
            {
                var minutes = Regex.Match(value, @"(?<minutes>\d+)\s*(?:分|分鐘|分钟|minutes?|mins?)", RegexOptions.IgnoreCase);
                if (minutes.Success && int.TryParse(minutes.Groups["minutes"].Value, out var duration) && duration > 0) record.RuntimeMinutes = duration;
                recognized++;
            }
            else if (label.Contains("導演") || label.Contains("导演") || label is "director")
            { record.Director = string.Join("、", DetailNames(raw)); recognized++; }
            else if (label.Contains("片商") || label.Contains("製作商") || label.Contains("制作商") || label is "maker" or "studio")
            { record.Studio = string.Join("、", DetailNames(raw)); recognized++; }
            else if (label.Contains("系列") || label is "series")
            { record.Series = string.Join("、", DetailNames(raw)); recognized++; }
            else if (label.Contains("演員") || label.Contains("演员") || label is "actor" or "actress")
            { record.Actors = DetailNames(raw); recognized++; }
            else if (label.Contains("類別") || label.Contains("类别") || label.Contains("標籤") || label.Contains("标签") || label is "genre" or "genres")
            { record.Genres = DetailNames(raw); recognized++; }
            else if (label.Contains("評分") || label.Contains("评分") || label is "rating")
            { record.Rating = value; recognized++; }
            else if (label.Contains("簡介") || label.Contains("简介") || label.Contains("劇情") || label.Contains("剧情") || label is "summary")
            { record.Summary = value; recognized++; }
        }
        if (codeConflict)
        {
            var safe = candidate.Clone();
            safe.DetailNote = "详情页番号与搜索结果不一致；未采用该页资料，请人工核对";
            safe.DetailFetched = false;
            return safe;
        }
        record.CatalogLinks = ExtractCatalogLinks(html);
        record.RefreshChangedFieldProvenance(candidate, "详情页");
        record.DetailFetched = recognized > 0;
        record.DetailNote = recognized > 0 ? $"已读取 JavDB 详情页（{recognized} 项）；来源未提供的字段保持空白"
            : "详情页未返回可识别字段；当前仅有搜索摘要，请核对来源页面";
        // Preserve free-form site ratings as text. Only an explicit scale can
        // be compared with ratings from other platforms.
        var platformRating = RatingAggregation.FromMetadata(record, "详情页番号一致");
        if (platformRating is not null) RatingAggregation.AddOrUpdate(record, platformRating);
        return record;
    }
}

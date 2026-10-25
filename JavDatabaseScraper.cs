using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// JAVDatabase is an optional public, exact-number fallback. Its search and
// detail pages can be unavailable by region; do not infer a match from an
// HTTP 200 response or from a similarly named result.
public static class JavDatabaseScraper
{
    const string SourceName = "JAVDatabase";
    const string SiteHost = "www.javdatabase.com";
    const int MaximumHtmlLength = 2_000_000;

    static readonly Uri Site = new("https://www.javdatabase.com/");
    static readonly Regex ValidCode = new(@"^[A-Z0-9]{2,15}-[A-Z0-9-]{2,20}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Anchors = new(@"<a\b[^>]*\bhref\s*=\s*(['""'])(?<href>.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Heading = new(@"<h1\b[^>]*>(?<value>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex DetailRows = new(@"<p\b[^>]*class\s*=\s*(['""'])[^'""']*\bmb-1\b[^'""']*\1[^>]*>\s*<b\b[^>]*>(?<label>.*?)</b>(?<value>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Paragraphs = new(@"<p\b[^>]*>(?<body>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex FieldInParagraph = new(@"<(?:b|strong)\b[^>]*>(?<label>.*?)</(?:b|strong)>(?<value>.*?)(?=<(?:b|strong)\b|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex MetaTags = new(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Attribute = new(@"(?<name>[\w:-]+)\s*=\s*(['""'])(?<value>.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Links = new(@"<a\b[^>]*>(?<value>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex SearchCards = new(@"<(?:div|article)\b[^>]*\bclass\s*=\s*(['""'])[^'""']*\bborderlesscard\b[^'""']*\1[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex SearchCode = new(@"<p\b[^>]*\bclass\s*=\s*(['""'])[^'""']*\bpcard\b[^'""']*\1[^>]*>(?<body>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex SearchTitleBlock = new(@"<(?:div|p)\b[^>]*\bclass\s*=\s*(['""'])[^'""']*\bmt-auto\b[^'""']*\1[^>]*>(?<body>.*?)</(?:div|p)>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly HttpClient Client = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    public static async Task<MovieMetadata> ScrapeByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (!ValidCode.IsMatch(code)) throw new ArgumentException("JAVDatabase 番号格式无效。", nameof(code));

        var searchUrl = new Uri(Site, "?s=" + Uri.EscapeDataString(code));
        var searchHtml = await GetHtmlAsync(searchUrl, cancellationToken);
        EnsureNormalPage(searchHtml, "搜索");
        var detailUrl = FindExactResult(searchHtml, code);
        if (detailUrl is null)
        {
            if (Regex.IsMatch(searchHtml, @"No results found\.|No Results Found", RegexOptions.IgnoreCase) ||
                Anchors.Matches(searchHtml).Any(match => TryMovieUri(match.Groups["href"].Value, out _)))
                throw new ScrapeNotFoundException("JAVDatabase 没有完全匹配的番号。");
            throw new InvalidOperationException("JAVDatabase 搜索页没有可识别的结果，可能页面结构已变化或访问受到限制。");
        }

        var candidate = new MovieMetadata { Code = code, Title = code, Source = SourceName, SourceUrl = detailUrl.AbsoluteUri };
        candidate.StampAvailableFields("搜索结果");
        var detailHtml = await GetHtmlAsync(detailUrl, cancellationToken);
        EnsureNormalPage(detailHtml, "详情");
        var result = ParseDetail(candidate, detailHtml);
        if (!result.DetailFetched)
            throw new InvalidOperationException(result.DetailNote ?? "JAVDatabase 详情页缺少可识别字段。");
        return result;
    }

    // Based on the public JAVDatabase search structure documented by the
    // javdb-python project. Search results remain candidates until the user
    // chooses one and its code is verified against the detail page.
    public static async Task<List<MovieMetadata>> SearchTitleAsync(string query, CancellationToken cancellationToken = default)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) throw new ArgumentException("请填写 JAVDatabase 搜索词。", nameof(query));
        if (query.Length > 200) query = query[..200];
        var url = new Uri(Site, "?post_type=movies%2Cuncensored&s=" + Uri.EscapeDataString(query) + "&paged=1");
        var html = await GetHtmlAsync(url, cancellationToken);
        EnsureNormalPage(html, "搜索");
        return ParseTitleSearch(html);
    }

    internal static List<MovieMetadata> ParseTitleSearch(string html)
    {
        EnsureNormalPage(html, "搜索");
        var cardStarts = SearchCards.Matches(html);
        var found = new List<MovieMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match start in cardStarts.Cast<Match>().Take(30))
        {
            var next = start.NextMatch();
            var end = next.Success ? next.Index : html.Length;
            var card = html[start.Index..end];
            var codeBlock = SearchCode.Match(card);
            if (!codeBlock.Success) continue;
            var codeLink = Regex.Match(codeBlock.Groups["body"].Value, @"<a\b[^>]*\bhref\s*=\s*(['""'])(?<href>.*?)\1[^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!codeLink.Success || !TryMovieUri(codeLink.Groups["href"].Value, out var movieUri) || movieUri is null) continue;
            var code = CleanHtml(codeLink.Groups["text"].Value).ToUpperInvariant();
            if (!ValidCode.IsMatch(code) ||
                !string.Equals(movieUri.AbsolutePath["/movies/".Length..].TrimEnd('/'), code, StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(movieUri.AbsoluteUri)) continue;
            var titleBlock = SearchTitleBlock.Match(card);
            var titleLink = titleBlock.Success ? Links.Match(titleBlock.Groups["body"].Value) : Match.Empty;
            var title = titleLink.Success ? CleanHtml(titleLink.Groups["value"].Value) : code;
            if (title.Length == 0) title = code;
            var record = new MovieMetadata
            {
                Code = code, Title = title, Source = SourceName, SourceUrl = movieUri.AbsoluteUri,
                DetailNote = "搜索摘要；选定后读取并核对完整详情"
            };
            var cover = Regex.Match(card, @"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (cover.Success)
            {
                var attributes = Attribute.Matches(cover.Value).Cast<Match>()
                    .ToDictionary(match => match.Groups["name"].Value, match => WebUtility.HtmlDecode(match.Groups["value"].Value), StringComparer.OrdinalIgnoreCase);
                var rawCover = attributes.GetValueOrDefault("data-src") ?? attributes.GetValueOrDefault("src");
                if (rawCover is not null && Uri.TryCreate(movieUri, rawCover, out var coverUri) && coverUri.Scheme == Uri.UriSchemeHttps &&
                    IsAllowedPosterHost(coverUri.Host)) record.PosterUrl = coverUri.AbsoluteUri;
            }
            var date = Regex.Match(card, @"\b(?:19|20)\d{2}-\d{2}-\d{2}\b");
            if (date.Success) record.ReleaseDate = date.Value;
            var studio = Regex.Match(card, @"<(?:span|div)\b[^>]*\bclass\s*=\s*(['""'])[^'""']*\bbtn(?:-primary)?\b[^'""']*\1[^>]*>(?<value>.*?)</(?:span|div)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (studio.Success) record.Studio = CleanHtml(studio.Groups["value"].Value);
            record.StampAvailableFields("搜索结果");
            found.Add(record);
            if (found.Count == 12) break;
        }
        if (found.Count == 0 && !Regex.IsMatch(html, @"No results found\.|No Results Found|Nothing Found", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("JAVDatabase 搜索页没有可识别的候选，无法确认没有匹配结果。");
        return found;
    }

    public static async Task<MovieMetadata> FetchCandidateDetailsAsync(MovieMetadata candidate, CancellationToken cancellationToken = default)
    {
        if (candidate.Source != SourceName || !TryMovieUri(candidate.SourceUrl, out var uri) || uri is null)
            throw new ArgumentException("JAVDatabase 候选链接无效。", nameof(candidate));
        var html = await GetHtmlAsync(uri, cancellationToken);
        var detail = ParseDetail(candidate, html);
        if (!detail.DetailFetched) throw new InvalidOperationException(detail.DetailNote ?? "JAVDatabase 详情页缺少资料。");
        return detail;
    }

    static async Task<string> GetHtmlAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var retryHeader = response.Headers.RetryAfter;
            var retryAfter = retryHeader?.Delta ?? (retryHeader?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : null);
            throw new ScrapeHttpException($"JAVDatabase 返回 {(int)response.StatusCode}，请检查来源是否在当前地区可用。", response.StatusCode, retryAfter);
        }
        if (response.Content.Headers.ContentType?.MediaType is not "text/html")
            throw new InvalidOperationException("JAVDatabase 没有返回 HTML 页面。");
        if (response.Content.Headers.ContentLength is > MaximumHtmlLength)
            throw new InvalidOperationException("JAVDatabase 页面超出安全大小限制。");
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (html.Length > MaximumHtmlLength) throw new InvalidOperationException("JAVDatabase 页面超出安全大小限制。");
        return html;
    }

    static void EnsureNormalPage(string html, string stage)
    {
        if (html.Length < 500 ||
            Regex.IsMatch(html, @"(?:Access Denied|not available in your region|Age Verification|verify you are human|cf-browser-verification|Just a moment\.\.\.)", RegexOptions.IgnoreCase))
            throw new InvalidOperationException($"JAVDatabase {stage}页面不可用或需要验证；请在浏览器中确认来源可访问。");
    }

    static Uri? FindExactResult(string html, string code)
    {
        foreach (Match anchor in Anchors.Matches(html))
        {
            if (!TryMovieUri(anchor.Groups["href"].Value, out var movieUri)) continue;
            var slug = movieUri!.AbsolutePath["/movies/".Length..].TrimEnd('/');
            if (string.Equals(slug, code, StringComparison.OrdinalIgnoreCase)) return movieUri;
        }
        return null;
    }

    static bool TryMovieUri(string raw, out Uri? uri)
    {
        uri = null;
        raw = WebUtility.HtmlDecode(raw);
        if (!Uri.TryCreate(Site, raw, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps ||
            !IsSiteHost(parsed.Host) || !parsed.AbsolutePath.StartsWith("/movies/", StringComparison.OrdinalIgnoreCase) ||
            parsed.Query.Length > 0 || parsed.Fragment.Length > 0) return false;
        var slug = parsed.AbsolutePath["/movies/".Length..].TrimEnd('/');
        if (slug.Length == 0 || slug.Contains('/')) return false;
        uri = parsed;
        return true;
    }

    static bool IsSiteHost(string host) => host.Equals(SiteHost, StringComparison.OrdinalIgnoreCase) ||
        host.Equals("javdatabase.com", StringComparison.OrdinalIgnoreCase);

    internal static MovieMetadata ParseDetail(MovieMetadata candidate, string html)
    {
        if (candidate.Source != SourceName || !TryMovieUri(candidate.SourceUrl, out var sourceUri) || sourceUri is null)
            throw new ArgumentException("JAVDatabase 详情链接无效。", nameof(candidate));
        var expectedCode = candidate.Code.Trim().ToUpperInvariant();
        if (!ValidCode.IsMatch(expectedCode) ||
            !string.Equals(sourceUri.AbsolutePath["/movies/".Length..].TrimEnd('/'), expectedCode, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("JAVDatabase 详情链接与番号不一致。", nameof(candidate));
        EnsureNormalPage(html, "详情");

        var heading = Heading.Match(html);
        if (!heading.Success)
            throw new InvalidOperationException("JAVDatabase 详情页没有作品标题，可能页面结构已变化。");
        var headingText = CleanHtml(heading.Groups["value"].Value);
        if (!Regex.IsMatch(headingText, @"^" + Regex.Escape(expectedCode) + @"(?:\s|$)", RegexOptions.IgnoreCase))
            throw new ScrapeNotFoundException("JAVDatabase 详情页标题的番号与请求不一致。");

        var rows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match row in DetailRows.Matches(html))
        {
            var label = CleanHtml(row.Groups["label"].Value).Trim().TrimEnd(':', '：');
            if (!rows.ContainsKey(label)) rows[label] = row.Groups["value"].Value;
        }
        foreach (Match paragraph in Paragraphs.Matches(html))
            foreach (Match field in FieldInParagraph.Matches(paragraph.Groups["body"].Value))
            {
                var label = CleanHtml(field.Groups["label"].Value).Trim().TrimEnd(':', '：');
                if (label.Length > 0 && !rows.ContainsKey(label)) rows[label] = field.Groups["value"].Value;
            }
        if (!rows.TryGetValue("DVD ID", out var rawId))
            throw new InvalidOperationException("JAVDatabase 详情页缺少 DVD ID，无法验证番号。");
        if (!string.Equals(CleanHtml(rawId), expectedCode, StringComparison.OrdinalIgnoreCase))
            throw new ScrapeNotFoundException("JAVDatabase 详情页的 DVD ID 与请求番号不一致。");

        var result = candidate.Clone();
        var title = Regex.Replace(headingText, @"^" + Regex.Escape(expectedCode) + @"\s*[-–—:]?\s*", "", RegexOptions.IgnoreCase).Trim();
        if (title.Length > 0) result.Title = title;
        var recognized = title.Length > 0 ? 1 : 0;
        if ((TryRow(rows, "Release Date", out var date) || TryRow(rows, "Released", out date)) &&
            DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        { result.ReleaseDate = date; recognized++; }
        if (TryRow(rows, "Runtime", out var runtime))
        {
            var minutes = Regex.Match(runtime, @"\b(?<value>\d{1,4})\s*min", RegexOptions.IgnoreCase);
            var hours = Regex.Match(runtime, @"\b(?<value>\d{1,2})\s*hours?\b", RegexOptions.IgnoreCase);
            var value = (hours.Success && int.TryParse(hours.Groups["value"].Value, out var h) ? h * 60 : 0) +
                (minutes.Success && int.TryParse(minutes.Groups["value"].Value, out var m) ? m : 0);
            if (value is > 0 and <= 3000) { result.RuntimeMinutes = value; recognized++; }
        }
        if (TryRow(rows, "Studio", out var studio)) { result.Studio = studio; recognized++; }
        if (TryRow(rows, "Director", out var director)) { result.Director = director; recognized++; }
        if (TryRow(rows, "JAV Series", out var series) || TryRow(rows, "Series", out series)) { result.Series = series; recognized++; }
        if (rows.TryGetValue("Genre(s)", out var rawGenres))
        { result.Genres = LinkNames(rawGenres); if (result.Genres.Count > 0) recognized++; }
        if (rows.TryGetValue("Idol(s)/Actress(es)", out var rawActors))
        { result.Actors = LinkNames(rawActors); if (result.Actors.Count > 0) recognized++; }

        var ratingBlock = Regex.Match(html, @"<div\b[^>]*\bclass\s*=\s*(['""'])[^'""']*\bwp-postratings\b[^'""']*\1[^>]*>(?<body>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (ratingBlock.Success)
        {
            var rating = Regex.Match(CleanHtml(ratingBlock.Groups["body"].Value),
                @"(?<value>\d+(?:\.\d+)?)\s*/\s*(?<maximum>\d+(?:\.\d+)?)\s+from\s+(?<votes>[\d,]+)\s+votes?", RegexOptions.IgnoreCase);
            if (rating.Success && double.TryParse(rating.Groups["value"].Value, CultureInfo.InvariantCulture, out var value) &&
                double.TryParse(rating.Groups["maximum"].Value, CultureInfo.InvariantCulture, out var maximum) &&
                int.TryParse(rating.Groups["votes"].Value.Replace(",", ""), out var votes) &&
                maximum > 0 && value >= 0 && value <= maximum && votes > 0)
            {
                result.Rating = $"{value.ToString("0.##", CultureInfo.InvariantCulture)}/{maximum.ToString("0.##", CultureInfo.InvariantCulture)}（{votes} 票）";
                recognized++;
            }
        }

        var about = Regex.Matches(html, @"<h[2-4]\b[^>]*>(?<heading>.*?)</h[2-4]>(?<body>.*?)(?=<h[2-4]\b|</body>|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Cast<Match>().FirstOrDefault(section => Regex.IsMatch(CleanHtml(section.Groups["heading"].Value), @"\bAbout\b.*\bJAV Movie\b", RegexOptions.IgnoreCase));
        if (about is not null)
        {
            var paragraphs = Paragraphs.Matches(about.Groups["body"].Value).Cast<Match>()
                .Select(match => CleanHtml(match.Groups["body"].Value))
                .Where(value => value.Length > 0).Take(8).ToArray();
            if (paragraphs.Length > 0) { result.Summary = string.Join("\n\n", paragraphs); recognized++; }
        }

        foreach (Match meta in MetaTags.Matches(html))
        {
            var attributes = Attribute.Matches(meta.Value).Cast<Match>()
                .ToDictionary(match => match.Groups["name"].Value, match => WebUtility.HtmlDecode(match.Groups["value"].Value), StringComparer.OrdinalIgnoreCase);
            if (!attributes.TryGetValue("property", out var property) || !property.Equals("og:image", StringComparison.OrdinalIgnoreCase) ||
                !attributes.TryGetValue("content", out var poster) || !Uri.TryCreate(poster, UriKind.Absolute, out var imageUri) ||
                imageUri.Scheme != Uri.UriSchemeHttps || !IsAllowedPosterHost(imageUri.Host)) continue;
            result.PosterUrl = imageUri.AbsoluteUri;
            recognized++;
            break;
        }
        if (result.PosterUrl is null)
        {
            var posterContainer = Regex.Match(html, @"<div\b[^>]*\bid\s*=\s*(['""'])poster-container\1[^>]*>(?<body>.*?</div>)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var image = posterContainer.Success ? Regex.Match(posterContainer.Groups["body"].Value, @"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline) : Match.Empty;
            if (image.Success)
            {
                var attributes = Attribute.Matches(image.Value).Cast<Match>()
                    .ToDictionary(match => match.Groups["name"].Value, match => WebUtility.HtmlDecode(match.Groups["value"].Value), StringComparer.OrdinalIgnoreCase);
                var rawPoster = attributes.GetValueOrDefault("data-src") ?? attributes.GetValueOrDefault("src");
                if (rawPoster is not null && Uri.TryCreate(sourceUri, rawPoster, out var imageUri) && imageUri.Scheme == Uri.UriSchemeHttps &&
                    IsAllowedPosterHost(imageUri.Host)) { result.PosterUrl = imageUri.AbsoluteUri; recognized++; }
            }
        }
        result.RefreshChangedFieldProvenance(candidate, "详情页");
        result.DetailFetched = recognized > 0;
        result.DetailIdentityVerified = result.DetailFetched;
        result.DetailNote = result.DetailFetched
            ? $"已读取 JAVDatabase 公开详情（{recognized} 项）；缺失字段保持空白"
            : "JAVDatabase 详情页没有可识别的资料字段";
        var platformRating = RatingAggregation.FromMetadata(result, "番号一致");
        if (platformRating is not null) RatingAggregation.AddOrUpdate(result, platformRating);
        return result;
    }

    static bool IsAllowedPosterHost(string host) => IsSiteHost(host) ||
        host.Equals("pics.dmm.co.jp", StringComparison.OrdinalIgnoreCase);

    static bool TryRow(Dictionary<string, string> rows, string name, out string text)
    {
        text = rows.TryGetValue(name, out var raw) ? CleanHtml(raw) : "";
        return text.Length > 0;
    }

    static List<string> LinkNames(string html) => Links.Matches(html).Cast<Match>()
        .Select(match => CleanHtml(match.Groups["value"].Value))
        .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static string CleanHtml(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();
}

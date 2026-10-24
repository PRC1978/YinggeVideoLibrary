using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

public sealed class MovieMetadata
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "JavDB";
    public string SourceUrl { get; set; } = "";
    public string? Poster { get; set; }
    public string? PosterUrl { get; set; }
    public DateTime ScrapedAt { get; set; } = DateTime.Now;
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
    public int Confidence { get; set; }
    public string Status { get; set; } = "pending";
    // ai/rules describe the automatic verdict; manual marks a later human decision.
    public string DecisionSource { get; set; } = "ai";
    public string Reason { get; set; } = "";
    public string Model { get; set; } = "local-rules";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? ReviewedAt { get; set; }

    public MovieMetadata ToMetadata() => new()
    {
        Code = CandidateCode,
        Title = CandidateTitle,
        Source = CandidateSource,
        SourceUrl = CandidateSourceUrl,
        Poster = CandidatePoster,
        PosterUrl = CandidatePosterUrl,
        ScrapedAt = CreatedAt
    };
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

public static class JavDbScraper
{
    static readonly Regex Cards = new("<a\\s+href=\"(?<href>/v/[^\"]+)\"\\s+class=\"box\"[^>]*>(?<body>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Codes = new("<strong>\\s*(?<code>[^<]+)\\s*</strong>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Title = new("\\btitle=\"(?<value>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Image = new("<img[^>]+(?:src|data-src)=\"(?<value>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FileCode = new(@"(?<![A-Za-z0-9])([A-Za-z]{2,10})[-_ ]?(\d{2,5})(?![A-Za-z0-9])", RegexOptions.Compiled);
    static readonly HttpClient Client = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli };
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

    public static async Task<MovieMetadata> ScrapeAsync(string code, string videoId, string dataDir, CancellationToken cancellationToken = default)
    {
        code = code.Trim().ToUpperInvariant();
        if (code.Length is < 3 or > 35 || !Regex.IsMatch(code, @"^[A-Z0-9-]+$")) throw new InvalidOperationException("番号格式无效。请使用字母、数字和连字符。");
        var searchUrl = "https://javdb.com/search?q=" + Uri.EscapeDataString(code) + "&f=all";
        using var response = await Client.GetAsync(searchUrl, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var retryHeader = response.Headers.RetryAfter;
            var retryAfter = retryHeader?.Delta ?? (retryHeader?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : null);
            throw new ScrapeHttpException($"JavDB 返回 {(int)response.StatusCode}，请稍后重试。", response.StatusCode, retryAfter);
        }
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
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
        var detail = new Uri(new Uri("https://javdb.com"), matched.Groups["href"].Value).ToString();
        var posterUrl = WebUtility.HtmlDecode(Image.Match(matched.Groups["body"].Value).Groups["value"].Value);
        string? poster = null;
        if (Uri.TryCreate(posterUrl, UriKind.Absolute, out var imageUri) && imageUri.Scheme == Uri.UriSchemeHttps && (imageUri.Host.Equals("jdbstatic.com", StringComparison.OrdinalIgnoreCase) || imageUri.Host.EndsWith(".jdbstatic.com", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var imageResponse = await Client.GetAsync(imageUri, HttpCompletionOption.ResponseContentRead, cancellationToken);
                imageResponse.EnsureSuccessStatusCode();
                var bytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length is > 0 and < 8_000_000 && imageResponse.Content.Headers.ContentType?.MediaType?.StartsWith("image/") == true)
                {
                    var folder = Path.Combine(dataDir, "posters"); Directory.CreateDirectory(folder);
                    poster = Path.Combine(folder, videoId + ".jpg");
                    await File.WriteAllBytesAsync(poster, bytes, cancellationToken);
                }
            }
            catch { /* Keep the title when the image host is unavailable. */ }
        }
        return new MovieMetadata { Code = code, Title = title, SourceUrl = detail, Poster = poster };
    }
}

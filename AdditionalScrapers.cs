using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

public static class AdditionalScrapers
{
    static readonly HttpClient Client = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(18) };

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
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("TMDB 凭据无效，请到设置中检查。");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var entry in document.RootElement.GetProperty("results").EnumerateArray().Take(8))
            {
                var titleName = type == "movie" ? "title" : "name";
                var title = entry.TryGetProperty(titleName, out var titleValue) ? titleValue.GetString() ?? "" : "";
                if (title.Length == 0) continue;
                var id = entry.GetProperty("id").GetInt32();
                var dateName = type == "movie" ? "release_date" : "first_air_date";
                var year = entry.TryGetProperty(dateName, out var date) ? (date.GetString() ?? "") : "";
                if (year.Length >= 4) year = year[..4];
                var posterPath = entry.TryGetProperty("poster_path", out var poster) && poster.ValueKind == JsonValueKind.String ? poster.GetString() : null;
                results.Add(new MovieMetadata { Title = year.Length == 4 ? $"{title} ({year})" : title, Code = "", Source = type == "movie" ? "TMDB 电影" : "TMDB 剧集", SourceUrl = $"https://www.themoviedb.org/{type}/{id}", PosterUrl = posterPath is null ? null : "https://image.tmdb.org/t/p/w500" + posterPath });
            }
        }
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
        if (!document.RootElement.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array) return [];
        var results = new List<MovieMetadata>();
        foreach (var item in videos.EnumerateArray().Take(15))
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var sourceUrl = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var posterUrl = item.TryGetProperty("default_thumb", out var p) ? p.GetString() : null;
            if (title.Length == 0 || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) || source.Host != "www.pornhub.com" || source.Scheme != "https") continue;
            results.Add(new MovieMetadata { Title = title, Source = "Pornhub", SourceUrl = sourceUrl, PosterUrl = posterUrl });
        }
        return results;
    }

    public static async Task<List<MovieMetadata>> SearchTvMazeAsync(string query)
    {
        using var response = await Client.GetAsync("https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(query));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var results = new List<MovieMetadata>();
        foreach (var entry in document.RootElement.EnumerateArray().Take(10))
        {
            if (!entry.TryGetProperty("show", out var show)) continue;
            var title = show.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
            var id = show.TryGetProperty("id", out var idValue) ? idValue.GetInt32() : 0;
            var image = show.TryGetProperty("image", out var imageNode) && imageNode.ValueKind == JsonValueKind.Object && imageNode.TryGetProperty("original", out var imageUrl) ? imageUrl.GetString() : null;
            if (title.Length == 0 || id == 0) continue;
            results.Add(new MovieMetadata { Title = title, Source = "TVmaze", SourceUrl = $"https://www.tvmaze.com/shows/{id}", PosterUrl = image });
        }
        return results;
    }

    public static async Task DownloadPosterAsync(MovieMetadata record, string videoId, string dataDir)
    {
        if (!Uri.TryCreate(record.PosterUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https") return;
        var allowed = record.Source.StartsWith("TMDB") ? uri.Host.Equals("image.tmdb.org", StringComparison.OrdinalIgnoreCase) : uri.Host.EndsWith(".phncdn.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("phncdn.com", StringComparison.OrdinalIgnoreCase);
        if (!allowed) return;
        try
        {
            using var response = await Client.GetAsync(uri);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length is < 1 or > 8_000_000 || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/") != true) return;
            var folder = Path.Combine(dataDir, "posters"); Directory.CreateDirectory(folder);
            var destination = Path.Combine(folder, videoId + ".jpg");
            await File.WriteAllBytesAsync(destination, bytes);
            record.Poster = destination;
        }
        catch { /* The title remains useful if cover retrieval fails. */ }
    }
}

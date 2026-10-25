using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// This adapter reads an already verified person identity, either on demand
// or from a user-confirmed bulk import. It never searches by a name. Requests
// are built from the numeric identity independently verified on a work.
internal static class RolePersonDetailsFetcher
{
    const int MaxJsonBytes = 512 * 1024;
    const int MaxPortraitBytes = 1024 * 1024;
    static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(18)
    };

    internal sealed class RateLimitException : HttpRequestException
    {
        internal RateLimitException(string provider, TimeSpan retryAfter)
            : base(provider + " 人物来源请求过于频繁。", null, HttpStatusCode.TooManyRequests)
        {
            RetryAfter = retryAfter;
        }

        internal TimeSpan RetryAfter { get; }
    }

    internal static TimeSpan RetryAfterDelay(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header?.Delta is { } delta) return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        if (header?.Date is { } date)
        {
            var remaining = date - now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return TimeSpan.FromSeconds(5);
    }

    static RolePersonDetailsFetcher()
    {
        var version = typeof(RolePersonDetailsFetcher).Assembly.GetName().Version?.ToString(3) ?? "1.5.2";
        Client.DefaultRequestHeaders.UserAgent.ParseAdd($"YinggeVideoLibrary/{version} (local profile viewer)");
    }

    internal static async Task<RoleProfileStore.SourceDetails> FetchAsync(
        RoleProfileStore.SourceIdentity source, string tmdbToken, CancellationToken token = default,
        bool downloadPortrait = true)
    {
        if (!int.TryParse(source.PersonId, out var personId) || personId <= 0)
            throw new InvalidDataException("人物来源 ID 无效。");
        string url;
        if (source.Provider == "TMDB")
        {
            if (source.PersonUrl != $"https://www.themoviedb.org/person/{personId}")
                throw new InvalidDataException("TMDB 人物链接与来源 ID 不一致。");
            if (string.IsNullOrWhiteSpace(tmdbToken))
                throw new InvalidOperationException("请先在搜刮设置中保存 TMDB 凭据。");
            url = $"https://api.themoviedb.org/3/person/{personId}?language=zh-CN";
        }
        else if (source.Provider == "TVmaze")
        {
            if (source.PersonUrl != $"https://www.tvmaze.com/people/{personId}")
                throw new InvalidDataException("TVmaze 人物链接与来源 ID 不一致。");
            url = $"https://api.tvmaze.com/people/{personId}";
        }
        else throw new InvalidOperationException("此来源暂未提供可读取的人物详情；可手工填写档案。");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (source.Provider == "TMDB")
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tmdbToken);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitException(source.Provider,
                RetryAfterDelay(response.Headers.RetryAfter, DateTimeOffset.UtcNow));
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{source.Provider} 人物详情返回 {(int)response.StatusCode}。",
                null, response.StatusCode);
        var bytes = await ReadBoundedAsync(response.Content, MaxJsonBytes, token);
        using var document = JsonDocument.Parse(bytes);
        var details = source.Provider == "TMDB"
            ? ParseTmdb(source, document.RootElement)
            : ParseTvmaze(source, document.RootElement);
        if (downloadPortrait && details.PortraitUrl.Length > 0)
        {
            try
            {
                var (mime, image) = await DownloadPortraitAsync(source.Provider, details.PortraitUrl, token);
                details.PortraitMime = mime;
                details.PortraitBase64 = Convert.ToBase64String(image);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException)
            {
                // The official facts remain useful if its image CDN fails.
                // The URL stays visible for a later explicit open/retry.
            }
        }
        return details;
    }

    internal static RoleProfileStore.SourceDetails ParseTmdb(RoleProfileStore.SourceIdentity source, JsonElement root)
    {
        var personId = RequireId(root, source, "TMDB");
        if (source.PersonUrl != $"https://www.themoviedb.org/person/{personId}")
            throw new InvalidDataException("TMDB 人物 URL 与响应 ID 不一致。");
        var portraitPath = Text(root, "profile_path");
        var portraitUrl = portraitPath is not null && Regex.IsMatch(portraitPath,
            @"^/[a-zA-Z0-9_-]{4,120}\.(?:jpg|jpeg|png)$", RegexOptions.IgnoreCase)
            ? "https://image.tmdb.org/t/p/w342" + portraitPath : "";
        var gender = root.TryGetProperty("gender", out var node) && node.ValueKind == JsonValueKind.Number
            && node.TryGetInt32(out var code) ? code switch
            { 1 => "女", 2 => "男", 3 => "非二元", _ => "" } : "";
        return new RoleProfileStore.SourceDetails
        {
            Provider = "TMDB", PersonId = source.PersonId, SourceUrl = source.PersonUrl,
            Name = Limit(Text(root, "name"), 180), Biography = Limit(Text(root, "biography"), 20000),
            Birthday = Limit(Text(root, "birthday"), 80), Deathday = Limit(Text(root, "deathday"), 80),
            Gender = gender, PlaceOfBirth = Limit(Text(root, "place_of_birth"), 180),
            PortraitUrl = portraitUrl
        };
    }

    internal static RoleProfileStore.SourceDetails ParseTvmaze(RoleProfileStore.SourceIdentity source, JsonElement root)
    {
        var personId = RequireId(root, source, "TVmaze");
        if (source.PersonUrl != $"https://www.tvmaze.com/people/{personId}")
            throw new InvalidDataException("TVmaze 人物 URL 与响应 ID 不一致。");
        var imageUrl = "";
        if (root.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object)
        {
            var candidate = Text(image, "medium") ?? Text(image, "original");
            if (candidate is not null && TrustedPortraitUrl("TVmaze", candidate)) imageUrl = candidate;
        }
        var country = root.TryGetProperty("country", out var countryNode) &&
            countryNode.ValueKind == JsonValueKind.Object ? Text(countryNode, "name") : null;
        return new RoleProfileStore.SourceDetails
        {
            Provider = "TVmaze", PersonId = source.PersonId, SourceUrl = source.PersonUrl,
            Name = Limit(Text(root, "name"), 180), Birthday = Limit(Text(root, "birthday"), 80),
            Deathday = Limit(Text(root, "deathday"), 80), Gender = Limit(Text(root, "gender"), 80),
            Country = Limit(country, 180), PortraitUrl = imageUrl
        };
    }

    static int RequireId(JsonElement root, RoleProfileStore.SourceIdentity source, string provider)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var node)
            || node.ValueKind != JsonValueKind.Number || !node.TryGetInt32(out var id)
            || id <= 0 || source.Provider != provider || source.PersonId != id.ToString())
            throw new InvalidDataException(provider + " 人物详情 ID 与所选来源不一致，已放弃该响应。 ");
        return id;
    }

    static string? Text(JsonElement root, string key) => root.TryGetProperty(key, out var node)
        && node.ValueKind == JsonValueKind.String ? node.GetString()?.Trim() : null;
    static string Limit(string? value, int maxLength) => value is null ? ""
        : value.Length <= maxLength ? value : value[..maxLength];

    internal static bool TrustedPortraitUrl(string provider, string url)
    {
        if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        return provider switch
        {
            "TMDB" => uri.Host == "image.tmdb.org" &&
                Regex.IsMatch(uri.AbsolutePath, @"^/t/p/w342/[a-zA-Z0-9_-]{4,120}\.(?:jpg|jpeg|png)$",
                    RegexOptions.IgnoreCase),
            "TVmaze" => uri.Host == "static.tvmaze.com" &&
                Regex.IsMatch(uri.AbsolutePath,
                    @"^/uploads/images/(?:medium_portrait|original_untouched)/[0-9]+/[a-zA-Z0-9_-]+\.(?:jpg|jpeg|png)$",
                    RegexOptions.IgnoreCase),
            _ => false
        };
    }

    static async Task<(string Mime, byte[] Bytes)> DownloadPortraitAsync(string provider,
        string url, CancellationToken token)
    {
        if (!TrustedPortraitUrl(provider, url)) throw new InvalidDataException("来源肖像地址不受信任。");
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("来源肖像读取失败。");
        var mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        if (mime is not ("image/jpeg" or "image/png"))
            throw new InvalidDataException("来源肖像不是 JPEG 或 PNG 图片。");
        var image = await ReadBoundedAsync(response.Content, MaxPortraitBytes, token);
        if (mime == "image/jpeg" && (image.Length < 3 || image[0] != 0xff || image[1] != 0xd8 || image[2] != 0xff)
            || mime == "image/png" && (image.Length < 8 ||
                !image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })))
            throw new InvalidDataException("来源肖像内容与图片格式不一致。");
        return (mime, image);
    }

    static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken token)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("来源响应超过允许的大小。");
        await using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[32768];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token);
            if (count == 0) break;
            if (output.Length + count > maxBytes) throw new InvalidDataException("来源响应超过允许的大小。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}

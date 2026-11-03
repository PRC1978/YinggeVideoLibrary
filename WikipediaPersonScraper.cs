using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// Wikipedia is a public biography reference, not proof that a named local
// performer is the same person or that any work belongs to that biography.
internal static class WikipediaPersonScraper
{
    internal sealed record Candidate(long PageId, string Key, string Title, string Description)
    {
        public string Display => Title + (Description.Length == 0 ? "" : " · " + Description);
    }

    static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(15) };
    static WikipediaPersonScraper() => Client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "YinggeVideoLibrary/1.6.6 (https://github.com/PRC1978/YinggeVideoLibrary)");

    internal static string PageUrl(string title) => "https://zh.wikipedia.org/wiki/" +
        Uri.EscapeDataString(title.Replace(' ', '_'));

    internal static bool IsCanonicalPageUrl(string? url, string title) =>
        !string.IsNullOrWhiteSpace(title) && title.Length <= 180 &&
        string.Equals(url, PageUrl(title), StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<Candidate> ParseSearch(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("维基百科搜索响应缺少候选列表。");
        var results = new List<Candidate>();
        foreach (var item in pages.EnumerateArray().Take(8))
        {
            if (!item.TryGetProperty("id", out var id) || !id.TryGetInt64(out var pageId) || pageId <= 0 ||
                !item.TryGetProperty("key", out var keyNode) ||
                !item.TryGetProperty("title", out var titleNode)) continue;
            var key = keyNode.GetString() ?? "";
            var title = titleNode.GetString() ?? "";
            if (key.Length is < 1 or > 180 || title.Length is < 1 or > 180) continue;
            var description = item.TryGetProperty("description", out var descriptionNode) &&
                descriptionNode.ValueKind == JsonValueKind.String ? descriptionNode.GetString() ?? "" : "";
            results.Add(new Candidate(pageId, key, title, description.Length > 300 ? description[..300] : description));
        }
        return results.DistinctBy(item => item.PageId).Take(5).ToList();
    }

    internal static RoleProfileStore.WikipediaReference ParseSummary(Candidate selected, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("pageid", out var id) || !id.TryGetInt64(out var pageId) ||
            pageId != selected.PageId || !root.TryGetProperty("title", out var titleNode))
            throw new InvalidDataException("维基百科摘要与所选条目不一致。");
        var title = titleNode.GetString() ?? "";
        if (title.Length is < 1 or > 180 ||
            !title.Equals(selected.Title, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("维基百科条目标题在读取期间发生变化，请重新搜索。");
        var extract = root.TryGetProperty("extract", out var extractNode) &&
            extractNode.ValueKind == JsonValueKind.String ? extractNode.GetString() ?? "" : "";
        var description = root.TryGetProperty("description", out var descriptionNode) &&
            descriptionNode.ValueKind == JsonValueKind.String ? descriptionNode.GetString() ?? "" : selected.Description;
        return new RoleProfileStore.WikipediaReference
        {
            PageId = pageId, Title = title, Url = PageUrl(title),
            Description = description.Length > 300 ? description[..300] : description,
            Extract = extract.Length > 6000 ? extract[..6000] : extract,
            FetchedUtc = DateTimeOffset.UtcNow
        };
    }

    internal static async Task<IReadOnlyList<Candidate>> SearchAsync(string name, CancellationToken token = default)
    {
        name = name.Trim();
        if (name.Length is < 2 or > 80 || name.Any(c => char.IsControl(c) || c is '/' or '\\' or '<' or '>'))
            throw new ArgumentException("人物姓名需为 2–80 字，不能包含路径或网址。", nameof(name));
        var url = "https://zh.wikipedia.org/w/rest.php/v1/search/page?q=" +
            Uri.EscapeDataString(name) + "&limit=5";
        return ParseSearch(await ReadJsonAsync(url, token));
    }

    internal static async Task<RoleProfileStore.WikipediaReference> FetchAsync(Candidate selected,
        CancellationToken token = default)
    {
        if (selected.PageId <= 0 || selected.Key.Length is < 1 or > 180 ||
            selected.Key.Contains('/') || selected.Key.Contains('\\'))
            throw new InvalidDataException("维基百科候选条目无效。");
        var url = "https://zh.wikipedia.org/api/rest_v1/page/summary/" +
            Uri.EscapeDataString(selected.Key);
        return ParseSummary(selected, await ReadJsonAsync(url, token));
    }

    static async Task<string> ReadJsonAsync(string url, CancellationToken token)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("维基百科请求过于频繁，请稍后重试。");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 256 * 1024)
            throw new InvalidDataException("维基百科响应过大。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, token);
            if (read == 0) break;
            if (output.Length + read > 256 * 1024) throw new InvalidDataException("维基百科响应过大。");
            output.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    internal static IReadOnlyDictionary<string, bool> Verify()
    {
        const string search = """{"pages":[{"id":8731741,"key":"森日向子","title":"森日向子","description":"日本演员"},{"id":2,"key":"同名人物","title":"同名人物","description":"其他人物"}]}""";
        var candidates = ParseSearch(search);
        const string summary = """{"pageid":8731741,"title":"森日向子","description":"日本演员","extract":"森日向子的公开简介。"}""";
        var reference = ParseSummary(candidates[0], summary);
        var results = new Dictionary<string, bool>
        {
            ["wikipediaKeepsSameNameCandidatesSeparate"] = candidates.Count == 2 && candidates[0].PageId != candidates[1].PageId,
            ["wikipediaSummaryHasCanonicalAttribution"] = reference.PageId == 8731741 &&
                reference.Url == "https://zh.wikipedia.org/wiki/%E6%A3%AE%E6%97%A5%E5%90%91%E5%AD%90" &&
                reference.Extract.Contains("公开简介", StringComparison.Ordinal)
        };
        try { _ = ParseSummary(candidates[1], summary); results["wikipediaRejectsWrongPageId"] = false; }
        catch (InvalidDataException) { results["wikipediaRejectsWrongPageId"] = true; }
        return results;
    }
}

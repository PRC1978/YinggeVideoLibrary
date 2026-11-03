using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class LiveSourceSmokeDiagnostics
{
    // Opt-in, two public title searches. Never reads the library or calls a model.
    internal static async Task<int> RunToFileAsync(string path)
    {
        var results = new Dictionary<string, object>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        foreach (var (name, search) in new List<(string Name, Func<Task<List<MovieMetadata>>> Search)>
        {
            ("Bangumi", () => BangumiScraper.SearchAsync("Cowboy Bebop", timeout.Token)),
            ("AniList", () => AniListScraper.SearchAsync("Cowboy Bebop", timeout.Token))
        })
        {
            try
            {
                var candidates = await search();
                var matching = candidates.FirstOrDefault(candidate =>
                    (candidate.Title + " " + candidate.OriginalTitle).Contains("Cowboy Bebop", StringComparison.OrdinalIgnoreCase));
                var detail = matching is null ? null : name == "Bangumi"
                    ? await BangumiScraper.FetchFullDetailsAsync(matching, timeout.Token)
                    : await AniListScraper.FetchFullDetailsAsync(matching, timeout.Token);
                results[name] = new { passed = detail is { DetailFetched: true, DetailIdentityVerified: true }
                        && detail.SourceUrl == matching?.SourceUrl,
                    count = candidates.Count, sourceUrl = matching?.SourceUrl, title = matching?.Title,
                    detailTitle = detail?.Title, detailIdentityVerified = detail?.DetailIdentityVerified };
            }
            catch (Exception ex)
            {
                results[name] = new { passed = false, error = ex.GetType().Name + ": " + ex.Message };
            }
        }
        var passed = results.Values.All(value =>
            JsonSerializer.SerializeToElement(value).GetProperty("passed").GetBoolean());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { passed, results },
            new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}

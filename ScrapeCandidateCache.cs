using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace YinggeVideoLibrary;

internal static class ScrapeCandidateCache
{
    public sealed class Entry
    {
        public DateTime ExpiresUtc { get; set; }
        public List<MovieMetadata> Candidates { get; set; } = [];
    }

    static string PathFor(string dataDir, string source, string title)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + title.Trim().ToUpperInvariant())));
        return System.IO.Path.Combine(dataDir, "candidate-cache", key + ".json");
    }

    public static async Task<IReadOnlyList<MovieMetadata>> GetOrFetchAsync(string dataDir, string source, string title, Func<Task<List<MovieMetadata>>> fetch)
    {
        var path = PathFor(dataDir, source, title);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path)) is { } cached && cached.ExpiresUtc > DateTime.UtcNow)
                return cached.Candidates;
        }
        catch { /* A corrupt cache is a miss. */ }
        var results = await fetch();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var temporary = path + "." + Environment.ProcessId + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Entry { ExpiresUtc = DateTime.UtcNow.AddDays(3), Candidates = results }));
            File.Move(temporary, path, true);
        }
        catch { /* Search results remain usable without disk caching. */ }
        return results;
    }
}

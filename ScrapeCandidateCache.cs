using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace YinggeVideoLibrary;

internal static class ScrapeCandidateCache
{
    const int CurrentSchemaVersion = 2;
    const string DefaultParserVersion = "1";

    public sealed class Entry
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = "";
        public string Query { get; set; } = "";
        public string ParserVersion { get; set; } = "";
        public DateTime FetchedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public List<MovieMetadata> Candidates { get; set; } = [];
    }

    static string PathFor(string dataDir, string source, string title)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + title.Trim().ToUpperInvariant())));
        return System.IO.Path.Combine(dataDir, "candidate-cache", key + ".json");
    }

    public static async Task<IReadOnlyList<MovieMetadata>> GetOrFetchAsync(string dataDir, string source, string title,
        Func<Task<List<MovieMetadata>>> fetch, string parserVersion = DefaultParserVersion)
    {
        var path = PathFor(dataDir, source, title);
        var query = title.Trim();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path)) is { } cached &&
                cached.ExpiresUtc > DateTime.UtcNow && cached.Candidates is not null)
            {
                if (cached.SchemaVersion == CurrentSchemaVersion && cached.Source == source &&
                    cached.Query.Equals(query, StringComparison.OrdinalIgnoreCase) && cached.ParserVersion == parserVersion)
                    return cached.Candidates;
                // Keep still-valid search cards from 1.5. Cached details lack
                // the new independent identity check and must be fetched again.
                if (cached.SchemaVersion == 0 && parserVersion == DefaultParserVersion &&
                    cached.Candidates.All(candidate => !candidate.DetailFetched))
                    return cached.Candidates;
            }
        }
        catch { /* A corrupt cache is a miss. */ }
        var results = await fetch();
        var fetchedAt = DateTime.UtcNow;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Entry
            {
                SchemaVersion = CurrentSchemaVersion,
                Source = source,
                Query = query,
                ParserVersion = parserVersion,
                FetchedUtc = fetchedAt,
                ExpiresUtc = fetchedAt.Add(results.Count == 0 ? TimeSpan.FromHours(6) : TimeSpan.FromDays(3)),
                Candidates = results
            }));
            File.Move(temporary, path, true);
        }
        catch { /* Search results remain usable without disk caching. */ }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch { } }
        return results;
    }

    internal static async Task<bool> VerifyLegacyIdentityRefreshAsync(string dataDir)
    {
        var source = "legacy-details-test";
        var query = "ABC-123";
        var path = PathFor(dataDir, source, query);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Entry
        {
            ExpiresUtc = DateTime.UtcNow.AddDays(1),
            Candidates = [new MovieMetadata { Code = query, DetailFetched = true }]
        }));
        var requests = 0;
        var refreshed = await GetOrFetchAsync(dataDir, source, query, () =>
        {
            requests++;
            return Task.FromResult(new List<MovieMetadata>
            {
                new() { Code = query, DetailFetched = true, DetailIdentityVerified = true }
            });
        });
        var stored = JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path));
        var kept = await GetOrFetchAsync(dataDir, source, query, () =>
        {
            requests++;
            return Task.FromResult(new List<MovieMetadata>());
        });
        return requests == 1 && refreshed.Count == 1 && refreshed[0].DetailIdentityVerified &&
            kept.Count == 1 && stored?.SchemaVersion == CurrentSchemaVersion &&
            stored.Source == source && stored.Query == query;
    }
}

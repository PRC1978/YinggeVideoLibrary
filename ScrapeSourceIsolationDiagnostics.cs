using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class ScrapeSourceIsolationDiagnostics
{
    internal static async Task<IReadOnlyDictionary<string, bool>> VerifyAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var paid = new MovieMetadata
        {
            Code = "ABC-123", Title = "作品", Source = "Javinfo · fanza",
            SourceUrl = "https://api.javinfo.dev/movie?q=ABC-123"
        };
        static MovieMetadata Native() => new()
        {
            Code = "ABC-123", Title = "作品", Source = "JavDB",
            SourceUrl = "https://javdb.com/v/abc123"
        };

        var currentDirectory = Path.Combine(directory, "current-search");
        await SeedAsync(currentDirectory, "JavDB 标题", "作品", 2, [paid]);
        var currentFetches = 0;
        var current = await ScrapeCandidateCache.GetOrFetchAsync(currentDirectory, "JavDB 标题", "作品", () =>
        {
            currentFetches++;
            return Task.FromResult(new List<MovieMetadata> { Native() });
        });

        var legacyDirectory = Path.Combine(directory, "legacy-search");
        await SeedAsync(legacyDirectory, "JavDB 标题", "作品", 0, [paid]);
        var legacyFetches = 0;
        var legacy = await ScrapeCandidateCache.GetOrFetchAsync(legacyDirectory, "JavDB 标题", "作品", () =>
        {
            legacyFetches++;
            return Task.FromResult(new List<MovieMetadata> { Native() });
        });

        var detailDirectory = Path.Combine(directory, "detail");
        var detailQuery = "https://javdb.com/v/abc123";
        var paidDetail = paid.Clone();
        paidDetail.DetailFetched = true;
        paidDetail.DetailIdentityVerified = true;
        await SeedAsync(detailDirectory, "JavDB 详情", detailQuery, 2, [paidDetail]);
        var detailFetches = 0;
        var detail = await ScrapeCandidateCache.GetOrFetchAsync(detailDirectory, "JavDB 详情", detailQuery, () =>
        {
            detailFetches++;
            var native = Native();
            native.DetailFetched = true;
            native.DetailIdentityVerified = true;
            return Task.FromResult(new List<MovieMetadata> { native });
        });

        var freshDirectory = Path.Combine(directory, "fresh-mismatch");
        var freshRejected = false;
        try
        {
            await ScrapeCandidateCache.GetOrFetchAsync(freshDirectory, "JavDB 标题", "作品", () =>
                Task.FromResult(new List<MovieMetadata> { paid.Clone() }));
        }
        catch (InvalidDataException) { freshRejected = true; }

        var filtered = MainWindow.FilterTitleQueueCandidates([
            Native(), paid, new MovieMetadata { Source = "JavDB Proxy" },
            new MovieMetadata { Source = "TMDB 电影" },
            new MovieMetadata { Source = "JAVDatabase" }
        ]);
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["nativeCacheRejectsPaidSearchCandidate"] = currentFetches == 1 && current.Count == 1 &&
                current[0].Source == "JavDB",
            ["nativeCacheRejectsPaidLegacyCandidate"] = legacyFetches == 1 && legacy.Count == 1 &&
                legacy[0].Source == "JavDB",
            ["nativeCacheRejectsPaidDetailCandidate"] = detailFetches == 1 && detail.Count == 1 &&
                detail[0].Source == "JavDB",
            ["nativeFetchRejectsWrongSourceBeforeCaching"] = freshRejected &&
                !File.Exists(CachePath(freshDirectory, "JavDB 标题", "作品")),
            ["titleQueueOnlyDispatchesBuiltInSources"] = filtered.Count == 3 &&
                filtered.Select(candidate => candidate.Source)
                    .SequenceEqual(["JavDB", "TMDB 电影", "JAVDatabase"]),
            ["nativeCacheNeverReservesPaidRequest"] =
                !Directory.EnumerateFiles(directory, "javinfo-usage.json", SearchOption.AllDirectories).Any()
        };
    }

    static async Task SeedAsync(string directory, string source, string query, int schemaVersion,
        List<MovieMetadata> candidates)
    {
        var path = CachePath(directory, source, query);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ScrapeCandidateCache.Entry
        {
            SchemaVersion = schemaVersion,
            Source = source,
            Query = query,
            ParserVersion = "1",
            ExpiresUtc = DateTime.UtcNow.AddHours(1),
            Candidates = candidates
        }));
    }

    static string CachePath(string directory, string source, string query)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            source + "\n" + query.Trim().ToUpperInvariant())));
        return Path.Combine(directory, "candidate-cache", key + ".json");
    }
}

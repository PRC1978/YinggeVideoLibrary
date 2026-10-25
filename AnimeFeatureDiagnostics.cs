using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class AnimeFeatureDiagnostics
{
    internal static int RunToFile(string resultPath)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            foreach (var check in MikanScraperDiagnostics.Verify()) checks[check.Key] = check.Value;
            foreach (var check in FankuHubScraperDiagnostics.Verify()) checks[check.Key] = check.Value;
            foreach (var check in DmhyAnimeReleaseDiagnostics.Verify()) checks[check.Key] = check.Value;
            foreach (var check in AnimeSourceIntegrationDiagnostics.Verify()) checks[check.Key] = check.Value;
            var cacheDirectory = Path.Combine(Path.GetTempPath(), "YinggeAnimeCacheTest-" + Guid.NewGuid().ToString("N"));
            try { checks["animeVerifiedCacheCanBypassCooldown"] = ScrapeCandidateCache.VerifyAnimeSourceCacheReady(cacheDirectory); }
            finally { if (Directory.Exists(cacheDirectory)) Directory.Delete(cacheDirectory, recursive: true); }
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new
            {
                passed = checks.Values.All(value => value), checks
            }, new JsonSerializerOptions { WriteIndented = true }));
            return checks.Values.All(value => value) ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new
            {
                passed = false, error = ex.ToString(), checks
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}

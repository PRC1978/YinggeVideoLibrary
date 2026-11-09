using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class JavDbFailureDiagnostics
{
    internal static Dictionary<string, bool> Verify()
    {
        var checks = new Dictionary<string, bool>();
        bool Cooling(Action action)
        {
            try { action(); return false; }
            catch (ScrapeSourceUnavailableException ex) { return ex.RetryAt > DateTimeOffset.UtcNow.AddMinutes(29); }
        }
        bool NotFound(Action action)
        {
            try { action(); return false; }
            catch (ScrapeNotFoundException) { return true; }
        }
        const string card = "<a class='box' href='/v/sample'><img title='Example'><strong>ABC-123</strong></a>";
        checks["javDbKnownCardRemainsExact"] = JavDbScraper.FindExactResultCard(card, "ABC-123").Success;
        checks["javDbOtherExactCodeIsNotFound"] = NotFound(() => JavDbScraper.FindExactResultCard(card, "ABC-124"));
        checks["javDbExplicitEmptyIsNotFound"] = NotFound(() => JavDbScraper.FindExactResultCard("<title>JavDB</title><p>No results found</p>", "ABC-123"));
        // Fixed markup captured from the public synthetic no-match smoke on 2026-11-09.
        const string actualEmpty = "<title>JavDB</title><div class=\"empty-message\">暫無內容</div>";
        checks["javDbPublicEmptyMarkupIsNotFound"] = NotFound(() => JavDbScraper.FindExactResultCard(actualEmpty, "ABC-123"));
        checks["javDbPublicEmptyMarkupIsEmptyTitleSearch"] = JavDbScraper.ParseTitleSearch(actualEmpty).Count == 0;
        checks["javDbEmptyWordsInOrdinaryBodyRemainUnknown"] = Cooling(() => JavDbScraper.FindExactResultCard("<title>JavDB</title><p>暫無內容</p>", "ABC-123"));
        checks["javDbChallengeTakesPriorityOverEmptyMarkup"] = Cooling(() => JavDbScraper.FindExactResultCard(actualEmpty + "<script src='/cf-chl-test'></script>", "ABC-123"));
        checks["javDbInactiveAuthCaptchaDoesNotOverrideExplicitEmpty"] = NotFound(() => JavDbScraper.FindExactResultCard(actualEmpty + "<div class='modal auth-modal' id='auth-modal'><input name='_rucaptcha'></div>", "ABC-123"));
        checks["javDbGenericCaptchaWithoutEmptyStillCools"] = Cooling(() => JavDbScraper.FindExactResultCard("<title>JavDB</title><input name='captcha'>", "ABC-123"));
        checks["javDbVerificationTitleStillOverridesEmpty"] = Cooling(() => JavDbScraper.FindExactResultCard(actualEmpty.Replace("<title>JavDB</title>", "<title>Captcha - JavDB</title>"), "ABC-123"));
        checks["javDbExplicitHumanChallengeStillOverridesEmpty"] = Cooling(() => JavDbScraper.FindExactResultCard(actualEmpty + "<p>verify you are human</p>", "ABC-123"));
        checks["javDbUnknownStructureCoolsSource"] = Cooling(() => JavDbScraper.FindExactResultCard("<title>JavDB</title><main>changed</main>", "ABC-123"));
        checks["javDbBlankCoolsSource"] = Cooling(() => JavDbScraper.FindExactResultCard("", "ABC-123"));
        checks["javDbChallengeCoolsSource"] = Cooling(() => JavDbScraper.FindExactResultCard("<title>Just a moment</title><script src='/cf-chl-test'></script>", "ABC-123"));
        checks["javDbRatePageCoolsSource"] = Cooling(() => JavDbScraper.ParseTitleSearch("<p>too many requests</p>"));
        checks["javDbMalformedTitleCardCoolsSource"] = Cooling(() => JavDbScraper.ParseTitleSearch("<a class='box' href='/v/sample'>changed</a>"));
        using var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden);
        checks["javDb403CoolsSource"] = Cooling(() => JavDbScraper.EnsureSuccessfulResponseForDiagnostics(forbidden));
        using var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        try { JavDbScraper.EnsureSuccessfulResponseForDiagnostics(unauthorized); checks["javDb401NotHidden"] = false; }
        catch (ScrapeHttpException ex) { checks["javDb401NotHidden"] = ex.StatusCode == HttpStatusCode.Unauthorized; }
        checks["javDatabaseUnknownStructureCoolsSource"] = Cooling(() => JavDatabaseScraper.ParseTitleSearch("<html>" + new string(' ', 500) + "changed</html>"));
        checks["javDatabaseVerificationCoolsSource"] = Cooling(() => JavDatabaseScraper.ParseTitleSearch("<html>" + new string(' ', 500) + "verify you are human</html>"));
        checks["javDatabaseExplicitEmptyRemainsEmpty"] = JavDatabaseScraper.ParseTitleSearch("<html>" + new string(' ', 500) + "No results found.</html>").Count == 0;
        return checks;
    }

    public static async Task<int> RunAsync(string resultPath, bool publicSmoke, string? publicSnapshotPath = null)
    {
        var checks = Verify();
        foreach (var check in ScrapeQueueFailureSummary.VerifyForDiagnostics()) checks[check.Key] = check.Value;
        var observations = new List<object>();
        if (publicSnapshotPath is not null)
        {
            // Explicit opt-in offline replay of a previously saved public HTML response.
            var html = await File.ReadAllTextAsync(publicSnapshotPath);
            try { JavDbScraper.FindExactResultCard(html, "YINGGEDIAGNOSTICNOMATCH-999999999"); checks["javDbSavedPublicEmptySnapshotIsNotFound"] = false; }
            catch (ScrapeNotFoundException) { checks["javDbSavedPublicEmptySnapshotIsNotFound"] = true; }
            checks["javDbSavedPublicEmptySnapshotHasNoTitleCandidates"] = JavDbScraper.ParseTitleSearch(html).Count == 0;
            observations.Add(new { classification = "offline-public-empty-snapshot", sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(publicSnapshotPath))) });
        }
        if (publicSmoke)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(resultPath))!;
            Directory.CreateDirectory(directory);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
            // Public synthetic queries only. Never use a user's queue or bypass a refusal.
            foreach (var query in new[] { "JUL-001", "YINGGEDIAGNOSTICNOMATCH-999999999" })
            {
                try
                {
                    using var response = await client.GetAsync("https://javdb.com/search?q=" + Uri.EscapeDataString(query) + "&f=all");
                    var html = await response.Content.ReadAsStringAsync();
                    var snapshot = Path.Combine(directory, query + ".html");
                    await File.WriteAllTextAsync(snapshot, html);
                    var classification = "unclassified";
                    try
                    {
                        JavDbScraper.EnsureSuccessfulResponseForDiagnostics(response);
                        JavDbScraper.FindExactResultCard(html, query);
                        classification = "exact-card";
                    }
                    catch (ScrapeSourceUnavailableException) { classification = "source-unavailable"; }
                    catch (ScrapeNotFoundException) { classification = "explicit-no-exact-match"; }
                    catch (ScrapeHttpException) { classification = "http-error"; }
                    catch (InvalidOperationException) { classification = "identity-or-redirect-rejected"; }
                    observations.Add(new { query, status = (int)response.StatusCode, classification, snapshot,
                        sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(snapshot))) });
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                { observations.Add(new { query, classification = "transport-unavailable", errorType = ex.GetType().Name }); }
            }
        }
        var passed = checks.Values.All(value => value);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new { passed, realLibraryRead = false,
            realLibraryWritten = false, checks, observations }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}

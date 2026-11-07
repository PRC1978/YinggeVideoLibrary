using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Npgsql;

namespace YinggeVideoLibrary;

internal static class R18OfflineDiagnostics
{
    internal static IEnumerable<KeyValuePair<string, bool>> VerifyStatic()
    {
        yield return new("r18SourceOptInOnly", !MainWindow.PlanCandidateSources(3, false).Contains(16) &&
            MainWindow.PlanCandidateSources(3, false, r18Offline: true).Contains(16) &&
            MainWindow.PlanQueueCodeSources(false, false).Length == 0 &&
            MainWindow.PlanQueueCodeSources(false, false, true).SequenceEqual([R18OfflineCatalog.Source]));
        yield return new("r18PosterUrlValidated", R18OfflineCatalog.PosterUrl("digital/video/abc00123/abc00123pl") ==
            "https://pics.dmm.co.jp/digital/video/abc00123/abc00123pl.jpg" &&
            R18OfflineCatalog.PosterUrl("digital/video/../secret") is null &&
            R18OfflineCatalog.PosterUrl("https://pics.dmm.co.jp.evil.invalid/a.jpg") is null);
        yield return new("r18NativeIdentityRequiresExactCode", NativeMetadataAggregation.IsNativeSource(R18OfflineCatalog.Source) &&
            !ScrapeCodeIdentity.Same("ABC1-23", "ABC-123"));
        var workUrl = "https://r18.dev/#offline-abc00123";
        var edition = new MovieMetadata { Source = R18OfflineCatalog.Source, SourceUrl = workUrl,
            Code = "ABC-123", DetailFetched = true, DetailIdentityVerified = true };
        var differentEdition = new MovieMetadata { Source = R18OfflineCatalog.Source,
            SourceUrl = "https://r18.dev/#offline-abc00124", Code = "ABC-123",
            DetailFetched = true, DetailIdentityVerified = true };
        yield return new("r18CachedEditionsRequireSameContentId",
            ScrapeCandidateCache.DetailMatchesCandidate(edition, edition) &&
            !ScrapeCandidateCache.DetailMatchesCandidate(edition, differentEdition));
        edition.ExternalIds["R18"] = "abc00123";
        differentEdition.ExternalIds["R18"] = "abc00124";
        yield return new("r18SameCodeRequiresExplicitEditionSelection",
            !R18OfflineCatalog.CodeResultsRequireSelection([edition]) &&
            R18OfflineCatalog.CodeResultsRequireSelection([edition, differentEdition]));
        var credit = new VerifiedPersonCredit
        {
            Provider = "R18", PersonId = "actress:123", Name = "示例人物",
            PersonUrl = "https://r18.dev/#offline-person-actress-123",
            VerifiedFromWorkUrl = workUrl
        };
        var work = new MovieMetadata
        {
            Source = R18OfflineCatalog.Source, SourceUrl = workUrl,
            DetailFetched = true, DetailIdentityVerified = true, IsAdultContent = true,
            ExternalIds = new Dictionary<string, string> { ["R18"] = "abc00123" },
            PersonCredits = [credit]
        };
        yield return new("r18VerifiedPersonIdLinksWork", RoleProfileStore.TryVerifiedPersonCredit(work, credit, out var identity) &&
            RoleProfileStore.IsTrustedIdentity(identity) && identity.PersonId == "actress:123");
        work.PersonCredits.Add(new VerifiedPersonCredit
        {
            Provider = "R18", PersonId = "actress:124", Name = "示例人物",
            PersonUrl = "https://r18.dev/#offline-person-actress-124",
            VerifiedFromWorkUrl = workUrl
        });
        var video = new VideoItem { Id = "r18-diagnostic", Title = "示例作品",
            Path = "C:\\r18-diagnostic.mp4", Folder = "C:\\", Size = 1, Modified = DateTime.UtcNow };
        var preview = RoleProfileAutoScraper.Preview([video],
            new Dictionary<string, MovieMetadata> { [video.Id] = work }, new RoleProfileStore.State());
        yield return new("r18SameNameDistinctIdsStaySeparate", preview.Candidates.Count == 2 &&
            preview.Candidates.Select(item => item.PersonId).Distinct().Count() == 2);
        credit.PersonId = "actress:999";
        yield return new("r18ForgedPersonIdRejected", !RoleProfileStore.TryVerifiedPersonCredit(work, credit, out _));
    }

    internal static int Run(string connectionString, string dataDirectory, string archivePath, string resultPath)
    {
        var result = new Dictionary<string, object?> { ["realLibraryRead"] = false, ["passed"] = false };
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var imported = archivePath == "-" ? null : R18OfflineCatalog.Import(connectionString, dataDirectory, archivePath,
                CancellationToken.None, new Progress<string>(message => Console.WriteLine(message)));
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var sample = new NpgsqlCommand(
                "SELECT dvd_id,title_en FROM yingge_r18_active.derived_video WHERE dvd_id IS NOT NULL AND length(title_en)>12 ORDER BY content_id LIMIT 1", connection);
            using var row = sample.ExecuteReader();
            if (!row.Read()) throw new InvalidDataException("找不到测试作品。 ");
            var code = row.GetString(0);
            var originalTitle = row.GetString(1);
            var title = originalTitle[..Math.Min(originalTitle.Length, 80)];
            row.Close();
            var clock = Stopwatch.StartNew();
            var found = R18OfflineCatalog.SearchCode(connectionString, code);
            result["codeLookupMs"] = clock.ElapsedMilliseconds;
            clock.Restart();
            var titleFound = R18OfflineCatalog.SearchTitle(connectionString, title);
            result["titleLookupMs"] = clock.ElapsedMilliseconds;
            result["archiveSha256"] = imported?.Sha256;
            result["version"] = imported?.Version;
            result["videoCount"] = imported?.VideoCount;
            result["sampleCode"] = code;
            result["sampleResults"] = found.Count;
            using var duplicateCode = new NpgsqlCommand(
                "SELECT content_id FROM yingge_r18_active.derived_video WHERE dvd_id='SS-023' LIMIT 2", connection);
            using var duplicateCodeRows = duplicateCode.ExecuteReader();
            var duplicateCodeCount = 0;
            while (duplicateCodeRows.Read()) duplicateCodeCount++;
            duplicateCodeRows.Close();
            var duplicateFound = duplicateCodeCount < 2 ? null : R18OfflineCatalog.SearchCode(connectionString, "SS-023");
            result["duplicateCodeChecked"] = duplicateFound is not null;
            result["duplicateCodeRequiresSelection"] = duplicateFound is null ||
                R18OfflineCatalog.CodeResultsRequireSelection(duplicateFound);
            result["titleResults"] = titleFound.Count;
            result["titleMatchedSample"] = titleFound.Any(item => NativeMetadataAggregation.HasExactCode(item, code));
            using var duplicateSample = new NpgsqlCommand("""
                SELECT content_id,title_en FROM yingge_r18_active.derived_video
                WHERE dvd_id='SS-023' AND title_en='Tokyo Mask Club' LIMIT 1
                """, connection);
            using var duplicateRow = duplicateSample.ExecuteReader();
            string? editionId = null;
            string? editionTitle = null;
            if (duplicateRow.Read())
            {
                editionId = duplicateRow.GetString(0);
                editionTitle = duplicateRow.GetString(1);
            }
            duplicateRow.Close();
            var editionResults = editionTitle is null ? null : R18OfflineCatalog.SearchTitle(connectionString, editionTitle);
            var editionIsolated = editionResults is null || editionResults.Any(item =>
                    item.ExternalIds.GetValueOrDefault("R18") == editionId) &&
                editionResults.All(item => item.Code != "SS-023" ||
                    item.ExternalIds.GetValueOrDefault("R18") == editionId);
            result["duplicateEditionChecked"] = editionResults is not null;
            result["duplicateEditionIsolated"] = editionIsolated;
            result["posterUrl"] = found.FirstOrDefault()?.PosterUrl;
            result["verifiedPersonCredits"] = found.Sum(item => item.PersonCredits.Count(credit =>
                RoleProfileStore.TryVerifiedPersonCredit(item, credit, out _)));
            result["passed"] = editionIsolated && (duplicateFound is null || R18OfflineCatalog.CodeResultsRequireSelection(duplicateFound)) &&
                found.Count > 0 && titleFound.Any(item => NativeMetadataAggregation.HasExactCode(item, code)) && found.All(item =>
                item.DetailIdentityVerified && NativeMetadataAggregation.HasExactCode(item, code));
        }
        catch (Exception ex) { result["error"] = ex.ToString(); }
        File.WriteAllText(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result["passed"] is true ? 0 : 1;
    }
}

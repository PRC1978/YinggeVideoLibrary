using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class RoleProfileDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Verify(string parent)
    {
        var root = Path.Combine(parent, "role-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var result = new Dictionary<string, bool>();
        try
        {
            var first = new RoleProfileStore.Profile { Name = "同名人物", Biography = "用户自己填写的生平" };
            var second = new RoleProfileStore.Profile { Name = "同名人物" };
            first.Works.Add(new RoleProfileStore.WorkLink
            {
                LocalVideoId = "local-1", WorkTitle = "作品 A", LinkOrigin = "manual"
            });
            var state = new RoleProfileStore.State { Profiles = [first, second] };
            RoleProfileStore.Save(root, state);
            var loaded = RoleProfileStore.Load(root);
            result["roleProfilesSameNameStaysSeparate"] = loaded.Profiles.Count == 2
                && loaded.Profiles[0].Id != loaded.Profiles[1].Id
                && loaded.Profiles[0].Works.Single().LocalVideoId == "local-1"
                && loaded.Profiles[1].Works.Count == 0;
            var stale = RoleProfileStore.Load(root);
            loaded.Profiles[0].Role = "主演";
            RoleProfileStore.Save(root, loaded);
            try { stale.Profiles[1].Role = "stale"; RoleProfileStore.Save(root, stale);
                result["roleProfilesRejectStaleWriter"] = false; }
            catch (InvalidDataException) { result["roleProfilesRejectStaleWriter"] = true; }
            File.WriteAllText(Path.Combine(root, RoleProfileStore.FileName), "{broken-json");
            result["roleProfilesRecoverLastGoodSnapshot"] = RoleProfileStore.Load(root).Profiles.Count == 2;
            File.WriteAllText(Path.Combine(root, RoleProfileStore.FileName),
                "{\"Version\":1,\"Revision\":\"x\",\"Profiles\":[{\"Id\":\""
                + Guid.NewGuid().ToString("N") + "\",\"Name\":\"bad\",\"Works\":[{\"LocalVideoId\":\"v\",\"EvidenceUrl\":null}]}]}");
            try { RoleProfileStore.VerifyFile(Path.Combine(root, RoleProfileStore.FileName));
                result["roleProfilesRejectNullFields"] = false; }
            catch (InvalidDataException) { result["roleProfilesRejectNullFields"] = true; }
            File.Delete(Path.Combine(root, RoleProfileStore.FileName));
            loaded = RoleProfileStore.Load(root);
            loaded.Profiles[0].Role = "主演";
            RoleProfileStore.Save(root, loaded);

            var tmdbCandidate = new MovieMetadata
            {
                Source = "TMDB 电影", SourceUrl = "https://www.themoviedb.org/movie/42",
                Title = "Example", DetailFetched = false
            };
            using var tmdbJson = JsonDocument.Parse("""
                {"id":42,"title":"Example","credits":{"cast":[
                  {"id":101,"name":"Alex","character":"角色甲"},
                  {"id":102,"name":"Alex","character":"角色乙"}]}}
                """);
            var tmdb = AdditionalScrapers.ParseTmdbDetails(tmdbCandidate, tmdbJson.RootElement, "movie");
            var firstCredit = tmdb.PersonCredits[0];
            result["roleProfilesTmdbCastKeepsDistinctPersonIds"] = tmdb.PersonCredits.Count == 2
                && tmdb.PersonCredits[0].Name == tmdb.PersonCredits[1].Name
                && tmdb.PersonCredits[0].PersonId != tmdb.PersonCredits[1].PersonId
                && RoleProfileStore.TryVerifiedPersonCredit(tmdb, firstCredit, out _);
            var previouslyIndexed = new VideoItem
            {
                Id = "previously-indexed-movie", Title = "Example", Category = "电影",
                Path = Path.Combine(root, "Example.mp4"), Folder = root,
                Size = 1, Modified = DateTime.UtcNow
            };
            var tmdbWork = new RoleProfileStore.WorkLink
            {
                LocalVideoId = previouslyIndexed.Id, WorkTitle = "Example",
                LinkOrigin = "verified-source", SourceProvider = "TMDB",
                SourcePersonId = firstCredit.PersonId,
                SourceWorkId = tmdb.SourceUrl, EvidenceUrl = tmdb.SourceUrl
            };
            var videoMap = new Dictionary<string, VideoItem>(StringComparer.OrdinalIgnoreCase)
                { [previouslyIndexed.Id] = previouslyIndexed };
            var recordMap = new Dictionary<string, MovieMetadata>(StringComparer.OrdinalIgnoreCase)
                { [previouslyIndexed.Id] = tmdb };
            var shownBeforeReclassification = RoleProfilesWindow.TmdbWorkIsAllowed(
                tmdbWork, videoMap, recordMap);
            previouslyIndexed.Category = "成人";
            previouslyIndexed.CategoryList.Clear();
            previouslyIndexed.CategoryList.Add("成人");
            var oldCacheWindow = new RoleProfilesWindow(root, [previouslyIndexed], tmdb,
                previouslyIndexed, "", _ => { }, true, recordMap);
            var oldCacheEntryHidden = oldCacheWindow.VerifiedOptionCountForDiagnostics == 0;
            oldCacheWindow.Close();
            result["roleProfilesOldTmdbCacheHiddenAfterAdultReclassification"] =
                shownBeforeReclassification && oldCacheEntryHidden && !RoleProfilesWindow.TmdbWorkIsAllowed(
                    tmdbWork, videoMap, recordMap);
            var forged = firstCredit.Clone(); forged.PersonId = "102";
            result["roleProfilesRejectForgedCredit"] = !RoleProfileStore.TryVerifiedPersonCredit(tmdb, forged, out _);
            var tmdbIdentity = new RoleProfileStore.SourceIdentity
            {
                Provider = "TMDB", PersonId = "101", PersonUrl = "https://www.themoviedb.org/person/101",
                NameAtSource = "Alex", VerifiedFromWorkUrl = tmdb.SourceUrl
            };
            using var personJson = JsonDocument.Parse("""
                {"id":101,"name":"Alex","biography":"来自 TMDB 的简介","birthday":"1980-01-02",
                 "gender":1,"place_of_birth":"City","profile_path":"/abcd1234.jpg"}
                """);
            var sourced = RolePersonDetailsFetcher.ParseTmdb(tmdbIdentity, personJson.RootElement);
            result["roleProfilesSourcedBiographyIsSeparate"] = sourced.Biography == "来自 TMDB 的简介"
                && first.Biography == "用户自己填写的生平" && sourced.Birthday == "1980-01-02"
                && RolePersonDetailsFetcher.TrustedPortraitUrl("TMDB", sourced.PortraitUrl)
                && !RolePersonDetailsFetcher.TrustedPortraitUrl("TMDB", "https://image.tmdb.org.evil.test/t/p/w342/abcd.jpg");
            try
            {
                using var mismatch = JsonDocument.Parse("""{"id":999,"name":"Alex"}""");
                _ = RolePersonDetailsFetcher.ParseTmdb(tmdbIdentity, mismatch.RootElement);
                result["roleProfilesRejectWrongPersonDetails"] = false;
            }
            catch (InvalidDataException) { result["roleProfilesRejectWrongPersonDetails"] = true; }

            var tvCandidate = new MovieMetadata
            {
                Source = "TVmaze", SourceUrl = "https://www.tvmaze.com/shows/7", Title = "Show"
            };
            using var tvJson = JsonDocument.Parse("""
                {"id":7,"name":"Show","_embedded":{"cast":[{"person":{"id":29,"name":"Taylor"},
                  "character":{"name":"Protagonist"}}]}}
                """);
            var tv = AdditionalScrapers.ParseTvMazeDetails(tvCandidate, tvJson.RootElement);
            var tvCredit = tv.PersonCredits.Single();
            var tvIdentity = new RoleProfileStore.SourceIdentity
            {
                Provider = "TVmaze", PersonId = "29", PersonUrl = "https://www.tvmaze.com/people/29",
                NameAtSource = "Taylor", VerifiedFromWorkUrl = tv.SourceUrl
            };
            using var tvPerson = JsonDocument.Parse("""
                {"id":29,"name":"Taylor","birthday":"1990-05-06","gender":"Female",
                 "country":{"name":"Canada"},
                 "image":{"medium":"https://static.tvmaze.com/uploads/images/medium_portrait/0/123.jpg"}}
                """);
            var tvDetails = RolePersonDetailsFetcher.ParseTvmaze(tvIdentity, tvPerson.RootElement);
            result["roleProfilesTvmazeVerifiedPersonAndMissingBio"] =
                RoleProfileStore.TryVerifiedPersonCredit(tv, tvCredit, out _) &&
                tvDetails.Birthday == "1990-05-06" && tvDetails.Biography.Length == 0 &&
                RolePersonDetailsFetcher.TrustedPortraitUrl("TVmaze", tvDetails.PortraitUrl);
            result["roleProfilesTvmazeCountryIsNotBirthplace"] =
                tvDetails.PlaceOfBirth.Length == 0 && tvDetails.Country == "Canada";

            var forgedProfile = new RoleProfileStore.Profile { Name = "导入身份校验" };
            forgedProfile.Sources.Add(tmdbIdentity);
            forgedProfile.Works.Add(new RoleProfileStore.WorkLink
            {
                LocalVideoId = "v1", WorkTitle = "Example", LinkOrigin = "verified-source",
                SourceProvider = "TMDB", SourcePersonId = "999",
                SourceWorkId = tmdb.SourceUrl, EvidenceUrl = tmdb.SourceUrl
            });
            try { RoleProfileStore.ValidateState(new RoleProfileStore.State { Profiles = [forgedProfile] });
                result["roleProfilesRejectForgedImportedRelation"] = false; }
            catch (InvalidDataException) { result["roleProfilesRejectForgedImportedRelation"] = true; }
            forgedProfile.Works.Clear();
            forgedProfile.Works.Add(new RoleProfileStore.WorkLink
                { LocalVideoId = "v1", WorkTitle = "other", LinkOrigin = "manual" });
            try { RoleProfileStore.ValidateState(new RoleProfileStore.State { Profiles = [forgedProfile] });
                result["roleProfilesTmdbRejectsManualCrossContentLink"] = false; }
            catch (InvalidDataException) { result["roleProfilesTmdbRejectsManualCrossContentLink"] = true; }

            var replacementPath = Path.Combine(root, "replacement.bin");
            File.WriteAllBytes(replacementPath, [1, 2, 3, 4, 5]);
            var replacementInfo = new FileInfo(replacementPath);
            var replacement = new VideoItem { Id = "same-path", Path = replacementPath,
                Folder = root, Title = "同路径视频", Size = replacementInfo.Length,
                Modified = replacementInfo.LastWriteTime };
            var captured = RoleLocalWorkIdentity.CaptureAsync(replacement).GetAwaiter().GetResult();
            var linked = new RoleProfileStore.WorkLink { LocalVideoId = replacement.Id,
                WorkTitle = replacement.Title, LinkOrigin = "manual" };
            RoleLocalWorkIdentity.Apply(linked, captured);
            var beforeReplacement = RoleLocalWorkIdentity.MatchesAsync(replacement, linked)
                .GetAwaiter().GetResult();
            File.WriteAllBytes(replacementPath, [5, 4, 3, 2, 1]);
            File.SetLastWriteTime(replacementPath, replacement.Modified);
            result["roleProfilesSamePathReplacementCannotOpen"] = beforeReplacement
                && !RoleLocalWorkIdentity.MatchesAsync(replacement, linked).GetAwaiter().GetResult();

            var video = new VideoItem { Id = "local-1", Path = Path.Combine(root, "a.mp4"),
                Folder = root, Title = "Example", Category = "电影", Modified = DateTime.Now, Size = 100 };
            var many = new RoleProfileStore.Profile { Name = "分页测试" };
            for (var index = 0; index < 130; index++) many.Works.Add(new RoleProfileStore.WorkLink
            {
                LocalVideoId = "test-" + index, WorkTitle = "作品 " + index, LinkOrigin = "manual"
            });
            loaded.Profiles.Add(many);
            RoleProfileStore.Save(root, loaded);
            var browser = new RoleProfilesWindow(root, [video], tmdb, video, "", _ => { }, true);
            result["roleProfilesOffscreenUiAndVerifiedEntry"] = browser.VerifiedOptionCountForDiagnostics == 2;
            browser.SelectProfileForDiagnostics(many.Id);
            result["roleProfilesLargeWorksArePaged"] = browser.DisplayedWorkCountForDiagnostics == 40;
            browser.Close();

            File.WriteAllText(Path.Combine(root, "config.json"), JsonSerializer.Serialize(new LibraryConfig()));
            var backup = LibraryBackupService.Create(root, forceDefault: true);
            result["roleProfilesIncludedAndValidatedInBackup"] =
                File.Exists(Path.Combine(backup, RoleProfileStore.FileName)) &&
                LibraryBackupService.Verify(backup).IsValid;

            var legacy = Path.Combine(root, "legacy-restore");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "config.json"), JsonSerializer.Serialize(new LibraryConfig()));
            var oldPackage = LibraryBackupService.Create(legacy, forceDefault: true);
            var liveProfiles = new RoleProfileStore.State
            {
                Profiles = [new RoleProfileStore.Profile { Name = "备份以后新建的人物" }]
            };
            RoleProfileStore.Save(legacy, liveProfiles);
            liveProfiles.Profiles[0].Biography = "第二次保存，生成安全副本";
            RoleProfileStore.Save(legacy, liveProfiles);
            var rolePath = Path.Combine(legacy, RoleProfileStore.FileName);
            var siteSidecar = Path.Combine(legacy, "site-list-import.json.bak");
            File.WriteAllText(siteSidecar, "旧片单副本");
            LibraryBackupService.StageRestore(legacy, oldPackage);
            var appliedOld = LibraryBackupService.ApplyPendingAtStartup(legacy, out _, false);
            result["roleProfilesLegacyRestoreRemovesBothSidecars"] = appliedOld
                && !File.Exists(rolePath) && !File.Exists(rolePath + ".bak")
                && !File.Exists(siteSidecar) && RoleProfileStore.Load(legacy).Profiles.Count == 0
                && SiteListImportStore.Load(legacy).Entries.Count == 0;

            RoleProfileStore.Save(legacy, liveProfiles = new RoleProfileStore.State
            {
                Profiles = [new RoleProfileStore.Profile { Name = "回滚保留的人物" }]
            });
            liveProfiles.Profiles[0].Biography = "备份副本中保留同一人物 ID";
            RoleProfileStore.Save(legacy, liveProfiles);
            var survivingId = liveProfiles.Profiles[0].Id;
            File.Delete(rolePath); // Only the valid sidecar remains at rollback capture time.
            var rollbackPackage = LibraryBackupService.Create(legacy, rollback: true);
            File.WriteAllText(Path.Combine(legacy, "restore-active.json"), JsonSerializer.Serialize(new
            {
                BackupPath = oldPackage, RollbackPath = rollbackPackage
            }));
            var rolledBack = !LibraryBackupService.ApplyPendingAtStartup(legacy, out _, false);
            result["roleProfilesInterruptedRestoreRollsBackFallback"] = rolledBack
                && RoleProfileStore.Load(legacy).Profiles.SingleOrDefault()?.Id == survivingId
                && File.Exists(rolePath) && !File.Exists(rolePath + ".bak")
                && !File.Exists(Path.Combine(legacy, "restore-active.json"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
        return result;
    }
}

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

            foreach (var (name, passed) in VerifyAutoScrape(root, tmdb))
                result[name] = passed;

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

    static IReadOnlyDictionary<string, bool> VerifyAutoScrape(string root, MovieMetadata firstMovie)
    {
        var results = new Dictionary<string, bool>();
        var directory = Path.Combine(root, "auto-scrape");
        Directory.CreateDirectory(directory);

        static VideoItem Video(string directory, string id, string category)
        {
            var path = Path.Combine(directory, id + ".mp4");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var file = new FileInfo(path);
            return new VideoItem
            {
                Id = id, Title = "Example", Category = category,
                Path = path, Folder = directory, Size = file.Length,
                Modified = file.LastWriteTime
            };
        }

        var firstVideo = Video(directory, "auto-first", "电影");
        var secondVideo = Video(directory, "auto-second", "电影");
        var adultVideo = Video(directory, "auto-adult", "成人");
        var unverifiedVideo = Video(directory, "auto-unverified", "电影");
        var forgedVideo = Video(directory, "auto-forged", "电影");
        using var nextMovieJson = JsonDocument.Parse("""
            {"id":43,"title":"Second work","credits":{"cast":[
              {"id":101,"name":"Alex","character":"另一角色"}]}}
            """);
        var secondMovie = AdditionalScrapers.ParseTmdbDetails(new MovieMetadata
        {
            Source = "TMDB 电影", SourceUrl = "https://www.themoviedb.org/movie/43",
            Title = "Second work"
        }, nextMovieJson.RootElement, "movie");
        var unverified = firstMovie.Clone();
        unverified.DetailIdentityVerified = false;
        var forged = firstMovie.Clone();
        forged.PersonCredits = [firstMovie.PersonCredits[0].Clone()];
        forged.PersonCredits[0].PersonId = "999";
        var videos = new[] { firstVideo, secondVideo, adultVideo, unverifiedVideo, forgedVideo };
        var records = new Dictionary<string, MovieMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            [firstVideo.Id] = firstMovie,
            [secondVideo.Id] = secondMovie,
            [adultVideo.Id] = firstMovie.Clone(),
            [unverifiedVideo.Id] = unverified,
            [forgedVideo.Id] = forged
        };
        MetadataStore.Save(directory, records);
        var manual = new RoleProfileStore.Profile
        {
            Name = "Alex", Biography = "用户手写的同名人物简介"
        };
        RoleProfileStore.Save(directory, new RoleProfileStore.State { Profiles = [manual] });
        var preview = RoleProfileAutoScraper.Preview(videos, records,
            RoleProfileStore.Load(directory));
        results["roleProfilesAutoPreviewVerifiedOnlyAndDistinctIds"] =
            preview.Candidates.Count == 2 && preview.NewCount == 2 &&
            preview.WorkCount == 3 && !preview.CapacityExceeded &&
            preview.Candidates.Select(item => item.PersonId).OrderBy(id => id)
                .SequenceEqual(["101", "102"]);
        results["roleProfilesAutoRejectsUnverifiedAdultAndForged"] =
            preview.SkippedUnverifiedCount >= 3 &&
            preview.Candidates.All(item => item.Provider == "TMDB") &&
            preview.Candidates.Single(item => item.PersonId == "101").WorkCount == 2;

        static Task<RoleProfileStore.SourceDetails> FakeDetails(
            RoleProfileStore.SourceIdentity identity, CancellationToken _)
            => Task.FromResult(new RoleProfileStore.SourceDetails
            {
                Provider = identity.Provider, PersonId = identity.PersonId,
                SourceUrl = identity.PersonUrl, Name = identity.NameAtSource,
                Biography = "来源独立简介"
            });

        var imported = Task.Run(() => RoleProfileAutoScraper.RunAsync(directory, preview,
            "offline-diagnostic-token", fetchDetails: FakeDetails)).GetAwaiter().GetResult();
        var state = RoleProfileStore.Load(directory);
        var id101 = RoleProfileStore.FindBySource(state, "TMDB", "101");
        var id102 = RoleProfileStore.FindBySource(state, "TMDB", "102");
        results["roleProfilesAutoImportsStablePeopleAndWorkEvidence"] =
            imported.Created == 2 && imported.Fetched == 2 && imported.Failed == 0 &&
            state.Profiles.Count == 3 && id101 is not null && id102 is not null &&
            id101.Id != id102.Id && id101.Works.Count == 2 && id102.Works.Count == 1 &&
            id101.Works.All(work => work.LinkOrigin == "verified-source" &&
                work.EvidenceUrl == work.SourceWorkId && work.SourcePersonId == "101") &&
            id101.Works.All(work => work.LocalVideoId != adultVideo.Id &&
                work.LocalVideoId != unverifiedVideo.Id && work.LocalVideoId != forgedVideo.Id) &&
            state.Profiles.Single(item => item.Id == manual.Id).Biography == manual.Biography;
        results["roleProfilesAutoKeepsManualAndSourcedFieldsSeparate"] =
            id101?.Biography.Length == 0 &&
            id101.SourcedDetails.SingleOrDefault()?.Biography == "来源独立简介" &&
            state.Profiles.Single(item => item.Id == manual.Id).Sources.Count == 0;

        id101!.Biography = "用户后来手写的简介";
        RoleProfileStore.Save(directory, state);
        var secondPreview = RoleProfileAutoScraper.Preview(videos, records,
            RoleProfileStore.Load(directory));
        var repeated = Task.Run(() => RoleProfileAutoScraper.RunAsync(directory, secondPreview,
            "offline-diagnostic-token", fetchDetails: FakeDetails)).GetAwaiter().GetResult();
        var afterRepeat = RoleProfileStore.Load(directory);
        var repeatedPerson = RoleProfileStore.FindBySource(afterRepeat, "TMDB", "101");
        results["roleProfilesAutoRerunIsIdempotentAndPreservesManualEdits"] =
            secondPreview.NewCount == 0 && secondPreview.ExistingCount == 2 &&
            repeated.Created == 0 && repeated.Fetched == 0 &&
            afterRepeat.Profiles.Count == 3 && repeatedPerson?.Works.Count == 2 &&
            repeatedPerson.Biography == "用户后来手写的简介" &&
            repeatedPerson.SourcedDetails.Single().Biography == "来源独立简介";

        // The store accepts 10,000 profiles. A preview that would cross that
        // boundary must stop before changing even one existing profile.
        var quotaDirectory = Path.Combine(root, "auto-scrape-quota");
        Directory.CreateDirectory(quotaDirectory);
        var quotaState = new RoleProfileStore.State
        {
            Profiles = Enumerable.Range(0, 9999).Select(index => new RoleProfileStore.Profile
            {
                Name = "容量测试 " + index
            }).ToList()
        };
        RoleProfileStore.Save(quotaDirectory, quotaState);
        var quotaPreview = RoleProfileAutoScraper.Preview(videos, records,
            RoleProfileStore.Load(quotaDirectory));
        var revisionBefore = RoleProfileStore.Load(quotaDirectory).Revision;
        var quotaRejected = false;
        try
        {
            _ = Task.Run(() => RoleProfileAutoScraper.RunAsync(quotaDirectory, quotaPreview,
                "offline-diagnostic-token", fetchDetails: FakeDetails)).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException) { quotaRejected = true; }
        var quotaAfter = RoleProfileStore.Load(quotaDirectory);
        results["roleProfilesAutoQuotaRejectsWithoutMutation"] =
            quotaPreview.CapacityExceeded && quotaRejected &&
            quotaAfter.Profiles.Count == 9999 && quotaAfter.Revision == revisionBefore;

        // A long network request can overlap another process accepting a
        // changed work. The next candidate must use the fresh disk record.
        var changedDirectory = Path.Combine(root, "auto-scrape-changed-work");
        Directory.CreateDirectory(changedDirectory);
        var changedRecords = records.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
        MetadataStore.Save(changedDirectory, changedRecords);
        var changingPreview = RoleProfileAutoScraper.Preview(videos, changedRecords,
            RoleProfileStore.Load(changedDirectory));
        var changedDuringFetch = false;
        Task<RoleProfileStore.SourceDetails> ChangeLaterCredit(
            RoleProfileStore.SourceIdentity identity, CancellationToken token)
        {
            if (!changedDuringFetch)
            {
                changedDuringFetch = true;
                changedRecords[firstVideo.Id].PersonCredits.RemoveAll(credit => credit.PersonId == "102");
                MetadataStore.Save(changedDirectory, changedRecords);
            }
            return FakeDetails(identity, token);
        }
        var changingRun = Task.Run(() => RoleProfileAutoScraper.RunAsync(changedDirectory,
            changingPreview, "offline-diagnostic-token", fetchDetails: ChangeLaterCredit))
            .GetAwaiter().GetResult();
        var changedState = RoleProfileStore.Load(changedDirectory);
        results["roleProfilesAutoRechecksDiskMetadataDuringLongRun"] =
            changedDuringFetch && changingRun.SkippedChanged == 1 &&
            RoleProfileStore.FindBySource(changedState, "TMDB", "101") is not null &&
            RoleProfileStore.FindBySource(changedState, "TMDB", "102") is null;

        var batchDirectory = Path.Combine(root, "auto-scrape-batch-change");
        Directory.CreateDirectory(batchDirectory);
        var batchRecords = records.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
        MetadataStore.Save(batchDirectory, batchRecords);
        var batchPreview = RoleProfileAutoScraper.Preview(videos, batchRecords,
            RoleProfileStore.Load(batchDirectory));
        Task<RoleProfileStore.SourceDetails> InvalidateFirstBeforeBatchSave(
            RoleProfileStore.SourceIdentity identity, CancellationToken token)
        {
            if (identity.PersonId == "102")
            {
                batchRecords[firstVideo.Id].PersonCredits.RemoveAll(credit => credit.PersonId == "101");
                batchRecords[secondVideo.Id].PersonCredits.RemoveAll(credit => credit.PersonId == "101");
                MetadataStore.Save(batchDirectory, batchRecords);
            }
            return FakeDetails(identity, token);
        }
        var batchRefused = false;
        try
        {
            _ = Task.Run(() => RoleProfileAutoScraper.RunAsync(batchDirectory,
                batchPreview, "offline-diagnostic-token", fetchDetails: InvalidateFirstBeforeBatchSave))
                .GetAwaiter().GetResult();
        }
        catch (InvalidDataException) { batchRefused = true; }
        results["roleProfilesAutoRejectsChangedPendingBatchBeforeSave"] =
            batchRefused && RoleProfileStore.Load(batchDirectory).Profiles.Count == 0;

        // TVmaze permits a user-authored link. Import should not add a second
        // verified row pointing to the same local video in that profile.
        var manualDirectory = Path.Combine(root, "auto-scrape-manual-work");
        Directory.CreateDirectory(manualDirectory);
        var tvVideo = Video(manualDirectory, "auto-tv", "剧集");
        using var tvJson = JsonDocument.Parse("""
            {"id":7,"name":"Show","_embedded":{"cast":[{"person":{"id":29,"name":"Taylor"},
              "character":{"name":"Protagonist"}}]}}
            """);
        var tvRecord = AdditionalScrapers.ParseTvMazeDetails(new MovieMetadata
        {
            Source = "TVmaze", SourceUrl = "https://www.tvmaze.com/shows/7", Title = "Show"
        }, tvJson.RootElement);
        var tvIdentityValid = RoleProfileStore.TryVerifiedPersonCredit(tvRecord,
            tvRecord.PersonCredits.Single(), out var tvIdentity);
        var tvProfile = new RoleProfileStore.Profile { Name = "Taylor" };
        tvProfile.Sources.Add(tvIdentity);
        tvProfile.Works.Add(new RoleProfileStore.WorkLink
        {
            LocalVideoId = tvVideo.Id, WorkTitle = "用户手工关联的 Show", LinkOrigin = "manual"
        });
        var tvRecords = new Dictionary<string, MovieMetadata>(StringComparer.OrdinalIgnoreCase)
            { [tvVideo.Id] = tvRecord };
        MetadataStore.Save(manualDirectory, tvRecords);
        RoleProfileStore.Save(manualDirectory, new RoleProfileStore.State { Profiles = [tvProfile] });
        var tvPreview = RoleProfileAutoScraper.Preview([tvVideo], tvRecords,
            RoleProfileStore.Load(manualDirectory));
        var tvRun = Task.Run(() => RoleProfileAutoScraper.RunAsync(manualDirectory, tvPreview,
            "", fetchDetails: FakeDetails)).GetAwaiter().GetResult();
        var tvAfter = RoleProfileStore.Load(manualDirectory).Profiles.Single();
        results["roleProfilesAutoKeepsManualSameVideoWithoutDuplicate"] =
            tvIdentityValid && tvPreview.ExistingCount == 1 && tvRun.Created == 0 &&
            tvAfter.Works.Count == 1 && tvAfter.Works[0].LinkOrigin == "manual" &&
            tvAfter.SourcedDetails.Count == 1;

        // The per-profile 20k cap is explicit in preview and refuses the run
        // before touching the 20,000 existing work rows.
        var fullDirectory = Path.Combine(root, "auto-scrape-full-works");
        Directory.CreateDirectory(fullDirectory);
        var fullProfile = new RoleProfileStore.Profile { Name = "Taylor" };
        fullProfile.Sources.Add(tvIdentity);
        for (var index = 0; index < 20000; index++)
            fullProfile.Works.Add(new RoleProfileStore.WorkLink
            {
                LocalVideoId = "prior-" + index, WorkTitle = "已有关联", LinkOrigin = "manual"
            });
        RoleProfileStore.Save(fullDirectory, new RoleProfileStore.State { Profiles = [fullProfile] });
        var fullPreview = RoleProfileAutoScraper.Preview([tvVideo], tvRecords,
            RoleProfileStore.Load(fullDirectory));
        var fullRevision = RoleProfileStore.Load(fullDirectory).Revision;
        var fullRejected = false;
        try
        {
            _ = Task.Run(() => RoleProfileAutoScraper.RunAsync(fullDirectory, fullPreview,
                "", fetchDetails: FakeDetails)).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException) { fullRejected = true; }
        var fullAfter = RoleProfileStore.Load(fullDirectory);
        results["roleProfilesAutoWorkCapExplicitAndAtomic"] =
            fullPreview.CapacityExceeded && fullPreview.WorkCapacityExceededCount == 1 &&
            fullRejected && fullAfter.Profiles.Single().Works.Count == 20000 &&
            fullAfter.Revision == fullRevision;

        // A persistent 429 makes at most one retry for that provider, then
        // stops the provider for this run and keeps its identities/works.
        var throttledDirectory = Path.Combine(root, "auto-scrape-throttled");
        Directory.CreateDirectory(throttledDirectory);
        MetadataStore.Save(throttledDirectory, records);
        var throttledPreview = RoleProfileAutoScraper.Preview(videos, records,
            RoleProfileStore.Load(throttledDirectory));
        var rateCalls = 0;
        Task<RoleProfileStore.SourceDetails> Always429(
            RoleProfileStore.SourceIdentity identity, CancellationToken token)
        {
            rateCalls++;
            throw new RolePersonDetailsFetcher.RateLimitException(identity.Provider, TimeSpan.Zero);
        }
        var throttledRun = Task.Run(() => RoleProfileAutoScraper.RunAsync(throttledDirectory,
            throttledPreview, "offline-diagnostic-token", fetchDetails: Always429))
            .GetAwaiter().GetResult();
        var throttledState = RoleProfileStore.Load(throttledDirectory);
        var retryHeader = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
        results["roleProfilesAuto429StopsProviderAndRespectsRetryAfter"] =
            rateCalls == 2 && throttledRun.SkippedRateLimited == 2 &&
            throttledRun.Fetched == 0 && throttledState.Profiles.Count == 2 &&
            RolePersonDetailsFetcher.RetryAfterDelay(retryHeader,
                DateTimeOffset.UtcNow) == TimeSpan.FromSeconds(17);
        return results;
    }
}

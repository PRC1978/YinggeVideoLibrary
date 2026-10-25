using System.IO;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace YinggeVideoLibrary;

internal static class FeatureDiagnostics
{
    public static async Task<int> RunAsync(string resultPath)
    {
        var findings = new Dictionary<string, object>();
        var credentialName = "SelfTest-" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "YinggeFeatureTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            findings["sphereExactTagCategoryLinks"] = MainWindow.VerifySphereRelationsForDiagnostics();
            findings["graphMovePreviewAndUndo"] = CategoryGraphMovePlan.VerifyForDiagnostics();
            findings["graphMoveDurableHistoryAndConflicts"] = CategoryGraphMoveHistoryStore.VerifyForDiagnostics();
            foreach (var metric in MainWindow.VerifySphereIncrementalIndexForDiagnostics())
                findings[metric.Key] = metric.Value;
            WindowsCredentialStore.Write(credentialName, "temporary-secret-中文");
            findings["windowsCredentialRoundTrip"] = WindowsCredentialStore.Read(credentialName) == "temporary-secret-中文";
            WindowsCredentialStore.Write(credentialName, "");
            findings["windowsCredentialDelete"] = WindowsCredentialStore.Read(credentialName) is null;
            Directory.CreateDirectory(directory);
            foreach (var (name, passed) in MainWindow.VerifyLibraryFormatMigrationForDiagnostics(
                         Path.Combine(directory, "library-format"))) findings[name] = passed;
            try
            {
                foreach (var (name, passed) in MainWindow.VerifyCurrentLibraryCopyForDiagnostics(
                             Path.Combine(directory, "real-library-copy"))) findings[name] = passed;
            }
            catch (Exception ex)
            {
                findings["realLibraryCopyUpgrade"] = false;
                findings["realLibraryCopyUpgradeError"] = ex.ToString();
            }
            foreach (var (name, passed) in OrdinarySourceMatrixDiagnostics.Verify(
                         Path.Combine(directory, "ordinary-source-matrix"))) findings[name] = passed;
            foreach (var (name, passed) in TmdbUsagePolicyDiagnostics.Verify()) findings[name] = passed;
            foreach (var (name, passed) in CrossSourceRelationDiagnostics.Verify()) findings[name] = passed;
            foreach (var (name, passed) in RoleProfileDiagnostics.Verify(directory)) findings[name] = passed;
            foreach (var result in SiteListImportDiagnostics.Run()) findings[result.Key] = result.Value;
            foreach (var result in SiteListLocalActionDiagnostics.Run()) findings[result.Key] = result.Value;
            foreach (var (name, checkPassed) in IndexRootMismatchDiagnostics.Verify(
                Path.Combine(directory, "index-root-mismatch")))
                findings[name] = checkPassed;
            foreach (var check in BackupRestorePreviewDiagnostics.Run(directory))
                findings[check.Key] = check.Value;
            foreach (var check in BackupSelectiveImportDiagnostics.Run(directory))
                findings[check.Key] = check.Value;
            try
            {
                foreach (var check in BackupCrossDeviceDiagnostics.Run(
                             Path.Combine(directory, "cross-device-import")))
                    findings[check.Key] = check.Value;
            }
            catch (Exception ex)
            {
                findings["crossDeviceImportDiagnostics"] = false;
                findings["crossDeviceImportDiagnosticsError"] = ex.ToString();
            }
            foreach (var check in LibraryBackupService.VerifyAiPacingRestoreForDiagnostics(
                Path.Combine(directory, "ai-backup-pacing")))
                findings[check.Key] = check.Value;
            foreach (var check in LibraryRelocationDiagnostics.Verify(
                Path.Combine(directory, "library-relocation")))
                findings[check.Key] = check.Value;
            foreach (var (name, checkPassed) in JavinfoEligibilityDiagnostics.Verify(Path.Combine(directory, "javinfo-eligibility")))
                findings[name] = checkPassed;
            foreach (var (name, checkPassed) in NativeMetadataAggregationDiagnostics.Verify())
                findings[name] = checkPassed;
            foreach (var (name, checkPassed) in NativeMediaFieldsDiagnostics.Verify())
                findings[name] = checkPassed;
            foreach (var (name, checkPassed) in NativeOriginalFieldsDiagnostics.Verify())
                findings[name] = checkPassed;
            findings["codeIdentityKeepsInternalSeparators"] = ScrapeCodeIdentity.VerifyForDiagnostics();
            findings["refreshCannotEraseAcceptedFields"] = MetadataRefreshPolicy.VerifyForDiagnostics();
            foreach (var (name, checkPassed) in JavDbCatalogDiagnostics.Verify())
                findings[name] = checkPassed;
            foreach (var (name, checkPassed) in JavDatabaseCatalogDiagnostics.Verify())
                findings[name] = checkPassed;
            foreach (var (name, checkPassed) in JavDatabaseActorSearchDiagnostics.Verify())
                findings[name] = checkPassed;
            foreach (var (name, checkPassed) in ScrapeRefreshDiagnostics.Verify(
                Path.Combine(directory, "scrape-refresh")))
                findings[name] = checkPassed;
            foreach (var (name, checkPassed) in await NativeScrapeSafetyDiagnostics.VerifyAsync(
                Path.Combine(directory, "native-scrape-safety"))) findings[name] = checkPassed;
            foreach (var (name, checkPassed) in await ScrapeSourceIsolationDiagnostics.VerifyAsync(
                Path.Combine(directory, "scrape-source-isolation"))) findings[name] = checkPassed;
            foreach (var (name, checkPassed) in ScrapeSourcePolicyDiagnostics.Verify(
                Path.Combine(directory, "scrape-source-policy"))) findings[name] = checkPassed;
            findings["sourceHealthExportRedactsPrivateValues"] = SourceHealthDiagnostics.VerifyForDiagnostics();
            foreach (var (name, wikidataPassed) in WikidataScraperDiagnostics.Verify()) findings[name] = wikidataPassed;
            foreach (var (name, omdbPassed) in OmdbScraperDiagnostics.Verify()) findings[name] = omdbPassed;
            foreach (var (name, kitsuPassed) in KitsuScraperDiagnostics.Verify(
                Path.Combine(directory, "kitsu-source"))) findings[name] = kitsuPassed;
            foreach (var (name, tenraiPassed) in TenraiScraperDiagnostics.Verify(
                Path.Combine(directory, "tenrai-source"))) findings[name] = tenraiPassed;
            foreach (var (name, traktPassed) in TraktScraperDiagnostics.Verify()) findings[name] = traktPassed;
            findings["wikidataVerifiedCacheCanBypassCooldown"] = await ScrapeCandidateCache.VerifyWikidataCacheReadyAsync(directory);
            findings["detailCacheBindsSourceUrlAndCode"] = await ScrapeCandidateCache.VerifyDetailIdentityBindingAsync(directory);
            findings["reviewHistoryJsonAndCsvRoundTrip"] = ReviewHistoryExport.VerifyForDiagnostics();
            findings["reviewHistoryCombinedFilters"] = ReviewHistoryQuery.VerifyForDiagnostics();
            foreach (var check in AiSuggestionAudit.VerifyForDiagnostics())
                findings[check.Key] = check.Value;
            foreach (var check in AiAdversarialDiagnostics.Run(Path.Combine(directory, "ai-adversarial")))
                findings[check.Key] = check.Value;
            foreach (var check in ReviewTransactionRecoveryDiagnostics.Run(
                Path.Combine(directory, "review-recovery")))
                findings[check.Key] = check.Value;
            foreach (var check in await ReviewAdversarialDiagnostics.RunAsync())
                findings[check.Key] = check.Value;
            foreach (var check in await AiJobStoreDiagnostics.VerifyAsync(Path.Combine(directory, "ai-jobs")))
                findings[check.Key] = check.Value;
            foreach (var check in await AiBudgetDiagnostics.VerifyAsync(
                Path.Combine(directory, "ai-budget-accounting")))
                findings[check.Key] = check.Value;
            foreach (var check in await AiQueueConcurrencyDiagnostics.VerifyAsync(
                Path.Combine(directory, "ai-queue-concurrency")))
                findings[check.Key] = check.Value;
            findings["personalRatingSharedStore"] = PersonalRatingStore.VerifyForDiagnostics(directory);
            foreach (var (name, passed) in MetadataRatingConcurrencyDiagnostics.Run(
                         Path.Combine(directory, "rating-concurrency"))) findings[name] = passed;
            foreach (var check in await DuplicateVideoDiagnostics.VerifyAsync(Path.Combine(directory, "duplicate-videos")))
                findings[check.Key] = check.Value;
            foreach (var check in await DuplicateMetadataTransactionDiagnostics.VerifyAsync(
                Path.Combine(directory, "duplicate-metadata-transactions"))) findings[check.Key] = check.Value;
            var scoreFromJavDb = RatingAggregation.FromMetadata(new MovieMetadata
            {
                Source = "JavDB", SourceUrl = "https://javdb.com/v/example", Rating = "4.57分"
            });
            var scoreFromJavDatabase = RatingAggregation.FromMetadata(new MovieMetadata
            {
                Source = "JAVDatabase", SourceUrl = "https://www.javdatabase.com/movies/abc-123/", Rating = "4.2/5（123 票）"
            });
            var scoreFromTmdb = RatingAggregation.FromMetadata(new MovieMetadata
            {
                Source = "TMDB 电影", SourceUrl = "https://www.themoviedb.org/movie/1", Rating = "7.6/10（900 票）"
            });
            var scoreFromJavinfoRelay = RatingAggregation.FromMetadata(new MovieMetadata
            {
                Source = "Javinfo · JAVDatabase", SourceUrl = "https://api.javinfo.dev/movie?q=ABC-123", Rating = "4.2/5（123 票）"
            });
            var combinedScore = RatingAggregation.Summarize([scoreFromJavDatabase!, scoreFromTmdb!, scoreFromJavinfoRelay!]);
            findings["platformRatingIdentityAndScale"] = scoreFromJavDb is null
                && scoreFromJavDatabase?.VoteCount == 123 && scoreFromJavDatabase.Maximum == 5
                && combinedScore?.SourceCount == 2 && combinedScore.ScoreOutOfTen is > 7.6 and < 8.4
                && RatingAggregation.Summarize([scoreFromJavDatabase!]) is null;
            var fetches = 0;
            async Task<List<MovieMetadata>> Fetch()
            {
                fetches++;
                await Task.Yield();
                return [new MovieMetadata { Title = "candidate", Source = "test" }];
            }
            var first = await ScrapeCandidateCache.GetOrFetchAsync(directory, "test", "query", Fetch);
            var second = await ScrapeCandidateCache.GetOrFetchAsync(directory, "test", "query", Fetch);
            findings["candidateCacheRoundTrip"] = first.Count == 1 && second.Count == 1 && fetches == 1;
            var hintRoot = Path.Combine(directory, "media");
            var nestedWork = Path.Combine(hintRoot, "演员", "Ruth_Lee", "2024", "Example Title", "Example Title.mp4");
            var stageWork = Path.Combine(hintRoot, "S-熊猫可乐-3.72G", "作品标题.mkv");
            findings["folderPersonContextBoundedAndFiltered"] =
                ScrapeSearchHints.FindPersonHint(nestedWork, [hintRoot]) == "Ruth_Lee"
                && ScrapeSearchHints.FindPersonHint(stageWork, [hintRoot]) == "熊猫可乐"
                && ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "电影", "日本", "1080p", "Title.mp4"), [hintRoot]) is null
                && ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "rootvideo.mp4"), [hintRoot]) is null
                && ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot + "-other", "Ruth_Lee", "Title.mp4"), [hintRoot]) is null;
            findings["folderSingleTokenCreatorNames"] =
                ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "ICICIS", "2024", "Example Title.mp4"), [hintRoot]) == "ICICIS"
                && ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "AsiaXXXTour", "作品", "Example Title.mp4"), [hintRoot]) == "AsiaXXXTour";
            findings["folderSingleTokenGenericWordsRejected"] =
                ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "COSPLAY", "Example Title.mp4"), [hintRoot]) is null
                && ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "TRAVEL", "Example Title.mp4"), [hintRoot]) is null
                && ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "Video", "Example Title.mp4"), [hintRoot]) is null;
            var numberedInWork = Path.Combine(hintRoot, "ICICIS", "作品标题", "子目录", "1.mp4");
            var numberedInNamedWork = Path.Combine(hintRoot, "人物名", "作品标题", "子目录", "1.mp4");
            var numberedUnderCategory = Path.Combine(hintRoot, "电影", "日本", "作品标题", "1.mp4");
            findings["folderHierarchySeparatesPersonAndWork"] =
                ScrapeSearchHints.FindPersonHint(numberedInWork, [hintRoot]) == "ICICIS"
                && ScrapeSearchHints.FindWorkTitleHint(numberedInWork, [hintRoot]) == "作品标题"
                && ScrapeSearchHints.FindPersonHint(numberedInNamedWork, [hintRoot]) == "人物名"
                && ScrapeSearchHints.FindWorkTitleHint(numberedInNamedWork, [hintRoot]) == "作品标题"
                && ScrapeSearchHints.FindWorkTitleHint(Path.Combine(hintRoot, "ICICIS", "作品标题", "Video_0001.mp4"), [hintRoot]) == "作品标题"
                && ScrapeSearchHints.FindPersonHint(Path.Combine(hintRoot, "@月暖如若音", "清晨之梦", "子目录", "1.mp4"), [hintRoot]) == "月暖如若音"
                && ScrapeSearchHints.FindWorkTitleHint(Path.Combine(hintRoot, "@月暖如若音", "清晨之梦", "子目录", "1.mp4"), [hintRoot]) == "清晨之梦";
            findings["folderWorkTitleNeedsReliablePerson"] =
                ScrapeSearchHints.FindWorkTitleHint(numberedUnderCategory, [hintRoot]) is null
                && ScrapeSearchHints.FindWorkTitleHint(Path.Combine(hintRoot, "ICICIS", "作品标题", "1.mp4"), [hintRoot + "-other"]) is null
                && ScrapeSearchHints.FindWorkTitleHint(Path.Combine(hintRoot, "ICICIS", "作品标题", "Proper Name.mp4"), [hintRoot]) is null
                && ScrapeSearchHints.IsWeakTitle("Video_0001")
                && !ScrapeSearchHints.IsWeakTitle("Example Title");
            var indexedFolder = Path.Combine(directory, "indexed", "person", "work");
            var indexedAncestors = MainWindow.BuildKnownVideoDirectorySet([indexedFolder]);
            findings["incrementalIndexIgnoresUnrelatedSidecars"] =
                !MainWindow.IsRelevantIncrementalPath(Path.Combine(indexedFolder, "poster.jpg"), false, indexedAncestors)
                && !MainWindow.IsRelevantIncrementalPath(Path.Combine(indexedFolder, "subtitle.srt"), false, indexedAncestors)
                && MainWindow.IsRelevantIncrementalPath(Path.Combine(indexedFolder, "new.mp4"), false, indexedAncestors);
            findings["incrementalIndexRetainsDirectoryChanges"] =
                indexedAncestors.Contains(Path.Combine(directory, "indexed", "person"))
                && MainWindow.IsRelevantIncrementalPath(Path.Combine(directory, "indexed", "person"), false, indexedAncestors)
                && MainWindow.IsRelevantIncrementalPath(Path.Combine(directory, "indexed", "new-person"), true, indexedAncestors)
                && !MainWindow.ShouldQueueDirectoryChange(WatcherChangeTypes.Changed, true)
                && MainWindow.ShouldQueueDirectoryChange(WatcherChangeTypes.Created, true)
                && MainWindow.ShouldQueueDirectoryChange(WatcherChangeTypes.Deleted, false)
                && MainWindow.ShouldQueueDirectoryChange(WatcherChangeTypes.Renamed, true);
            var queueDirectory = Path.Combine(directory, "scrape-queue");
            var mixedJobs = Enumerable.Range(0, 1001).Select(index => new ScrapeQueueItem
            {
                VideoId = $"video-{index}",
                Kind = index % 2 == 0 ? "code" : "title",
                Code = index % 2 == 0 ? $"ABC-{index}" : "",
                SearchTitle = index % 2 == 0 ? "" : $"测试标题 {index}"
            }).ToArray();
            var addedJobs = ScrapeQueueStore.Add(queueDirectory, mixedJobs);
            var loadedJobs = ScrapeQueueStore.Load(queueDirectory);
            findings["fullScrapeQueueWithoutBatchCap"] = addedJobs == mixedJobs.Length && loadedJobs.Count == mixedJobs.Length
                && loadedJobs.Count(item => item.Kind == "title") == 500;
            ScrapeQueueStore.MarkFailed(queueDirectory, "video-0", "test failure");
            var canceledJobs = ScrapeQueueStore.ClearPending(queueDirectory);
            var retainedJobs = ScrapeQueueStore.Load(queueDirectory);
            findings["clearPendingPreservesFailures"] = canceledJobs == 1000 && retainedJobs.Count == 1
                && retainedJobs[0].Failed && retainedJobs[0].VideoId == "video-0";
            var legacyQueueDirectory = Path.Combine(directory, "legacy-scrape-queue");
            Directory.CreateDirectory(legacyQueueDirectory);
            File.WriteAllText(Path.Combine(legacyQueueDirectory, "scrape-queue.json"),
                "[{\"VideoId\":\"legacy\",\"Code\":\"ABC-123\"}]");
            var legacyJobs = ScrapeQueueStore.Load(legacyQueueDirectory);
            findings["legacyCodeQueueCompatibility"] = legacyJobs.Count == 1 && legacyJobs[0].Kind == "code"
                && legacyJobs[0].DisplayQuery == "ABC-123";
            ScrapeQueueStore.CheckpointForDiagnostics(legacyQueueDirectory);
            findings["legacyQueueCheckpoint"] = ScrapeQueueStore.Load(legacyQueueDirectory).Count == 1;
            var legacyBackupDirectory = Path.Combine(directory, "legacy-backup-queue");
            Directory.CreateDirectory(legacyBackupDirectory);
            File.WriteAllText(Path.Combine(legacyBackupDirectory, "scrape-queue.json"), "{corrupt");
            File.WriteAllText(Path.Combine(legacyBackupDirectory, "scrape-queue.json.bak"),
                "[{\"VideoId\":\"backup\",\"Code\":\"ABC-456\"}]");
            findings["legacyQueueBackupRecovery"] = ScrapeQueueStore.Load(legacyBackupDirectory).SingleOrDefault()?.VideoId == "backup";
            var incrementalQueueDirectory = Path.Combine(directory, "incremental-scrape-queue");
            ScrapeQueueStore.Add(incrementalQueueDirectory, mixedJobs);
            var journalPath = Path.Combine(incrementalQueueDirectory, "scrape-queue.journal.00000000.bin");
            var initialJournalBytes = new FileInfo(journalPath).Length;
            for (var index = 0; index < 100; index++)
                ScrapeQueueStore.Remove(incrementalQueueDirectory, $"video-{index}");
            var changedJournalBytes = new FileInfo(journalPath).Length - initialJournalBytes;
            findings["incrementalQueueWrites"] = changedJournalBytes < 25_000
                && ScrapeQueueStore.Load(incrementalQueueDirectory).Count == 901;
            var retryItem = new ScrapeQueueItem { VideoId = "video-100", Kind = "code", Code = "ABC-100",
                Attempts = 2, NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1), LastError = "temporary" };
            ScrapeQueueStore.Reschedule(incrementalQueueDirectory, retryItem);
            ScrapeQueueStore.MarkFailed(incrementalQueueDirectory, "video-101", "failed");
            var nextDelay = DateTimeOffset.UtcNow.AddMinutes(5);
            ScrapeQueueStore.DelayAllUntil(incrementalQueueDirectory, nextDelay);
            var queueStatus = ScrapeQueueStore.GetStatus(incrementalQueueDirectory);
            var queuedNext = ScrapeQueueStore.PeekNext(incrementalQueueDirectory, false);
            findings["incrementalQueueRetryAndDelay"] = queueStatus.PendingCount == 900 && queueStatus.FailedCount == 1
                && queuedNext.Item?.Kind == "title" && queuedNext.Item.NextAttemptAt >= nextDelay
                && !ScrapeQueueStore.HasPending(incrementalQueueDirectory, "video-101");
            ScrapeQueueStore.CheckpointForDiagnostics(incrementalQueueDirectory);
            File.WriteAllText(Path.Combine(incrementalQueueDirectory, "scrape-queue.state.json"), "{corrupt");
            findings["incrementalQueueManifestRecovery"] = ScrapeQueueStore.Load(incrementalQueueDirectory).Count == 901;
            var rotatedJournal = Path.Combine(incrementalQueueDirectory, "scrape-queue.journal.00000001.bin");
            using (var partial = new FileStream(rotatedJournal, FileMode.Append, FileAccess.Write, FileShare.Read))
                partial.Write([0x17, 0x00]);
            var beforeRepair = ScrapeQueueStore.Load(incrementalQueueDirectory).Count;
            ScrapeQueueStore.Remove(incrementalQueueDirectory, "video-102");
            findings["incrementalQueueCrashRecovery"] = beforeRepair == 901
                && ScrapeQueueStore.Load(incrementalQueueDirectory).Count == 900;
            findings["incrementalQueueRetryFailures"] = ScrapeQueueStore.RetryFailures(incrementalQueueDirectory) == 1
                && ScrapeQueueStore.HasPending(incrementalQueueDirectory, "video-101");
            using (var tmdb = JsonDocument.Parse("""{"id":1,"title":"示例影片","original_title":"Example","release_date":"2025-06-01","runtime":112,"overview":"剧情介绍","genres":[{"name":"剧情"}],"production_companies":[{"name":"示例片厂"}],"vote_average":7.5,"vote_count":10,"credits":{"cast":[{"name":"演员甲"}],"crew":[{"job":"Director","name":"导演乙"}]},"keywords":{"keywords":[{"name":"关键词"}]}}"""))
            {
                var candidate = new MovieMetadata { Title = "示例影片", Source = "TMDB 电影", SourceUrl = "https://www.themoviedb.org/movie/1" };
                candidate.StampAvailableFields("搜索结果");
                var full = AdditionalScrapers.ParseTmdbDetails(candidate, tmdb.RootElement, "movie");
                var review = new AiReviewRecord { CandidateTitle = full.Title, CandidateSource = full.Source, CandidateDetails = full };
                var restored = JsonSerializer.Deserialize<AiReviewRecord>(JsonSerializer.Serialize(review))!.ToMetadata();
                findings["tmdbFullMetadataReviewRoundTrip"] = restored.DetailFetched && restored.DetailIdentityVerified && restored.RuntimeMinutes == 112 && restored.Studio == "示例片厂"
                    && restored.Actors.SequenceEqual(["演员甲"]) && restored.Director == "导演乙" && restored.Keywords.SequenceEqual(["关键词"])
                    && restored.PlatformRatings.Count == 1 && restored.PlatformRatings[0].Platform == "TMDB"
                    && restored.PlatformRatings[0].VoteCount == 10;
                var independent = restored.Clone();
                independent.FieldProvenance[nameof(MovieMetadata.Actors)].Stage = "changed-in-clone";
                findings["metadataFieldProvenance"] = candidate.FieldProvenance[nameof(MovieMetadata.Title)].Stage == "搜索结果"
                    && restored.FieldProvenance[nameof(MovieMetadata.Actors)].Stage == "详情接口"
                    && restored.FieldProvenance[nameof(MovieMetadata.Actors)].SourceUrl == candidate.SourceUrl
                    && restored.FieldProvenance[nameof(MovieMetadata.Title)].Stage == "详情接口"
                    && restored.FieldProvenance[nameof(MovieMetadata.Studio)].Source == "TMDB 电影"
                    && !restored.FieldProvenance.ContainsKey(nameof(MovieMetadata.Series))
                    && restored.FieldProvenance[nameof(MovieMetadata.Actors)].Stage != independent.FieldProvenance[nameof(MovieMetadata.Actors)].Stage;
                var detailFetches = 0;
                async Task<List<MovieMetadata>> FetchDetails() { detailFetches++; await Task.Yield(); return [full]; }
                var firstDetails = await ScrapeCandidateCache.GetOrFetchAsync(directory, "TMDB 详情", "https://www.themoviedb.org/movie/1", FetchDetails);
                var secondDetails = await ScrapeCandidateCache.GetOrFetchAsync(directory, "TMDB 详情", "https://www.themoviedb.org/movie/1", FetchDetails);
                findings["fullMetadataCandidateCache"] = detailFetches == 1 && firstDetails.Count == 1 && secondDetails.Count == 1
                    && secondDetails[0].Director == "导演乙" && secondDetails[0].Actors.SequenceEqual(["演员甲"]);
                var exactOfficial = ScrapeIdentityPolicy.Apply("示例影片 (2025)", restored, 100, "approve", "AI 极高可信");
                var fuzzyOfficial = ScrapeIdentityPolicy.Apply("示例影片 特别版", restored, 100, "approve", "AI 极高可信");
                findings["verifiedOfficialExactTitleGate"] = exactOfficial.Verdict == "approve" && exactOfficial.Confidence == 100
                    && fuzzyOfficial.Verdict == "review" && fuzzyOfficial.Confidence == 79;
                var differentYear = restored.Clone();
                differentYear.Title = "示例影片";
                differentYear.ReleaseDate = "2024-06-01";
                var yearConflict = ScrapeIdentityPolicy.Apply("示例影片.2025.mp4", differentYear, 100, "approve", "AI 极高可信");
                var cjkYearConflict = ScrapeIdentityPolicy.Apply("示例影片2025年", differentYear, 100, "approve", "AI 极高可信");
                differentYear.ReleaseDate = "2025-06-01";
                var matchingYear = ScrapeIdentityPolicy.Apply("示例影片.2025.mp4", differentYear, 100, "approve", "AI 极高可信");
                findings["explicitFileYearMustMatchSourceYear"] = yearConflict.Verdict == "review" && yearConflict.Confidence == 79
                    && yearConflict.Reason.Contains("年份冲突") && cjkYearConflict.Reason.Contains("年份冲突")
                    && matchingYear.Verdict == "approve" && matchingYear.Confidence == 100;
                var unknownVerdict = ScrapeIdentityPolicy.Apply("示例影片 (2025)", restored, 95, "uncertain", "AI 不确定");
                findings["unknownAiVerdictRequiresReview"] = unknownVerdict.Confidence == 95
                    && MainWindow.ReviewStatusFor(unknownVerdict.Confidence, unknownVerdict.Verdict) == "pending"
                    && MainWindow.ReviewStatusFor(95, "") == "pending"
                    && MainWindow.ReviewStatusFor(95, "approved") == "approved"
                    && MainWindow.ReviewStatusFor(95, "reject") == "rejected";
                using var wrongId = JsonDocument.Parse("""{"id":2,"title":"错误作品"}""");
                try { AdditionalScrapers.ParseTmdbDetails(candidate, wrongId.RootElement, "movie"); findings["tmdbRejectsDetailIdentityConflict"] = false; }
                catch (InvalidDataException) { findings["tmdbRejectsDetailIdentityConflict"] = true; }
            }
            using (var tvmaze = JsonDocument.Parse("""{"id":123,"name":"剧集示例","premiered":"2024-01-01","summary":"<p>第一季</p>","genres":["Comedy"],"averageRuntime":42,"rating":{"average":8.2},"network":{"name":"示例台","country":{"name":"英国"}},"_embedded":{"cast":[{"person":{"name":"演员丙"}}],"crew":[{"type":"Director","person":{"name":"导演丁"}}]}}"""))
            {
                var full = AdditionalScrapers.ParseTvMazeDetails(new MovieMetadata { Source = "TVmaze", SourceUrl = "https://www.tvmaze.com/shows/123" }, tvmaze.RootElement);
                findings["tvMazeFullMetadata"] = full.DetailFetched && full.DetailIdentityVerified && full.RuntimeMinutes == 42 && full.Summary == "第一季"
                    && full.Studio == "示例台" && full.Country == "英国" && full.Actors.SequenceEqual(["演员丙"]);
                using var wrongId = JsonDocument.Parse("""{"id":124,"name":"错误剧集"}""");
                try { AdditionalScrapers.ParseTvMazeDetails(new MovieMetadata { Source = "TVmaze", SourceUrl = "https://www.tvmaze.com/shows/123" }, wrongId.RootElement); findings["tvMazeRejectsDetailIdentityConflict"] = false; }
                catch (InvalidDataException) { findings["tvMazeRejectsDetailIdentityConflict"] = true; }
            }
            var javdb = JavDbScraper.ParseJavDbDetails(new MovieMetadata { Code = "ABC-123", Source = "JavDB", SourceUrl = "https://javdb.com/v/sample" },
                "<div class='panel-block'><strong>發行日期:</strong><span class='value'>2025-02-03</span></div>" +
                "<div class='panel-block'><strong>時長:</strong><span class='value'>120 分鐘</span></div>" +
                "<div class='panel-block'><strong>演員:</strong><span class='value'><a>演員戊</a></span></div>");
            findings["javDbDetailMetadata"] = javdb.DetailFetched && javdb.ReleaseDate == "2025-02-03" && javdb.RuntimeMinutes == 120 && javdb.Actors.SequenceEqual(["演員戊"])
                && javdb.FieldProvenance[nameof(MovieMetadata.Actors)].Source == "JavDB"
                && javdb.FieldProvenance[nameof(MovieMetadata.Actors)].Stage == "详情页";
            var linkedJavDb = JavDbScraper.ParseJavDbDetails(new MovieMetadata
            {
                Code = "ABC-123", Source = "JavDB", SourceUrl = "https://javdb.com/v/sample"
            }, "<div class='panel-block'><strong>番號:</strong><span class='value'>ABC-123</span></div>" +
               "<div class='panel-block'><strong>演員:</strong><span class='value'><a href='/actors/0R1n3'>甲</a></span></div>" +
               "<div class='panel-block'><strong>類別:</strong><span class='value'><a href='/tags?c7=11'>乙</a></span></div>" +
               "<div class='panel-block'><strong>系列:</strong><span class='value'><a href='/series/P899'>丙</a></span></div>");
            findings["javDbCatalogLinksFromVerifiedDetail"] = linkedJavDb.CatalogLinks.Count == 3
                && linkedJavDb.CatalogLinks.Any(link => link.Kind == "演员" && link.Url == "https://javdb.com/actors/0R1n3")
                && linkedJavDb.CatalogLinks.Any(link => link.Kind == "标签" && link.Url == "https://javdb.com/tags?c7=11")
                && linkedJavDb.CatalogLinks.Any(link => link.Kind == "系列" && link.Url == "https://javdb.com/series/P899");
            var exactCode = ScrapeIdentityPolicy.Apply("ABC-123 作品.mp4", linkedJavDb, 100, "approve", "AI 声称已通过");
            var codeConflict = ScrapeIdentityPolicy.Apply("ABC-124 作品.mp4", linkedJavDb, 100, "approve", "AI 声称已通过");
            var noLocalCode = ScrapeIdentityPolicy.Apply("作品名称.mp4", linkedJavDb, 100, "approve", "AI 声称已通过");
            var noCodeOnDetail = ScrapeIdentityPolicy.Apply("ABC-123 作品.mp4", javdb, 100, "approve", "AI 声称已通过");
            var searchCard = linkedJavDb.Clone();
            searchCard.DetailFetched = false;
            var noDetail = ScrapeIdentityPolicy.Apply("ABC-123 作品.mp4", searchCard, 100, "approve", "AI 声称已通过");
            findings["aiCannotOverrideIdentityEvidence"] = linkedJavDb.DetailIdentityVerified && !javdb.DetailIdentityVerified
                && exactCode.Verdict == "approve" && exactCode.Confidence == 100
                && codeConflict.Verdict == "review" && codeConflict.Confidence == 79 && codeConflict.Reason.Contains("番号冲突")
                && noLocalCode.Verdict == "review" && noLocalCode.Confidence == 79
                && noCodeOnDetail.Verdict == "review" && noCodeOnDetail.Confidence == 79
                && noDetail.Verdict == "review" && noDetail.Confidence == 79;
            findings["legacyIdentityEvidenceFailsClosed"] = !JsonSerializer.Deserialize<MovieMetadata>(
                """{"Code":"ABC-123","Title":"作品","DetailFetched":true}""")!.DetailIdentityVerified;
            findings["javDbCatalogLinkValidation"] = !JavDbScraper.TryCatalogUrl("https://javdb.com.evil.test/actors/0R1n3", out _, out _)
                && !JavDbScraper.TryCatalogUrl("/tags/11", out _, out _)
                && !JavDbScraper.TryCatalogUrl("/tags?c7=11&script=x", out _, out _)
                && JavDbScraper.TryCatalogUrl("/tags?c7=11", out _, out _);
            var catalogCards = JavDbScraper.ParseCatalogPage("<a class='box card' href='/v/abc123'><img title=\"作品甲\" src=\"https://c0.jdbstatic.com/a.jpg\"><strong>ABC-123</strong></a>" +
                "<a href=\"/v/def456\" class=\"box\"><img title=\"作品乙\" src=\"https://c0.jdbstatic.com/b.jpg\"><strong>DEF-456</strong></a>");
            bool catalogRateLimitThrows;
            try { JavDbScraper.ParseCatalogPage("<html><body>操作過於頻繁，請等一会再試</body></html>"); catalogRateLimitThrows = false; }
            catch (InvalidOperationException) { catalogRateLimitThrows = true; }
            findings["javDbCatalogCardsAndRateLimit"] = catalogCards.Count == 2
                && catalogCards[0].Code == "ABC-123" && catalogCards[1].Code == "DEF-456"
                && catalogRateLimitThrows;
            using (var bangumi = JsonDocument.Parse("""{"data":[{"id":123,"type":2,"name":"Example","name_cn":"示例动画","date":"2025-01-01"}]}"""))
            using (var bangumiDetail = JsonDocument.Parse("""{"id":123,"type":2,"name":"Example","name_cn":"示例动画","date":"2025-01-01","infobox":[{"key":"导演","value":"导演甲"},{"key":"动画制作","value":"工作室乙"}]}"""))
            {
                var found = BangumiScraper.ParseSearchResults(bangumi.RootElement);
                var full = BangumiScraper.ParseDetail(found.Single(), bangumiDetail.RootElement);
                findings["bangumiCandidateToFullDetail"] = full.DetailFetched && full.Title == "示例动画"
                    && full.Director == "导演甲" && full.Studio == "工作室乙"
                    && full.FieldProvenance[nameof(MovieMetadata.Director)].Source == "Bangumi";
            }
            using (var aniListSearch = JsonDocument.Parse("""{"data":{"Page":{"media":[{"id":123,"title":{"romaji":"Example Anime","english":"Example Anime","native":"例のアニメ"},"startDate":{"year":2025,"month":4,"day":3},"coverImage":{"large":"https://s4.anilist.co/example.jpg"},"genres":["Adventure"]}]}}}"""))
            using (var aniListDetail = JsonDocument.Parse("""{"data":{"Media":{"id":123,"title":{"romaji":"Example Anime","english":"Example Anime","native":"例のアニメ"},"description":"A <b>new</b> story","startDate":{"year":2025,"month":4,"day":3},"duration":24,"episodes":12,"countryOfOrigin":"JP","format":"TV","genres":["Adventure"],"averageScore":81,"tags":[{"name":"Journey","rank":85,"isGeneralSpoiler":false,"isMediaSpoiler":false},{"name":"Ending spoiler","rank":90,"isGeneralSpoiler":false,"isMediaSpoiler":true}],"coverImage":{"large":"https://s4.anilist.co/example.jpg"},"studios":{"edges":[{"isMain":true,"node":{"name":"Studio A"}}]},"staff":{"edges":[{"role":"Director","node":{"name":{"full":"Director B"}}}]},"characters":{"edges":[{"voiceActors":[{"name":{"full":"Voice C"}}]}]}}}}"""))
            {
                var found = AniListScraper.ParseSearchResults(aniListSearch.RootElement);
                var full = AniListScraper.ParseDetail(found.Single(), aniListDetail.RootElement);
                findings["aniListSearchToFullMetadata"] = full.DetailFetched && full.Source == "AniList"
                    && full.SourceUrl == "https://anilist.co/anime/123" && full.ReleaseDate == "2025-04-03"
                    && full.RuntimeMinutes == 24 && full.Studio == "Studio A" && full.Director == "Director B"
                    && full.Actors.SequenceEqual(["Voice C"]) && full.Keywords.SequenceEqual(["Journey"])
                    && full.Summary == "A new story" && full.Rating == "8.1/10"
                    && full.FieldProvenance[nameof(MovieMetadata.Director)].Stage == "详情接口"
                    && full.PlatformRatings.Single().Platform == "AniList"
                    && Math.Abs(full.PlatformRatings.Single().Value - 8.1) < 0.01;
                findings["aniListCandidateUrlValidation"] = AniListScraper.TryMediaId(full.SourceUrl, out var id) && id == 123
                    && !AniListScraper.TryMediaId("https://anilist.co.evil.test/anime/123", out _)
                    && !AniListScraper.TryMediaId("http://anilist.co/anime/123", out _);
            }
            var javDbTitle = JavDbScraper.ParseTitleSearch("<a href=\"/v/sample\" class=\"box\"><img title=\"作品甲\" src=\"https://c0.jdbstatic.com/a.jpg\"><strong>ABC-123</strong></a>");
            findings["javDbTitleCandidate"] = javDbTitle.Count == 1 && javDbTitle[0].Code == "ABC-123"
                && javDbTitle[0].SourceUrl == "https://javdb.com/v/sample" && !javDbTitle[0].DetailFetched;
            var javDatabase = JavDatabaseScraper.ParseDetail(new MovieMetadata
            {
                Code = "ABC-123", Title = "ABC-123", Source = "JAVDatabase", SourceUrl = "https://www.javdatabase.com/movies/abc-123/"
            }, "<html><body>" + new string(' ', 500) +
               "<h1>ABC-123 Example</h1><p class='mb-1'><b>DVD ID</b>ABC-123</p>" +
               "<p class='mb-1'><b>Release Date</b>2025-03-01</p><p class='mb-1'><b>Runtime</b>120 min</p></body></html>");
            findings["javDatabaseExactCodeDetail"] = javDatabase.DetailFetched && javDatabase.ReleaseDate == "2025-03-01"
                && javDatabase.RuntimeMinutes == 120 && javDatabase.Title == "Example";
            var ratedJavDatabase = JavDatabaseScraper.ParseDetail(new MovieMetadata
            {
                Code = "ABC-123", Title = "ABC-123", Source = "JAVDatabase", SourceUrl = "https://www.javdatabase.com/movies/abc-123/"
            }, "<html><body>" + new string(' ', 500) +
               "<h1>ABC-123 Example</h1><p class='mb-1'><b>DVD ID</b>ABC-123</p>" +
               "<div class='wp-postratings'><span>4.2 / 5 from 123 votes</span></div></body></html>");
            findings["javDatabaseExplicitRatingAndVotes"] = ratedJavDatabase.Rating == "4.2/5（123 票）"
                && ratedJavDatabase.PlatformRatings.Single().Platform == "JAVDatabase"
                && ratedJavDatabase.PlatformRatings.Single().VoteCount == 123;
            var javDatabaseTitle = JavDatabaseScraper.ParseTitleSearch("<html><body>" + new string(' ', 500) +
                "<div class='borderlesscard card extra'><p class='pcard'><a href='/movies/abc-123/'><b>ABC-123</b></a></p>" +
                "<div class='mt-auto'><a href='/movies/abc-123/'>A &amp; B</a></div>" +
                "<span class='btn-primary'><a>Example Studio</a></span><p>2024-01-02</p></div></body></html>");
            findings["javDatabaseTitleCandidate"] = javDatabaseTitle.Count == 1 && javDatabaseTitle[0].Code == "ABC-123"
                && javDatabaseTitle[0].Title == "A & B" && javDatabaseTitle[0].ReleaseDate == "2024-01-02"
                && javDatabaseTitle[0].Studio == "Example Studio" && !javDatabaseTitle[0].DetailFetched
                && javDatabaseTitle[0].SourceUrl == "https://www.javdatabase.com/movies/abc-123/";
            using (var javinfoSearch = JsonDocument.Parse("""{"q":"ABC-123","source":"javdatabase","count":2,"results":[{"id":"abc00123","dvdId":"ABC-123","title":"Example Film","cover":"https://www.javdatabase.com/covers/a.jpg","releaseDate":"2024-01-02","extra":{"studio":"Example Studio","url":"https://www.javdatabase.com/movies/abc-123/"}},{"id":"abc00124","dvdId":"ABC-124","title":"Second Film"}]}"""))
            using (var javinfoDetail = JsonDocument.Parse("""{"q":"ABC-123","source":"fanza","result":{"dvdId":"ABC-123","titleEn":"Example Film Full","titleJa":"作品原标题","commentEn":"Complete description","runtimeMins":120,"releaseDate":"2024-01-02T00:00:00.000Z","makers":["Studio A"],"series":"Series B","categories":["Drama","Portrait"],"actresses":["Actor C"],"actors":["Actor D"],"directors":["Director E"],"jacketFullUrl":"https://pics.dmm.co.jp/example.jpg","extra":{"sampleUrl":"https://cdn.javinfo.dev/paid-preview.mp4"}}}"""))
            {
                var found = JavinfoScraper.ParseSearchResults(javinfoSearch.RootElement);
                var full = JavinfoScraper.ParseMovieDetails(found[0], javinfoDetail.RootElement);
                findings["javinfoOptInCandidateAndFullMetadata"] = found.Count == 2 && !found[0].DetailFetched
                    && found[0].SourceUrl == "https://www.javdatabase.com/movies/abc-123/"
                    && full.DetailFetched && full.Code == "ABC-123" && full.Title == "Example Film Full"
                    && full.OriginalTitle == "作品原标题" && full.Summary == "Complete description"
                    && full.RuntimeMinutes == 120 && full.Studio == "Studio A" && full.Series == "Series B"
                    && full.Genres.SequenceEqual(["Drama", "Portrait"])
                    && full.Actors.SequenceEqual(["Actor C", "Actor D"]) && full.Director == "Director E"
                    && full.FieldProvenance[nameof(MovieMetadata.Actors)].Stage == "详情接口"
                    && full.SourceUrl == "https://api.javinfo.dev/movie?q=ABC-123"
                    && full.FieldProvenance[nameof(MovieMetadata.Actors)].SourceUrl == full.SourceUrl
                    && full.FieldProvenance[nameof(MovieMetadata.ReleaseDate)].SourceUrl == found[0].SourceUrl
                    && !JsonSerializer.Serialize(full).Contains("sampleUrl", StringComparison.Ordinal);
                try
                {
                    JavinfoScraper.ParseMovieDetails(new MovieMetadata { Code = "ABC-999", Source = "Javinfo" }, javinfoDetail.RootElement);
                    findings["javinfoRejectsMismatchedNumber"] = false;
                }
                catch (InvalidDataException) { findings["javinfoRejectsMismatchedNumber"] = true; }
            }
            var paidBudgetDir = Path.Combine(directory, "javinfo-budget");
            var budgetDay = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
            var firstWait = JavinfoUsageLedger.Reserve(paidBudgetDir, 2, budgetDay);
            var secondWait = JavinfoUsageLedger.Reserve(paidBudgetDir, 2, budgetDay);
            var stoppedAtLimit = false;
            try { JavinfoUsageLedger.Reserve(paidBudgetDir, 2, budgetDay); }
            catch (InvalidOperationException) { stoppedAtLimit = true; }
            var budgetUsed = JavinfoUsageLedger.ReadForDiagnostics(paidBudgetDir);
            JavinfoUsageLedger.Reserve(paidBudgetDir, 2, budgetDay.AddDays(1));
            var nextDayUsed = JavinfoUsageLedger.ReadForDiagnostics(paidBudgetDir);
            findings["javinfoDurableDailyPaidLimit"] = firstWait == TimeSpan.Zero && secondWait >= TimeSpan.FromSeconds(13)
                && stoppedAtLimit && budgetUsed.reservedCalls == 2 && nextDayUsed.reservedCalls == 1;
            File.WriteAllText(Path.Combine(paidBudgetDir, "javinfo-usage.json"), "{broken");
            try { JavinfoUsageLedger.Reserve(paidBudgetDir, 2, budgetDay.AddDays(2)); findings["javinfoCorruptLedgerFailsClosed"] = false; }
            catch (JsonException) { findings["javinfoCorruptLedgerFailsClosed"] = true; }
            var javDatabaseRich = JavDatabaseScraper.ParseDetail(javDatabaseTitle[0], "<html><body>" + new string(' ', 500) +
                "<h1>ABC-123 A &amp; B</h1><div id='poster-container'><div><img data-src='/images/cover.jpg'></div></div>" +
                "<p><strong>DVD ID:</strong> ABC-123</p><p><b>Released:</b> 2024-01-02 <b>Runtime:</b> 2 hours 10 minutes</p>" +
                "<p><b>Studio:</b> <a>Long Studio</a></p><p><b>Director:</b> <span>Director A</span></p>" +
                "<p><b>Series:</b> Collection</p><p><b>Genre(s):</b> <a>Drama</a>, <a>Comedy</a></p>" +
                "<p><b>Idol(s)/Actress(es):</b> <a>Actor A</a></p>" +
                "<h2><span>About</span> ABC-123 JAV Movie</h2><p>First paragraph.</p><p>Second paragraph.</p>" +
                "<h2>Related movies</h2><p>Not part of summary.</p></body></html>");
            findings["javDatabaseRichDetail"] = javDatabaseRich.DetailFetched && javDatabaseRich.RuntimeMinutes == 130
                && javDatabaseRich.Studio == "Long Studio" && javDatabaseRich.Director == "Director A"
                && javDatabaseRich.Series == "Collection" && javDatabaseRich.Genres.SequenceEqual(["Drama", "Comedy"])
                && javDatabaseRich.Actors.SequenceEqual(["Actor A"])
                && javDatabaseRich.Summary == "First paragraph.\n\nSecond paragraph."
                && javDatabaseRich.PosterUrl == "https://www.javdatabase.com/images/cover.jpg";
            var recommendationSamples = new[]
            {
                new RecommendationSnapshot("a", true, "已看", DateTime.Today.AddDays(-1), ["动漫"], ["个人私密标签"]),
                new RecommendationSnapshot("b", false, "未看", DateTime.Today, ["动漫"], ["冒险"])
            };
            var recommendationTopics = RecommendationEngine.BuildBilibiliTopics(recommendationSamples);
            findings["recommendationLocalAndOnlinePrivacy"] = RecommendationEngine.Rank(recommendationSamples, 2).Count == 2
                && recommendationTopics.Any(topic => topic.Keyword == "动画推荐")
                && recommendationTopics.All(topic => !topic.Keyword.Contains("个人私密标签", StringComparison.Ordinal));
            var distinctRecommendations = new[]
            {
                new RecommendationSnapshot("favorite", true, "已看", DateTime.Today, ["私人收藏"], ["仅我知道的人名"]),
                new RecommendationSnapshot("animation-1", false, "未看", DateTime.Today, ["动画"], []),
                new RecommendationSnapshot("animation-2", false, "未看", DateTime.Today, ["动画"], []),
                new RecommendationSnapshot("animation-3", false, "未看", DateTime.Today, ["动画"], []),
                new RecommendationSnapshot("documentary", false, "未看", DateTime.Today, ["纪录片"], [])
            };
            var allRecommendations = RecommendationEngine.Rank(distinctRecommendations, distinctRecommendations.Length);
            var firstTwo = allRecommendations.Take(2).Select(item => item.VideoId).ToArray();
            findings["recommendationNoSelfReinforcement"] = allRecommendations.Single(item => item.VideoId == "favorite").Reason == "你收藏的视频";
            findings["recommendationDiverseAndComplete"] = firstTwo.Contains("documentary")
                && firstTwo.Any(id => id.StartsWith("animation-", StringComparison.Ordinal))
                && allRecommendations.Select(item => item.VideoId).Distinct(StringComparer.Ordinal).Count() == distinctRecommendations.Length
                && RecommendationEngine.Rank(distinctRecommendations, 2).Select(item => item.VideoId).SequenceEqual(firstTwo);
            findings["recommendationPrivateLabelsNeverSent"] = RecommendationEngine.BuildBilibiliTopics(distinctRecommendations)
                .All(topic => !topic.Keyword.Contains("仅我知道", StringComparison.Ordinal)
                    && !topic.Keyword.Contains("私人收藏", StringComparison.Ordinal));
            var onlinePreference = Enumerable.Range(0, 100)
                .Select(index => new RecommendationSnapshot($"unwatched-{index}", false, "未看", DateTime.Today, ["电影"], []))
                .Append(new RecommendationSnapshot("favorite-animation", true, "已看", DateTime.Today, ["动画"], []))
                .ToArray();
            findings["recommendationOnlinePrefersObservedInterest"] =
                RecommendationEngine.BuildBilibiliTopics(onlinePreference)[0].Keyword == "动画推荐";
            foreach (var (name, checkPassed) in RecommendationPreferenceDiagnostics.Verify())
                findings[name] = checkPassed;
            var searchFields = new MovieMetadata { Title = "剧集", Source = "TVmaze", Genres = ["喜剧"], Summary = "搜索摘要" };
            searchFields.StampAvailableFields("搜索结果");
            var refreshedFields = searchFields.Clone();
            refreshedFields.Genres = [];
            refreshedFields.Summary = "详情简介";
            refreshedFields.RefreshChangedFieldProvenance(searchFields, "详情接口");
            findings["metadataFieldProvenanceStaleField"] = !refreshedFields.FieldProvenance.ContainsKey(nameof(MovieMetadata.Genres))
                && refreshedFields.FieldProvenance[nameof(MovieMetadata.Title)].Stage == "搜索结果"
                && refreshedFields.FieldProvenance[nameof(MovieMetadata.Summary)].Stage == "详情接口";
            var legacyMetadata = JsonSerializer.Deserialize<MovieMetadata>("{\"Title\":\"旧资料\",\"Source\":\"JavDB\"}");
            findings["legacyMetadataWithoutFieldProvenance"] = legacyMetadata?.FieldProvenance is { Count: 0 };
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var received = 0;
                var server = Task.Run(async () =>
                {
                    for (var i = 0; i < 2; i++)
                    {
                        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(12));
                        using var stream = client.GetStream();
                        var header = new List<byte>();
                        var buffer = new byte[1];
                        while (header.Count < 16384)
                        {
                            if (await stream.ReadAsync(buffer) != 1) break;
                            header.Add(buffer[0]);
                            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                        }
                        var text = Encoding.ASCII.GetString(header.ToArray());
                        var lengthLine = text.Split("\r\n").FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                        var remaining = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim());
                        while (remaining > 0)
                        {
                            var count = await stream.ReadAsync(new byte[Math.Min(remaining, 4096)]);
                            if (count == 0) break;
                            remaining -= count;
                        }
                        received++;
                        var body = "{\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":3}}";
                        var response = i == 0 ? "HTTP/1.1 429 Too Many Requests\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                            : $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
                    }
                });
                var local = new LibraryConfig { AiEndpoint = $"http://127.0.0.1:{port}/v1", AiDailyBudgetUsd = 1, AiInputUsdPerMillion = 1, AiOutputUsdPerMillion = 4, AiRequestIntervalMs = 500 };
                using var answer = await AiRequestQueue.SendAsync(local, local.AiEndpoint + "/chat/completions", new { model = "local", messages = new[] { new { role = "user", content = "test" } }, max_tokens = 20 }, directory);
                await server;
                findings["localAiRetry"] = received == 2 && answer.RootElement.TryGetProperty("choices", out _);
                findings["aiBudgetPersisted"] = File.Exists(Path.Combine(directory, "ai-budget.json"));
                var aiLedger = AiRequestQueue.LoadBudgetLedger(directory);
                findings["aiRetriesReserveEachAttemptAndReconcileTokens"] =
                    aiLedger.AttemptCount == 2 && aiLedger.ReportedUsageCount == 1
                    && aiLedger.InputTokens == 9 && aiLedger.OutputTokens == 3
                    && aiLedger.UnverifiedUsd > 0 && aiLedger.ReportedUsageUsd > 0
                    && aiLedger.EstimatedUsd >= aiLedger.UnverifiedUsd;
                local.AiDailyBudgetUsd = 0.0001;
                try
                {
                    using var unexpected = await AiRequestQueue.SendAsync(local,
                        local.AiEndpoint + "/chat/completions", new { model = "local", max_tokens = 20 }, directory);
                    findings["aiBudgetStopsBeforeNetwork"] = false;
                }
                catch (InvalidOperationException ex)
                {
                    findings["aiBudgetStopsBeforeNetwork"] = ex.Message.Contains("预算", StringComparison.Ordinal);
                }
            }
            var budgetCheckDirectory = Path.Combine(directory, "budget-integrity");
            Directory.CreateDirectory(budgetCheckDirectory);
            File.WriteAllText(Path.Combine(budgetCheckDirectory, "ai-budget.json"), "{}");
            try { AiRequestQueue.LoadBudgetLedger(budgetCheckDirectory); findings["corruptAiBudgetFailsClosed"] = false; }
            catch (InvalidDataException) { findings["corruptAiBudgetFailsClosed"] = true; }
            File.WriteAllText(Path.Combine(budgetCheckDirectory, "ai-budget.json"),
                JsonSerializer.Serialize(new AiRequestQueue.BudgetLedger { Day = DateTime.Now.ToString("yyyy-MM-dd"), EstimatedUsd = 1.25 }));
            findings["validAiBudgetRetained"] = Math.Abs(AiRequestQueue.LoadBudgetLedger(budgetCheckDirectory).EstimatedUsd - 1.25) < 0.00001;
            File.WriteAllText(Path.Combine(budgetCheckDirectory, "ai-budget.json"),
                JsonSerializer.Serialize(new AiRequestQueue.BudgetLedger
                {
                    Day = DateTime.Now.ToString("yyyy-MM-dd"), EstimatedUsd = 1.25, UnverifiedUsd = -1
                }));
            try { AiRequestQueue.LoadBudgetLedger(budgetCheckDirectory); findings["corruptAiBudgetBreakdownFailsClosed"] = false; }
            catch (InvalidDataException) { findings["corruptAiBudgetBreakdownFailsClosed"] = true; }
            findings["legacyDetailCacheRefresh"] = await ScrapeCandidateCache.VerifyLegacyIdentityRefreshAsync(directory);
            findings["reviewTransactionDurability"] = ReviewTransactionStore.VerifyForDiagnostics(Path.Combine(directory, "review-transactions"));
            findings["reviewSelection10k"] = MainWindow.VerifyReviewSelectionForDiagnostics();
            var fairQueue = ScrapeQueueFairnessDiagnostics.Run(Path.Combine(directory, "source-fair-queue"));
            findings["sourceFairQueue10k"] = fairQueue.Passed;
            findings["sourceFairQueue10kMetrics"] = fairQueue;
            findings["folderTreeHierarchy"] = MainWindow.VerifyFolderTreeForDiagnostics();
            findings["categoryChildPathsAndBatchApply"] = MainWindow.VerifyChildCategoriesForDiagnostics();

            var backupData = Path.Combine(directory, "backup-verification");
            Directory.CreateDirectory(backupData);
            File.WriteAllText(Path.Combine(backupData, "config.json"),
                "{\"folders\":[\"X:\\\\Videos\"],\"aiKey\":\"secret-sentinel\",\"tmdbToken\":\"tmdb-secret\",\"javinfoKey\":\"paid-secret\"}");
            File.WriteAllText(Path.Combine(backupData, "metadata.json"), "{\"video-1\":{\"Title\":\"Original\"}}");
            File.WriteAllText(Path.Combine(backupData, "personal-ratings.json"), "{}");
            File.WriteAllText(Path.Combine(backupData, "review-history.jsonl"), "{\"Action\":\"approve\"}" + Environment.NewLine);
            File.WriteAllText(Path.Combine(backupData, "ai-budget.json"), "{\"Day\":\"2026-10-25\",\"EstimatedUsd\":1}");
            File.WriteAllText(Path.Combine(backupData, "javinfo-usage.json"), "{\"UtcDay\":\"2026-10-25\",\"ReservedCalls\":1}");
            File.WriteAllText(Path.Combine(backupData, "scrape-source-backoff.json"), "{\"Version\":1,\"Sources\":{}}");
            Directory.CreateDirectory(Path.Combine(backupData, "posters"));
            File.WriteAllBytes(Path.Combine(backupData, "posters", "abc.jpg"), [1, 2, 3]);
            Directory.CreateDirectory(Path.Combine(backupData, "thumbs"));
            File.WriteAllBytes(Path.Combine(backupData, "thumbs", "abc-custom.png"), [4, 5, 6]);
            ScrapeQueueStore.Add(backupData, [new ScrapeQueueItem { VideoId = "video-1", Code = "ABC-123" }]);
            var backupPackage = LibraryBackupService.Create(backupData);
            var backupCheck = LibraryBackupService.Verify(backupPackage);
            var backedConfig = File.ReadAllText(Path.Combine(backupPackage, "config.json"));
            findings["versionedBackupManifestAndSecrets"] = backupCheck.IsValid && backupCheck.FileCount >= 7
                && File.Exists(Path.Combine(backupPackage, "backup-manifest.json"))
                && File.Exists(Path.Combine(backupPackage, "posters", "abc.jpg"))
                && File.Exists(Path.Combine(backupPackage, "thumbs", "abc-custom.png"))
                && File.Exists(Path.Combine(backupPackage, "scrape-source-backoff.json"))
                && !backedConfig.Contains("secret-sentinel", StringComparison.Ordinal)
                && !backedConfig.Contains("tmdb-secret", StringComparison.Ordinal)
                && !backedConfig.Contains("paid-secret", StringComparison.Ordinal);
            var sourceBackoffPath = Path.Combine(backupData, "scrape-source-backoff.json");
            var validSourceBackoff = File.ReadAllText(sourceBackoffPath);
            File.WriteAllText(sourceBackoffPath + ".bak", validSourceBackoff);
            File.WriteAllText(sourceBackoffPath, "{damaged");
            var fallbackBackup = LibraryBackupService.Create(backupData);
            findings["backupUsesValidSourceBackoffFallback"] = LibraryBackupService.Verify(fallbackBackup).IsValid
                && File.ReadAllText(Path.Combine(fallbackBackup, "scrape-source-backoff.json")) == validSourceBackoff;
            File.WriteAllText(sourceBackoffPath, validSourceBackoff);
            var offlineBackupData = Path.Combine(directory, "offline-backup-verification");
            Directory.CreateDirectory(offlineBackupData);
            File.WriteAllText(Path.Combine(offlineBackupData, "config.json"), "{\"folders\":[],\"aiKey\":\"offline-secret\"}");
            File.WriteAllText(Path.Combine(offlineBackupData, "metadata.json"), "{}");
            LibraryBackupService.StageOfflineBackup(offlineBackupData);
            bool backupWaitedForOtherSession;
            using (var activeSession = LibraryBackupService.AcquireSessionLease(offlineBackupData))
                backupWaitedForOtherSession = !LibraryBackupService.ApplyPendingOfflineBackupAtStartup(offlineBackupData, out _, false)
                    && LibraryBackupService.HasPendingOfflineBackup(offlineBackupData);
            var offlineBackupApplied = LibraryBackupService.ApplyPendingOfflineBackupAtStartup(offlineBackupData, out var offlineBackupError, false);
            var offlineBackupResult = LibraryBackupService.LastOfflineBackupResult(offlineBackupData);
            findings["scheduledOfflineBackupIsConsistent"] = backupWaitedForOtherSession && offlineBackupApplied
                && offlineBackupError.Length == 0 && !LibraryBackupService.HasPendingOfflineBackup(offlineBackupData)
                && offlineBackupResult is { Succeeded: true }
                && LibraryBackupService.Verify(offlineBackupResult.Path).IsValid
                && !File.ReadAllText(Path.Combine(offlineBackupResult.Path, "config.json")).Contains("offline-secret", StringComparison.Ordinal);
            LibraryBackupService.StageOfflineBackup(offlineBackupData);
            bool restoreBlockedByPendingBackup;
            try { LibraryBackupService.StageRestore(offlineBackupData, offlineBackupResult!.Path); restoreBlockedByPendingBackup = false; }
            catch (InvalidOperationException) { restoreBlockedByPendingBackup = true; }
            LibraryBackupService.CancelPendingOfflineBackup(offlineBackupData);
            findings["offlineBackupCanCancelWithoutRestoreRace"] = restoreBlockedByPendingBackup
                && !LibraryBackupService.HasPendingOfflineBackup(offlineBackupData);
            var failedOfflineData = Path.Combine(directory, "offline-backup-retry");
            Directory.CreateDirectory(failedOfflineData);
            File.WriteAllText(Path.Combine(failedOfflineData, "config.json"), "broken config");
            LibraryBackupService.StageOfflineBackup(failedOfflineData);
            var keptAfterFailure = LibraryBackupService.ApplyPendingOfflineBackupAtStartup(failedOfflineData,
                out var offlineFailureMessage, false)
                && offlineFailureMessage.Contains("一致性备份失败", StringComparison.Ordinal)
                && LibraryBackupService.HasPendingOfflineBackup(failedOfflineData)
                && LibraryBackupService.LastOfflineBackupResult(failedOfflineData) is { Succeeded: false };
            File.WriteAllText(Path.Combine(failedOfflineData, "config.json"), "{\"folders\":[]}");
            var retrySucceeded = LibraryBackupService.ApplyPendingOfflineBackupAtStartup(failedOfflineData, out _, false)
                && !LibraryBackupService.HasPendingOfflineBackup(failedOfflineData)
                && LibraryBackupService.LastOfflineBackupResult(failedOfflineData) is { Succeeded: true };
            findings["offlineBackupFailureKeepsRetryAndAllowsStart"] = keptAfterFailure && retrySucceeded;
            File.WriteAllText(Path.Combine(backupData, "config.json"), "{\"folders\":[\"modified\"]}");
            File.WriteAllText(Path.Combine(backupData, "metadata.json"), "{\"video-2\":{\"Title\":\"Changed\"}}");
            File.WriteAllText(Path.Combine(backupData, "ai-budget.json"), "{\"Day\":\"2026-10-25\",\"EstimatedUsd\":9}");
            File.WriteAllText(Path.Combine(backupData, "javinfo-usage.json"), "{\"UtcDay\":\"2026-10-25\",\"ReservedCalls\":9}");
            File.WriteAllText(Path.Combine(backupData, "scrape-source-backoff.json.bak"), "stale pre-restore source cooldown");
            ScrapeQueueStore.Remove(backupData, "video-1");
            LibraryBackupService.StageRestore(backupData, backupPackage);
            bool blockedBySharedLeases;
            using (var firstSession = LibraryBackupService.AcquireSessionLease(backupData))
            using (var secondSession = LibraryBackupService.AcquireSessionLease(backupData))
                blockedBySharedLeases = !LibraryBackupService.ApplyPendingAtStartup(backupData, out _, false)
                    && File.Exists(Path.Combine(backupData, "restore-pending.json"))
                    && File.ReadAllText(Path.Combine(backupData, "metadata.json")).Contains("Changed", StringComparison.Ordinal);
            findings["sharedSessionLeaseBlocksOfflineRestore"] = blockedBySharedLeases;
            var appliedBackup = LibraryBackupService.ApplyPendingAtStartup(backupData, out var restoreMessage, false);
            if (!appliedBackup) findings["backupRestoreDiagnostic"] = restoreMessage;
            findings["verifiedOfflineRestoreAndQueue"] = appliedBackup && restoreMessage.Length == 0
                && File.ReadAllText(Path.Combine(backupData, "metadata.json")).Contains("Original", StringComparison.Ordinal)
                && ScrapeQueueStore.Load(backupData).SingleOrDefault()?.Code == "ABC-123"
                && File.Exists(Path.Combine(backupData, "posters", "abc.jpg"))
                && !File.Exists(Path.Combine(backupData, "scrape-source-backoff.json.bak"));
            using var keptBudget = JsonDocument.Parse(File.ReadAllText(Path.Combine(backupData, "ai-budget.json")));
            findings["restorePreservesMonotonicQuotaLedgers"] = keptBudget.RootElement.GetProperty("EstimatedUsd").GetDouble() == 9
                && JavinfoUsageLedger.ReadForDiagnostics(backupData).reservedCalls == 9;
            File.Delete(Path.Combine(backupData, "ai-budget.json"));
            File.Delete(Path.Combine(backupData, "javinfo-usage.json"));
            LibraryBackupService.StageRestore(backupData, backupPackage);
            var restoredMissingLedgers = LibraryBackupService.ApplyPendingAtStartup(backupData, out _, false);
            using var recoveredBudget = JsonDocument.Parse(File.ReadAllText(Path.Combine(backupData, "ai-budget.json")));
            findings["restoreMissingQuotaLedgers"] = restoredMissingLedgers
                && recoveredBudget.RootElement.GetProperty("EstimatedUsd").GetDouble() == 1
                && JavinfoUsageLedger.ReadForDiagnostics(backupData).reservedCalls == 1;
            var beforeRejectedRestore = File.ReadAllText(Path.Combine(backupData, "metadata.json"));
            var damagedBackup = LibraryBackupService.Create(backupData);
            File.AppendAllText(Path.Combine(damagedBackup, "config.json"), "tampered");
            var damageRejected = !LibraryBackupService.Verify(damagedBackup).IsValid;
            try { LibraryBackupService.StageRestore(backupData, damagedBackup); damageRejected = false; }
            catch (InvalidDataException) { }
            findings["corruptBackupCannotModifyLibrary"] = damageRejected
                && File.ReadAllText(Path.Combine(backupData, "metadata.json")) == beforeRejectedRestore;
            var damagedAfterStaging = LibraryBackupService.Create(backupData);
            LibraryBackupService.StageRestore(backupData, damagedAfterStaging);
            File.AppendAllText(Path.Combine(damagedAfterStaging, "metadata.json"), "changed-after-selection");
            var startupRejectedTamper = !LibraryBackupService.ApplyPendingAtStartup(backupData, out var tamperMessage, false);
            findings["startupRechecksBackupBeforeWrite"] = startupRejectedTamper
                && tamperMessage.Contains("校验失败", StringComparison.Ordinal)
                && File.ReadAllText(Path.Combine(backupData, "metadata.json")) == beforeRejectedRestore
                && !File.Exists(Path.Combine(backupData, "restore-pending.json"));
            var missingBackup = LibraryBackupService.Create(backupData);
            File.Delete(Path.Combine(missingBackup, "scrape-queue.json"));
            findings["missingBackupFileRejected"] = !LibraryBackupService.Verify(missingBackup).IsValid;
            var oldBackup = Path.Combine(backupData, "backups", "old-style");
            Directory.CreateDirectory(oldBackup);
            File.Copy(Path.Combine(backupData, "config.json"), Path.Combine(oldBackup, "config.json"));
            findings["legacyBackupWithoutManifestRejected"] = !LibraryBackupService.Verify(oldBackup).IsValid;
            var rollbackBackup = LibraryBackupService.Create(backupData, rollback: true);
            File.WriteAllText(Path.Combine(backupData, "restore-active.json"), JsonSerializer.Serialize(new
                { BackupPath = backupPackage, RollbackPath = rollbackBackup }));
            File.WriteAllText(Path.Combine(backupData, "metadata.json"), "{\"interrupted\":{}}");
            var interruptedRecovered = !LibraryBackupService.ApplyPendingAtStartup(backupData, out var interruptedMessage, false);
            findings["interruptedRestoreRollsBack"] = interruptedRecovered && interruptedMessage.Contains("原数据", StringComparison.Ordinal)
                && File.ReadAllText(Path.Combine(backupData, "metadata.json")) == beforeRejectedRestore
                && !File.Exists(Path.Combine(backupData, "restore-active.json"));
            File.WriteAllText(Path.Combine(backupData, "config.json"), "{broken");
            LibraryBackupService.StageRestore(backupData, backupPackage);
            var repairedCorruptConfig = LibraryBackupService.ApplyPendingAtStartup(backupData, out _, false);
            findings["restoreWhenCurrentConfigCorrupt"] = repairedCorruptConfig
                && LibraryBackupService.Verify(backupPackage).IsValid
                && File.ReadAllText(Path.Combine(backupData, "config.json")).Contains("folders", StringComparison.Ordinal);
            File.Delete(Path.Combine(backupData, "config.json"));
            LibraryBackupService.StageRestore(backupData, backupPackage);
            var repairedMissingConfig = LibraryBackupService.ApplyPendingAtStartup(backupData, out _, false);
            findings["restoreWhenCurrentConfigMissing"] = repairedMissingConfig
                && File.Exists(Path.Combine(backupData, "config.json"))
                && ScrapeQueueStore.Load(backupData).SingleOrDefault()?.Code == "ABC-123";

            foreach (var result in BackupScheduleDiagnostics.Run())
                findings[result.Key] = result.Value;

            var titleCandidates = await TitleCandidateDiagnostics.RunAsync(directory);
            findings["titleCandidateOfflineComparison"] = titleCandidates.Passed;
            findings["titleCandidateThreeSurviveRestart"] = titleCandidates.ThreeCandidatesSurviveRestart;
            findings["titleCandidateStableTieOrder"] = titleCandidates.StableTieOrder;
            findings["titleCandidateProvenancePreserved"] = titleCandidates.OriginalAndDetailProvenancePreserved;
            findings["titleCandidateExpiredStillReadable"] = titleCandidates.ExpiredSnapshotStillReadable;
            findings["titleCandidateNetworkFailureNotEmpty"] = titleCandidates.NetworkFailureNotCachedAsEmpty;
            findings["titleCandidateOtherSourceSurvivesOverflow"] = titleCandidates.OtherSourceSurvivesOverflow;

            foreach (var result in MainWindow.VerifyUpdatePromptForDiagnostics(Path.Combine(directory, "update-prompt")))
                findings["updatePrompt." + result.Key] = result.Value;

            foreach (var result in StartupUpdateGateDiagnostics.Verify(Path.Combine(directory, "startup-update-gate")))
                findings["startupUpdateGate." + result.Key] = result.Value;

            findings["passed"] = findings.Values.OfType<bool>().All(value => value);
        }
        catch (Exception ex) { findings["passed"] = false; findings["error"] = ex.ToString(); }
        finally
        {
            try { WindowsCredentialStore.Write(credentialName, ""); } catch { }
            try { Directory.Delete(directory, true); } catch { }
        }
        File.WriteAllText(resultPath, JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true }));
        return findings.TryGetValue("passed", out var allPassed) && allPassed is true ? 0 : 1;
    }
}

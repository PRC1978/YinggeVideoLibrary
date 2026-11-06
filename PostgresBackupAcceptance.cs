using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace YinggeVideoLibrary;

// Acceptance probe for the database-mode backup and restore path. It runs only
// inside the isolated bundled-runtime probe: every cluster, every fixture and
// every backup lives under %TEMP%, and no real media file is opened.
internal static class PostgresBackupAcceptance
{
    internal static async Task<int> RunToFileAsync(string runtimeDirectory, string outputPath)
    {
        var probeRoot = Path.Combine(Path.GetTempPath(), "YinggePgBackupProbe-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeRoot);
        var findings = await RunAsync(runtimeDirectory, probeRoot, includeLarge: false);
        var passed = findings.TryGetValue("backupMatrixVerified", out var matrix) && matrix is true &&
            findings.TryGetValue("backupFailureInjectionVerified", out var failures) && failures is true;
        findings["passed"] = passed;
        findings["realLibraryRead"] = false;
        findings["probeDirectory"] = probeRoot;
        await File.WriteAllBytesAsync(outputPath, JsonSerializer.SerializeToUtf8Bytes(findings));
        return passed ? 0 : 1;
    }

    internal sealed record CaseResult(int Videos, bool Imported, bool BackupVerified,
        bool MarkerActivated, bool MutationApplied, bool Restored, bool RollbackArchiveVerified,
        bool PreviousSchemaVerified, bool MarkerRewritten, bool RestartVerified,
        bool EntitiesVerified, bool RuntimeWritesVerified, long ImportMs, long BackupMs,
        long RestoreMs, long BackupBytes);

    internal sealed record FailureResult(bool InterruptedBackupClean, bool SpacePrecheckRejected,
        bool CorruptPackageRejected, bool OldPackageRejected, bool OldDatabaseUpgraded,
        bool NewerDatabaseRefused, bool InterruptedRestoreKeptOldData, bool InterruptedRestoreCleaned,
        bool FailedActivationLeftNoMarker, bool FailedRestoreReverted, bool SentinelBlocksBothModes,
        bool FailedAfterMarkerRestoredOriginal, bool PartialAssetSwapReverted,
        bool MissingStagingTableRejected, bool PendingCleanupBlocksActivation);

    internal static async Task<Dictionary<string, object?>> RunAsync(string runtimeDirectory,
        string probeRoot, bool includeLarge = true)
    {
        var findings = new Dictionary<string, object?>();
        var results = new List<CaseResult>();
        Log(probeRoot, "acceptance start");
        try
        {
            var sizes = includeLarge ? new[] { 1, 3000, 30000 } : [1, 3000];
            foreach (var count in sizes)
            {
                Log(probeRoot, "matrix " + count + " start");
                results.Add(await RunMatrixCaseAsync(runtimeDirectory, probeRoot, count));
                Log(probeRoot, "matrix " + count + " done");
            }
            findings["backupMatrix"] = results;
            findings["backupMatrixVerified"] = results.All(item => item.Imported &&
                item.BackupVerified && item.MarkerActivated && item.MutationApplied &&
                item.Restored && item.RollbackArchiveVerified && item.PreviousSchemaVerified &&
                item.MarkerRewritten && item.RestartVerified && item.EntitiesVerified &&
                item.RuntimeWritesVerified);

            var failures = await RunFailureCasesAsync(runtimeDirectory, probeRoot);
            Log(probeRoot, "failure cases done");
            findings["backupFailureInjection"] = failures;
            Log(probeRoot, "failure findings stored");
            findings["backupFailureInjectionVerified"] = failures.InterruptedBackupClean &&
                failures.SpacePrecheckRejected && failures.CorruptPackageRejected &&
                failures.OldPackageRejected && failures.OldDatabaseUpgraded &&
                failures.NewerDatabaseRefused && failures.InterruptedRestoreKeptOldData &&
                failures.InterruptedRestoreCleaned && failures.FailedActivationLeftNoMarker &&
                failures.FailedRestoreReverted && failures.SentinelBlocksBothModes &&
                failures.FailedAfterMarkerRestoredOriginal && failures.PartialAssetSwapReverted &&
                failures.MissingStagingTableRejected &&
                failures.PendingCleanupBlocksActivation;
        }
        catch (Exception ex)
        {
            Log(probeRoot, "acceptance error " + ex);
            findings["backupAcceptanceError"] = ex.GetType().Name + ": " + ex.Message;
            findings["backupMatrixVerified"] = false;
            findings["backupFailureInjectionVerified"] = false;
        }
        return findings;
    }

    private static void Log(string probeRoot, string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(probeRoot, "backup-acceptance.log"),
                DateTimeOffset.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
        }
        catch (IOException) { }
    }

    private static async Task<CaseResult> RunMatrixCaseAsync(string runtimeDirectory,
        string probeRoot, int count)
    {
        var source = Path.Combine(probeRoot, "backup-source-" + count);
        var library = Path.Combine(probeRoot, "backup-library-" + count);
        var backup = Path.Combine(probeRoot, "backup-snapshot-" + count);
        var videos = BuildFixture(source, count);

        var cluster = await PostgresPrivateCluster.OpenAsync(runtimeDirectory, library);
        try
        {
            CopyLegacyFilesForActivation(source, library);
            var watch = Stopwatch.StartNew();
            await PostgresLibraryMigration.ImportAsync(source, cluster.ConnectionString);
            var importMs = watch.ElapsedMilliseconds;
            var imported = await PostgresLibraryMigration.VerifyAsync(cluster.ConnectionString);
            var importedOk = imported.VideoCount == count &&
                await PostgresLibraryMigration.VerifyContentAsync(source, cluster.ConnectionString);

            watch.Restart();
            var info = await PostgresLibraryBackup.CreateAsync(cluster.ConnectionString, library,
                backup);
            var backupMs = watch.ElapsedMilliseconds;
            var backupOk = info.VideoCount == count &&
                await PostgresLibraryExport.VerifyOfflineAsync(backup) &&
                await PostgresLibraryMigration.VerifyContentAsync(backup, cluster.ConnectionString);

            await PostgresStorageModeActivation.ActivateAsync(cluster.ConnectionString, library,
                backup);
            var markerOk = PostgresStorageModeGuard.PostgresMayOpen(library) &&
                !PostgresStorageModeGuard.LegacyFilesMayOpen(library);

            await MutateAsync(cluster.ConnectionString, videos);
            File.WriteAllBytes(Path.Combine(library, "posters", "sample.jpg"), [9, 9, 9]);
            File.WriteAllBytes(Path.Combine(library, "thumbs", "sample-custom.png"), [8, 8, 8]);
            var mutationApplied = await HasMutatedStateAsync(cluster.ConnectionString);

            watch.Restart();
            var restore = await PostgresLibraryRestore.RestoreInPlaceAsync(cluster.ConnectionString,
                library, backup);
            var restoreMs = watch.ElapsedMilliseconds;
            // A restored database is compared against the canonical snapshot, not
            // against the raw source directory: the export normalises the queue
            // into scrape-queue.json and intentionally drops journal generations.
            var restored = !await HasMutatedStateAsync(cluster.ConnectionString) &&
                await PostgresLibraryMigration.VerifyContentAsync(backup, cluster.ConnectionString) &&
                File.ReadAllBytes(Path.Combine(library, "posters", "sample.jpg"))
                    .SequenceEqual(new byte[] { 1, 2, 3, 4 }) &&
                File.ReadAllBytes(Path.Combine(library, "thumbs", "sample-custom.png"))
                    .SequenceEqual(new byte[] { 5, 6, 7 });

            var archiveVerified = await PostgresLibraryExport.VerifyOfflineAsync(
                restore.RollbackArchive);
            var previousSchemaVerified = archiveVerified &&
                await PostgresLibraryMigration.VerifyContentAsync(restore.RollbackArchive,
                    cluster.ConnectionString, CancellationToken.None, restore.PreviousSchema) &&
                !await PostgresLibraryMigration.VerifyContentAsync(restore.RollbackArchive,
                    cluster.ConnectionString);
            var markerAfter = PostgresStorageModeActivation.TryRead(library);
            var markerRewritten = markerAfter is not null &&
                markerAfter.BackupDirectory == Path.GetFullPath(backup) &&
                markerAfter.VideoCount == count &&
                PostgresStorageModeGuard.Current(library) ==
                    PostgresStorageModeGuard.StorageMode.PostgreSql;
            var entities = await VerifyEntitiesAsync(cluster.ConnectionString, videos);
            await cluster.DisposeAsync();

            var reopened = await PostgresLibraryRuntimeSession.OpenAsync(runtimeDirectory, library);
            bool restartVerified;
            bool runtimeWritesVerified;
            try
            {
                restartVerified = PostgresStorageModeGuard.PostgresMayOpen(library) &&
                    await PostgresLibraryMigration.VerifyContentAsync(backup,
                        reopened.ConnectionString) &&
                    reopened.Index.Index.Entries.Count == count &&
                    reopened.Metadata.Records.Count == count &&
                    reopened.Config.Folders.Count == 1 &&
                    reopened.Queue.Count == 2 &&
                    await VerifyEntitiesAsync(reopened.ConnectionString, videos);
                runtimeWritesVerified = await VerifyRuntimeWritesAsync(reopened, videos, library);
            }
            finally { await reopened.DisposeAsync(); }

            return new(count, importedOk, backupOk, markerOk, mutationApplied, restored,
                archiveVerified, previousSchemaVerified, markerRewritten, restartVerified,
                entities, runtimeWritesVerified, importMs, backupMs, restoreMs,
                PostgresLibraryBackup.DirectorySize(backup));
        }
        catch
        {
            try { await cluster.DisposeAsync(); } catch { }
            throw;
        }
    }

    private sealed record Fixture(int Videos, string VideoId, string MediaRoot);

    private static async Task<bool> VerifyRuntimeWritesAsync(
        PostgresLibraryRuntimeSession session, Fixture fixture, string library)
    {
        var firstWorker = await session.TryAcquireScrapeWorkerAsync();
        if (firstWorker is null) return false;
        var secondWorker = await session.TryAcquireScrapeWorkerAsync();
        var exclusiveWorker = secondWorker is null;
        if (secondWorker is not null) await secondWorker.DisposeAsync();
        await firstWorker.DisposeAsync();
        var afterRelease = await session.TryAcquireScrapeWorkerAsync();
        exclusiveWorker &= afterRelease is not null;
        if (afterRelease is not null) await afterRelease.DisposeAsync();
        var sourceReserved = await session.ReserveSourceAsync("runtime-source",
            TimeSpan.FromSeconds(2));
        var sourceSpacing = await session.ReserveSourceAsync("runtime-source",
            TimeSpan.FromSeconds(2));
        var sourceFailure = await session.RecordSourceFailureAsync("runtime-source",
            "temporary", false);
        var cooldown = await session.SourceCooldownAsync("runtime-source");
        var sourceRecovered = await session.ReportSourceSuccessAsync("runtime-source");
        var sourcePacingVerified = sourceReserved == DateTimeOffset.MinValue &&
            sourceSpacing > DateTimeOffset.UtcNow &&
            sourceFailure > sourceSpacing && cooldown == sourceFailure && sourceRecovered;
        var config = MainWindow.CloneLibraryConfig(session.Config);
        var originalShowAll = config.ShowAll;
        config.ShowAll = !originalShowAll;
        await session.SaveConfigAsync(config);

        var index = new LibraryIndex
        {
            Folders = session.Index.Index.Folders,
            Entries = session.Index.Index.Entries.Select(entry => new IndexEntry
            {
                Path = entry.Path, Size = entry.Size + (entry.Path.EndsWith("video-000000.mp4",
                    StringComparison.Ordinal) ? 1 : 0), ModifiedTicks = entry.ModifiedTicks,
                Missing = entry.Missing
            }).ToList()
        };
        await session.SaveIndexAsync(index);
        var beforeEmptyScan = session.Index.Revision;
        var protectedEmptyScan = await session.SaveScannedIndexAsync(new LibraryIndex
        {
            Folders = [Path.Combine(fixture.MediaRoot, "other-root")], Entries = []
        }, explicitlyClearedByUser: false);
        var unconfiguredEmptyScan = await session.SaveScannedIndexAsync(new LibraryIndex
        {
            Folders = [], Entries = []
        }, explicitlyClearedByUser: false);
        var emptyScanProtected = protectedEmptyScan.PreservedMismatchedIndex &&
            !protectedEmptyScan.Written &&
            protectedEmptyScan.PreviousEntryCount == fixture.Videos &&
            !unconfiguredEmptyScan.Written &&
            session.Index.Revision == beforeEmptyScan;

        var firstEntry = session.Index.Index.Entries[0];
        var missingIndex = new LibraryIndex { Folders = session.Index.Index.Folders,
            Entries = session.Index.Index.Entries.Select(entry => new IndexEntry
            { Path = entry.Path, Size = entry.Size, ModifiedTicks = entry.ModifiedTicks,
                Missing = entry.Path == firstEntry.Path || entry.Missing }).ToList() };
        await session.SaveIndexAsync(missingIndex);
        var cleanupRecord = await PostgresMissingCleanupStore.CleanupAsync(session,
            [new MissingCleanupEntry { Path = firstEntry.Path, Size = firstEntry.Size,
                ModifiedTicks = firstEntry.ModifiedTicks }], session.Config.Folders);
        await session.ReloadIndexAsync();
        var cleanupAtomic = session.Index.Index.Entries.Count == fixture.Videos - 1 &&
            await PostgresMissingCleanupStore.HasUndoableAsync(session.ConnectionString);
        var undone = await PostgresMissingCleanupStore.UndoAsync(session, session.Config.Folders);
        await session.ReloadIndexAsync();
        cleanupAtomic &= undone?.Id == cleanupRecord.Id &&
            session.Index.Index.Entries.Count == fixture.Videos &&
            session.Index.Index.Entries.Single(entry => entry.Path == firstEntry.Path).Missing &&
            !await PostgresMissingCleanupStore.HasUndoableAsync(session.ConnectionString);
        var externallyChanged = new LibraryIndex { Folders = session.Index.Index.Folders,
            Entries = session.Index.Index.Entries.Select(entry => new IndexEntry
            { Path = entry.Path, Size = entry.Size + (entry.Path == firstEntry.Path ? 1 : 0),
                ModifiedTicks = entry.ModifiedTicks, Missing = entry.Missing }).ToList() };
        await PostgresLibraryIndexStore.SaveAsync(session.ConnectionString, externallyChanged,
            session.Index.Revision);
        var staleCleanupRejected = false;
        try
        {
            await PostgresMissingCleanupStore.CleanupAsync(session,
                [new MissingCleanupEntry { Path = firstEntry.Path, Size = firstEntry.Size,
                    ModifiedTicks = firstEntry.ModifiedTicks }], session.Config.Folders);
        }
        catch (InvalidOperationException) { staleCleanupRejected = true; }
        var afterRejected = await PostgresLibraryIndexStore.ReadAsync(session.ConnectionString);
        cleanupAtomic &= staleCleanupRejected &&
            afterRejected.Index.Entries.Count == fixture.Videos &&
            afterRejected.Index.Entries.Single(entry => entry.Path == firstEntry.Path).Size == firstEntry.Size + 1 &&
            !await PostgresMissingCleanupStore.HasUndoableAsync(session.ConnectionString);
        await session.ReloadIndexAsync();
        await session.SaveIndexAsync(index);

        var metadata = session.Metadata.Records.ToDictionary(pair => pair.Key,
            pair => pair.Value.Clone(), StringComparer.Ordinal);
        metadata[fixture.VideoId].Title = "数据库会话写入测试";
        await session.SaveMetadataAsync(metadata);
        var reviewBefore = new ReviewItemSnapshot
        {
            Review = session.Config.AiReviews.GetValueOrDefault(fixture.VideoId),
            Metadata = session.Metadata.Records[fixture.VideoId].Clone(),
            Tags = session.Config.Tags.GetValueOrDefault(fixture.VideoId)?.ToList(),
            AutoAmateur = session.Config.AutoAmateurTags.Contains(fixture.VideoId)
        };
        var reviewAfter = reviewBefore.Clone();
        reviewAfter.Review = new AiReviewRecord { Status = "approved" };
        var historyBefore = session.ReviewHistory.Records.Count;
        await session.CommitReviewAsync(ReviewTransactionStore.Create("runtime-review",
            new Dictionary<string, ReviewItemSnapshot> { [fixture.VideoId] = reviewBefore },
            new Dictionary<string, ReviewItemSnapshot> { [fixture.VideoId] = reviewAfter }));
        var reviewCommitted = session.Config.AiReviews[fixture.VideoId].Status == "approved" &&
            session.ReviewHistory.Records.Count == historyBefore + 1;
        var reviewViews = await session.ChangeReviewViewsAsync(existing =>
            existing.Append(new ReviewSavedView { Name = "数据库会话视图", Status = "pending" }));
        var reviewViewsPersisted = reviewViews.Count == 1 &&
            (await session.LoadReviewViewsAsync()).Value.Single().Name == "数据库会话视图";

        var added = await session.AddQueueAsync([new ScrapeQueueItem
            { VideoId = "runtime-queue-test", Code = "RUNTIME-001" }]);
        var queued = session.Queue.Single(item => item.VideoId == "runtime-queue-test");
        var retry = new ScrapeQueueItem
        {
            VideoId = queued.VideoId, Code = queued.Code, Attempts = 2,
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(2), LastError = "temporary"
        };
        var rescheduled = await session.RescheduleQueueAsync(retry);
        var retryPersisted = session.Queue.Single(item => item.VideoId == retry.VideoId)
            .Attempts == 2;
        var markedFailed = await session.MarkQueueFailedAsync(retry.VideoId, "failure");
        var failedPersisted = session.Queue.Single(item => item.VideoId == retry.VideoId)
            .Failed;
        var removedQueueItem = await session.RemoveQueueAsync(retry.VideoId);
        var bridge = new ScrapeQueueBridge(fixture.MediaRoot, session);
        // Normal UI callers dispatch the synchronous bridge from a worker thread.
        // Exercise that same boundary so an ambient UI context cannot deadlock it.
        var bridgeReadsDatabase = await Task.Run(() =>
        {
            var bridgedStatus = bridge.GetStatus(["runtime-code-source"], ["runtime-title-source"]);
            var bridgedQueue = bridge.Load();
            return bridgedStatus.PendingCount == 2 && bridgedQueue.Count == 2 &&
                bridge.PeekNextFair(["runtime-code-source"], ["runtime-title-source"]).PendingCount == 2;
        });
        var roles = RoleProfileStore.Parse(JsonSerializer.Serialize(session.Roles.State));
        roles.Profiles[0].Name = "人物甲·会话写入";
        await session.SaveRolesAsync(roles);
        var roleBaseline = await PostgresRoleProfileStore.LoadAsync(session.ConnectionString);
        var verifiedRoles = RoleProfileStore.Parse(JsonSerializer.Serialize(roleBaseline.State));
        verifiedRoles.Profiles[0].Biography = "数据库自动发现证据";
        var metadataRevision = await PostgresLibraryMetadataStore.ReadRevisionAsync(
            session.ConnectionString);
        var verifiedRoleSave = await PostgresRoleProfileStore.SaveVerifiedBatchAsync(
            session.ConnectionString, roleBaseline, verifiedRoles, metadataRevision);
        var wrongMetadataRejected = false;
        try
        {
            await PostgresRoleProfileStore.SaveVerifiedBatchAsync(session.ConnectionString,
                verifiedRoleSave, RoleProfileStore.Parse(JsonSerializer.Serialize(verifiedRoleSave.State)),
                "stale-metadata-revision");
        }
        catch (InvalidOperationException) { wrongMetadataRejected = true; }
        var roleAfterRejected = await PostgresRoleProfileStore.LoadAsync(session.ConnectionString);
        var verifiedRoleAtomic = wrongMetadataRejected &&
            roleAfterRejected.DocumentRevision == verifiedRoleSave.DocumentRevision &&
            roleAfterRejected.State.Profiles[0].Biography == "数据库自动发现证据";
        using var personWorkJson = JsonDocument.Parse("""
            {"id":42,"title":"数据库会话写入测试","credits":{"cast":[
              {"id":101,"name":"数据库演员","character":"主角"}]}}
            """);
        var verifiedWork = AdditionalScrapers.ParseTmdbDetails(new MovieMetadata
        {
            Source = "TMDB 电影", SourceUrl = "https://www.themoviedb.org/movie/42",
            Title = "数据库会话写入测试"
        }, personWorkJson.RootElement, "movie");
        var personMetadata = session.Metadata.Records.ToDictionary(pair => pair.Key,
            pair => pair.Value.Clone(), StringComparer.Ordinal);
        personMetadata[fixture.VideoId] = verifiedWork;
        await session.SaveMetadataAsync(personMetadata);
        var personVideo = new VideoItem
        {
            Id = fixture.VideoId, Title = verifiedWork.Title, Category = "电影",
            Path = Path.Combine(Path.GetTempPath(), "ordinary-film", "Example.mp4"),
            Folder = Path.Combine(Path.GetTempPath(), "ordinary-film"),
            Size = firstEntry.Size, Modified = new DateTime(firstEntry.ModifiedTicks)
        };
        var personPreview = RoleProfileAutoScraper.Preview([personVideo], personMetadata,
            roleAfterRejected.State);
        static Task<RoleProfileStore.SourceDetails> FetchOfflinePerson(
            RoleProfileStore.SourceIdentity identity, CancellationToken _)
            => Task.FromResult(new RoleProfileStore.SourceDetails
            {
                Provider = identity.Provider, PersonId = identity.PersonId,
                SourceUrl = identity.PersonUrl, Name = identity.NameAtSource,
                Biography = "隔离来源资料"
            });
        var personRun = await RoleProfileAutoScraper.RunAsync(fixture.MediaRoot,
            personPreview, "offline-test-token", fetchDetails: FetchOfflinePerson,
            postgresSession: session);
        var discoveredRoles = await PostgresRoleProfileStore.LoadAsync(session.ConnectionString);
        var person = RoleProfileStore.FindBySource(discoveredRoles.State, "TMDB", "101");
        var roleAutoVerified = personPreview.Candidates.Count == 1 &&
            personRun.Created == 1 && personRun.Fetched == 1 && personRun.Failed == 0 &&
            person?.Works.Single().LocalVideoId == fixture.VideoId &&
            person.SourcedDetails.Single().Biography == "隔离来源资料" &&
            !File.Exists(Path.Combine(fixture.MediaRoot, RoleProfileStore.FileName));
        if (!roleAutoVerified)
            throw new InvalidDataException($"人物数据库路径校验失败：候选={personPreview.Candidates.Count}，" +
                $"新建={personRun.Created}，已抓取={personRun.Fetched}，失败={personRun.Failed}，" +
                $"档案={(person is null ? "无" : person.Works.Count.ToString())}。");

        var candidateBefore = await session.ReadDocumentAsync("plugin-candidates.json");
        if (candidateBefore is null) return false;
        var candidateBytes = JsonSerializer.SerializeToUtf8Bytes(new { videos = new { },
            runtimeProbe = "updated" });
        var candidateRevision = await session.SaveDocumentAsync("plugin-candidates.json",
            candidateBytes, candidateBefore.Revision);
        var candidateAfter = await session.ReadDocumentAsync("plugin-candidates.json");
        var staleWriteRejected = false;
        try
        {
            await session.SaveDocumentAsync("plugin-candidates.json", candidateBytes,
                candidateBefore.Revision);
        }
        catch (InvalidOperationException) { staleWriteRejected = true; }
        var dedicatedWriterRequired = false;
        try
        {
            await session.SaveDocumentAsync("config.json", candidateBytes, null);
        }
        catch (InvalidDataException) { dedicatedWriterRequired = true; }
        var candidateBridge = new PluginCandidateBridge(fixture.MediaRoot, session);
        var bridgeCandidate = new PluginCandidateRecord
        {
            PluginId = "sample-plugin", Metadata = new MovieMetadata
            {
                Title = "隔离候选", SourceUrl = "https://example.org/work/1", Code = "EX-001"
            }
        };
        var candidateAdded = candidateBridge.Add(fixture.VideoId, bridgeCandidate);
        var candidateDuplicate = candidateBridge.Add(fixture.VideoId, bridgeCandidate);
        var candidateBridgeVerified = candidateAdded.Saved && candidateAdded.Count == 1 &&
            candidateDuplicate.Saved && candidateDuplicate.Count == 1 &&
            candidateBridge.Get(fixture.VideoId).Single().Metadata.Code == "EX-001";
        var stateBridge = new PluginStateBridge(fixture.MediaRoot, session);
        var enabledPlugins = stateBridge.Load();
        enabledPlugins.Enabled.Add("another-plugin");
        var pluginStateSaved = stateBridge.Save(enabledPlugins, out _) &&
            stateBridge.Load().Enabled.Contains("another-plugin");
        var erroredPlugins = stateBridge.ChangeError("sample-plugin", "isolated error");
        pluginStateSaved &= erroredPlugins.LastErrors["sample-plugin"].Message == "isolated error" &&
            !stateBridge.ChangeError("sample-plugin", null).LastErrors.ContainsKey("sample-plugin");
        AiStorageRoutes.Register(fixture.MediaRoot, session.ConnectionString);
        var aiBudgetVerified = await AiRequestQueue.VerifyOldDayReconcileForDiagnosticsAsync(
            fixture.MediaRoot) &&
            !File.Exists(Path.Combine(fixture.MediaRoot, "ai-budget.json")) &&
            AiRequestQueue.LoadBudgetLedger(fixture.MediaRoot).AttemptCount == 1;
        const string testEndpoint = "https://example.org/v1/chat/completions";
        const string testRequest = "{\"model\":\"offline\",\"messages\":[]}";
        var aiJob = AiJobStore.Create(fixture.MediaRoot, "offline-probe", testEndpoint,
            testRequest);
        var savedJob = AiJobStore.Get(fixture.MediaRoot, aiJob.Id);
        var startedJob = AiJobStore.Begin(fixture.MediaRoot, aiJob.Id, testEndpoint,
            testRequest);
        AiJobStore.Finish(fixture.MediaRoot, aiJob.Id, startedJob.AttemptId,
            AiJobStore.Completed, resultJson: "{\"ok\":true}");
        var completedJob = AiJobStore.Get(fixture.MediaRoot, aiJob.Id);
        var staleJobRejected = false;
        try { AiJobStore.Finish(fixture.MediaRoot, aiJob.Id, startedJob.AttemptId,
            AiJobStore.Completed, resultJson: "{\"stale\":true}"); }
        catch (InvalidOperationException) { staleJobRejected = true; }
        var corruptLegacyJobRejected = false;
        try { _ = AiJobStore.List(fixture.MediaRoot); }
        catch (InvalidDataException) { corruptLegacyJobRejected = true; }
        var aiJobsVerified = AiJobStore.ReadProtectedRequest(savedJob) == testRequest &&
            completedJob.Status == AiJobStore.Completed && staleJobRejected &&
            corruptLegacyJobRejected &&
            !File.Exists(AiJobStore.PathFor(fixture.MediaRoot, aiJob.Id));
        var rated = await PostgresUserStateStore.SetRatingAsync(
            session.ConnectionString, fixture.VideoId, 8.5);
        var ratingVerified = rated.Value[fixture.VideoId].Score == 8.5 &&
            (await PostgresUserStateStore.LoadRatingsAsync(session.ConnectionString))
                .Value[fixture.VideoId].Score == 8.5;
        var titleSnapshot = new ScrapeCandidateCache.TitleCandidateSnapshot
        {
            VideoId = fixture.VideoId, Query = "隔离标题",
            Candidates = [new ScrapeCandidateCache.RankedTitleCandidate
            {
                Score = 85,
                SearchMetadata = new MovieMetadata { Title = "数据库候选", Source = "测试来源" }
            }]
        };
        await PostgresTitleCandidateStore.SaveAsync(session.ConnectionString, titleSnapshot);
        var titleFromDatabase = await PostgresTitleCandidateStore.LoadAsync(session.ConnectionString,
            fixture.VideoId);
        var titleCandidatesVerified = titleFromDatabase?.Query == "隔离标题" &&
            titleFromDatabase.Candidates.Single().SearchMetadata.Title == "数据库候选" &&
            !Directory.Exists(Path.Combine(fixture.MediaRoot, "title-candidates"));
        Directory.CreateDirectory(fixture.MediaRoot);
        var siteExport = Path.Combine(fixture.MediaRoot, "isolated-list.csv");
        await File.WriteAllTextAsync(siteExport,
            "source,title,code,url\r\nTest,Unmatched isolated title,,\r\n");
        var sitePreview = await PostgresSiteListImportStore.CreatePreviewAsync(
            session.ConnectionString, siteExport, []);
        var siteImport = await PostgresSiteListImportStore.CommitAsync(session.ConnectionString,
            fixture.MediaRoot, sitePreview, []);
        var siteLoaded = await PostgresSiteListImportStore.LoadAsync(session.ConnectionString);
        var siteUndo = await PostgresSiteListImportStore.UndoAsync(session.ConnectionString,
            fixture.MediaRoot, siteImport.BatchId);
        var siteAfterUndo = await PostgresSiteListImportStore.LoadAsync(session.ConnectionString);
        var siteListVerified = siteImport.EntryKeys.Count == 1 && siteLoaded.Entries.Count == 1 &&
            siteUndo.Kind == "undo" && siteAfterUndo.Entries.Count == 0 &&
            siteAfterUndo.Actions.Count == 2 &&
            !File.Exists(Path.Combine(fixture.MediaRoot, "site-list-import.json"));
        var plan = await PostgresBackupPlanStore.ChangeAsync(session.ConnectionString, current =>
        {
            current.Enabled = true;
            current.Frequency = "daily";
            current.RetentionCount = 1;
            current.NextDueUtc = DateTimeOffset.UtcNow.AddDays(1);
            current.Pending = true;
            return current;
        });
        PostgresLibraryBackup.Info? firstBackup;
        PostgresLibraryBackup.Info? secondBackup;
        using (LibraryBackupService.AcquireSessionLease(library))
        {
            firstBackup = await PostgresBackupPlanStore.RunAtStartupAsync(session.ConnectionString,
                library, session.Config.Folders);
            await PostgresBackupPlanStore.ChangeAsync(session.ConnectionString, current =>
            {
                current.Pending = true;
                return current;
            });
            secondBackup = await PostgresBackupPlanStore.RunAtStartupAsync(session.ConnectionString,
                library, session.Config.Folders);
        }
        var savedPlan = await PostgresBackupPlanStore.LoadAsync(session.ConnectionString);
        var backupScheduleVerified = plan.Pending && firstBackup is not null &&
            secondBackup is not null && secondBackup.Directory != firstBackup.Directory &&
            !Directory.Exists(firstBackup.Directory) && Directory.Exists(secondBackup.Directory) &&
            await PostgresLibraryExport.VerifyOfflineAsync(secondBackup.Directory) &&
            !savedPlan.Pending && savedPlan.Backups.Count == 1 &&
            savedPlan.LastBackupDirectory == secondBackup.Directory;
        if (!backupScheduleVerified)
            throw new InvalidDataException("数据库备份轮换未通过：" +
                $"firstExists={Directory.Exists(firstBackup!.Directory)}, " +
                $"secondExists={Directory.Exists(secondBackup!.Directory)}, " +
                $"tracked={savedPlan.Backups.Count}, pending={savedPlan.Pending}, " +
                $"lastError={savedPlan.LastError}");

        var savedConfig = await PostgresLibraryDocumentStore.ReadAsync(session.ConnectionString,
            "config.json");
        return session.Config.ShowAll != originalShowAll &&
            JsonSerializer.Deserialize<LibraryConfig>(savedConfig!.Content)?.ShowAll != originalShowAll &&
            session.Index.Index.Entries[0].Size == index.Entries[0].Size &&
            session.Metadata.Records[fixture.VideoId].Title == "数据库会话写入测试" &&
            added == 1 && rescheduled && retryPersisted && markedFailed &&
            failedPersisted && removedQueueItem && session.Queue.Count == 2 &&
            bridgeReadsDatabase && cleanupAtomic && verifiedRoleAtomic && roleAutoVerified &&
            session.Roles.State.Profiles[0].Name == "人物甲·会话写入" &&
            candidateAfter?.Revision == candidateRevision &&
            candidateAfter.Content.SequenceEqual(candidateBytes) &&
            candidateBridgeVerified && pluginStateSaved && aiBudgetVerified && aiJobsVerified &&
            ratingVerified && titleCandidatesVerified && siteListVerified && backupScheduleVerified &&
            staleWriteRejected && dedicatedWriterRequired && emptyScanProtected &&
            exclusiveWorker && sourcePacingVerified && reviewCommitted &&
            reviewViewsPersisted;
    }

    private static Fixture BuildFixture(string source, int count)
    {
        Directory.CreateDirectory(source);
        var mediaRoot = Path.Combine(source, "media");
        var entries = Enumerable.Range(0, count).Select(number => new IndexEntry
        {
            Path = Path.Combine(mediaRoot, $"video-{number:D6}.mp4"),
            Size = 1024 + number,
            ModifiedTicks = DateTime.UtcNow.Ticks
        }).ToList();
        var metadata = new Dictionary<string, MovieMetadata>(StringComparer.Ordinal);
        for (var number = 0; number < count; number++)
        {
            var id = VideoId(entries[number].Path);
            metadata[id] = new MovieMetadata
            {
                Title = "合成作品 " + number.ToString("D6"),
                Code = "CASE-" + number.ToString("D6"),
                Actors = ["演员甲", "演员乙"]
            };
        }
        File.WriteAllBytes(Path.Combine(source, "library-index.json"),
            JsonSerializer.SerializeToUtf8Bytes(new LibraryIndex
                { Folders = [mediaRoot], Entries = entries }));
        File.WriteAllBytes(Path.Combine(source, "metadata.json"),
            JsonSerializer.SerializeToUtf8Bytes(metadata));

        var firstVideo = VideoId(entries[0].Path);
        var config = new LibraryConfig
        {
            Folders = [mediaRoot],
            Favorites = [firstVideo],
            Tags = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                { [firstVideo] = ["标签甲", "标签乙"] },
            Categories = new Dictionary<string, string>(StringComparer.Ordinal)
                { [firstVideo] = "电影" },
            AiReviews = new Dictionary<string, AiReviewRecord>(StringComparer.Ordinal)
                { [firstVideo] = new AiReviewRecord { Status = "pending" } }
        };
        File.WriteAllBytes(Path.Combine(source, "config.json"),
            JsonSerializer.SerializeToUtf8Bytes(config));
        File.WriteAllText(Path.Combine(source, "role-profiles.json"), """
            {"Version":1,"Revision":"1","Profiles":[
              {"Id":"11111111111111111111111111111111","Name":"人物甲","Aliases":["别名甲"],"Role":"演员","PersonKind":"female-actor"},
              {"Id":"22222222222222222222222222222222","Name":"人物乙","Aliases":[],"Role":"导演","PersonKind":"director"}]}
            """);
        File.WriteAllText(Path.Combine(source, "plugins.json"), """
            {"schemaVersion":1,"enabled":["sample-plugin"],
             "consents":{"sample-plugin":"signature"},"lastErrors":{}}
            """);
        File.WriteAllText(Path.Combine(source, "plugin-candidates.json"), "{\"videos\":{}}");
        File.WriteAllText(Path.Combine(source, "review-history.jsonl"),
            "{\"Id\":\"seed-review\",\"Action\":\"initial\"}\n");
        File.WriteAllText(Path.Combine(source, "personal-ratings.json"),
            "{}");
        Directory.CreateDirectory(Path.Combine(source, "ai-jobs"));
        File.WriteAllText(Path.Combine(source, "ai-jobs", new string('a', 32) + ".json"),
            "{\"job\":\"合成 AI 作业\",\"status\":\"queued\"}");
        Directory.CreateDirectory(Path.Combine(source, "title-candidates"));
        File.WriteAllText(Path.Combine(source, "title-candidates", new string('c', 64) + ".json"),
            "{\"candidate\":\"合成候选标题\"}");
        Directory.CreateDirectory(Path.Combine(source, "posters"));
        Directory.CreateDirectory(Path.Combine(source, "thumbs"));
        File.WriteAllBytes(Path.Combine(source, "posters", "sample.jpg"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(source, "thumbs", "sample-custom.png"), [5, 6, 7]);
        ScrapeQueueStore.Add(source,
        [
            new ScrapeQueueItem { VideoId = firstVideo, Code = "ABC-001" },
            new ScrapeQueueItem { VideoId = "queue-second-item", Code = "ABC-002" }
        ]);
        return new(count, firstVideo, mediaRoot);
    }

    private static void CopyLegacyFilesForActivation(string source, string library)
    {
        Directory.CreateDirectory(library);
        foreach (var name in new[] { "config.json", "library-index.json", "metadata.json" })
            File.Copy(Path.Combine(source, name), Path.Combine(library, name), overwrite: true);
        foreach (var folder in new[] { "posters", "thumbs" })
        {
            var target = Path.Combine(library, folder);
            Directory.CreateDirectory(target);
            foreach (var path in Directory.EnumerateFiles(Path.Combine(source, folder)))
                File.Copy(path, Path.Combine(target, Path.GetFileName(path)), overwrite: true);
        }
    }

    private static async Task MutateAsync(string connectionString, Fixture fixture)
    {
        var metadata = await PostgresLibraryMetadataStore.ReadAsync(connectionString);
        var updated = new Dictionary<string, MovieMetadata>(metadata.Records, StringComparer.Ordinal);
        updated[fixture.VideoId] = metadata.Records[fixture.VideoId].Clone();
        updated[fixture.VideoId].Title = "恢复前被修改的标题";
        updated[fixture.VideoId].Actors = ["演员甲", "演员乙", "演员丙"];
        await PostgresLibraryMetadataStore.SaveAsync(connectionString, updated, metadata.Revision);

        var index = await PostgresLibraryIndexStore.ReadAsync(connectionString);
        var mutatedIndex = new LibraryIndex
        {
            Folders = index.Index.Folders,
            Entries = [.. index.Index.Entries, new IndexEntry
            {
                Path = Path.Combine(fixture.MediaRoot, "video-added-after-backup.mp4"),
                Size = 4096, ModifiedTicks = DateTime.UtcNow.Ticks
            }]
        };
        await PostgresLibraryIndexStore.SaveAsync(connectionString, mutatedIndex, index.Revision);

        var configDocument = await PostgresLibraryDocumentStore.ReadAsync(connectionString,
            "config.json") ?? throw new InvalidDataException("合成库缺少配置。");
        var beforeConfig = JsonSerializer.Deserialize<LibraryConfig>(configDocument.Content)
            ?? throw new InvalidDataException("合成库配置无效。");
        var afterConfig = MainWindow.CloneLibraryConfig(beforeConfig);
        afterConfig.Favorites.Add("added-after-backup");
        afterConfig.Tags[fixture.VideoId] = ["标签甲", "标签乙", "标签丙"];
        await PostgresLibraryConfigStore.SaveAsync(connectionString, beforeConfig, afterConfig);

        var reviewDocument = await PostgresLibraryDocumentStore.ReadAsync(connectionString,
            "config.json") ?? throw new InvalidDataException("合成库缺少配置。");
        var reviewConfig = JsonSerializer.Deserialize<LibraryConfig>(reviewDocument.Content)!;
        var metadataNow = await PostgresLibraryMetadataStore.ReadAsync(connectionString);
        var reviewBefore = new ReviewItemSnapshot
        {
            Metadata = metadataNow.Records[fixture.VideoId].Clone(),
            Review = reviewConfig.AiReviews.GetValueOrDefault(fixture.VideoId),
            Tags = reviewConfig.Tags.GetValueOrDefault(fixture.VideoId)
        };
        var reviewAfter = reviewBefore.Clone();
        reviewAfter.Review = new AiReviewRecord { Status = "approved" };
        reviewAfter.Tags = ["标签甲", "标签乙", "标签丙", "审核通过"];
        var transaction = ReviewTransactionStore.Create("acceptance-approve",
            new Dictionary<string, ReviewItemSnapshot> { [fixture.VideoId] = reviewBefore },
            new Dictionary<string, ReviewItemSnapshot> { [fixture.VideoId] = reviewAfter });
        await PostgresReviewTransactionStore.CommitAsync(connectionString, transaction);
        var undo = ReviewTransactionStore.Create("acceptance-undo",
            new Dictionary<string, ReviewItemSnapshot> { [fixture.VideoId] = reviewAfter },
            new Dictionary<string, ReviewItemSnapshot> { [fixture.VideoId] = reviewBefore },
            undoesTransactionId: transaction.Id);
        await PostgresReviewTransactionStore.CommitAsync(connectionString, undo);

        await PostgresScrapeQueueStore.RemoveAsync(connectionString, "queue-second-item");
        await PostgresScrapeQueueStore.AddAsync(connectionString,
            [new ScrapeQueueItem { VideoId = "queue-third-item", Code = "ABC-003" }]);

        var plugins = await PostgresLibraryDocumentStore.ReadAsync(connectionString,
            "plugins.json") ?? throw new InvalidDataException("合成库缺少插件设置。");
        await PostgresLibraryDocumentStore.SaveAsync(connectionString, "plugins.json",
            Encoding.UTF8.GetBytes("""
                {"schemaVersion":1,"enabled":["sample-plugin","added-plugin"],
                 "consents":{"sample-plugin":true,"added-plugin":true},"lastErrors":{}}
                """), plugins.Revision);
        await PostgresLibraryDocumentStore.SaveAsync(connectionString,
            "ai-jobs/" + new string('d', 32) + ".json",
            Encoding.UTF8.GetBytes("{\"job\":\"备份后新增作业\",\"status\":\"queued\"}"), null);
        var profiles = await PostgresLibraryDocumentStore.ReadAsync(connectionString,
            "role-profiles.json") ?? throw new InvalidDataException("合成库缺少人物档案。");
        await PostgresLibraryDocumentStore.SaveAsync(connectionString, "role-profiles.json",
            Encoding.UTF8.GetBytes("""
                {"Version":1,"Revision":"2","Profiles":[
                  {"Id":"person-1","Name":"人物甲（已改名）","Aliases":["别名甲"],"Role":"演员","PersonKind":"演员"}]}
                """), profiles.Revision);
    }

    private static async Task<bool> HasMutatedStateAsync(string connectionString)
    {
        var index = await PostgresLibraryIndexStore.ReadAsync(connectionString);
        var metadata = await PostgresLibraryMetadataStore.ReadAsync(connectionString);
        return index.Index.Entries.Any(entry => entry.Path.EndsWith("video-added-after-backup.mp4",
                   StringComparison.Ordinal)) &&
            metadata.Records.Values.Any(record => record.Title == "恢复前被修改的标题");
    }

    private static async Task<bool> VerifyEntitiesAsync(string connectionString, Fixture fixture)
    {
        var metadata = await PostgresLibraryMetadataStore.ReadAsync(connectionString);
        var configDocument = await PostgresLibraryDocumentStore.ReadAsync(connectionString,
            "config.json");
        var plugins = await PostgresLibraryDocumentStore.ReadAsync(connectionString, "plugins.json");
        var profiles = await PostgresLibraryDocumentStore.ReadAsync(connectionString,
            "role-profiles.json");
        var jobs = await PostgresLibraryDocumentStore.ReadAsync(connectionString,
            "ai-jobs/" + new string('a', 32) + ".json");
        var queue = await PostgresScrapeQueueStore.LoadAsync(connectionString);
        var config = configDocument is null ? null :
            JsonSerializer.Deserialize<LibraryConfig>(configDocument.Content);
        return metadata.Records[fixture.VideoId].Title == "合成作品 000000" &&
            metadata.Records[fixture.VideoId].Actors.SequenceEqual(["演员甲", "演员乙"]) &&
            config?.Tags[fixture.VideoId].SequenceEqual(["标签甲", "标签乙"]) is true &&
            config.AiReviews[fixture.VideoId].Status == "pending" &&
            config.Favorites.SequenceEqual([fixture.VideoId]) &&
            queue.Select(item => item.Code).SequenceEqual(["ABC-001", "ABC-002"]) &&
            plugins is not null && Encoding.UTF8.GetString(plugins.Content).Contains(
                "\"sample-plugin\"", StringComparison.Ordinal) &&
            !Encoding.UTF8.GetString(plugins.Content).Contains("added-plugin",
                StringComparison.Ordinal) &&
            jobs is not null && Encoding.UTF8.GetString(jobs.Content).Contains("合成 AI 作业",
                StringComparison.Ordinal) &&
            profiles is not null && Encoding.UTF8.GetString(profiles.Content).Contains("人物甲",
                StringComparison.Ordinal) &&
            !Encoding.UTF8.GetString(profiles.Content).Contains("已改名", StringComparison.Ordinal);
    }

    private static async Task<FailureResult> RunFailureCasesAsync(string runtimeDirectory,
        string probeRoot)
    {
        var source = Path.Combine(probeRoot, "failure-source");
        var library = Path.Combine(probeRoot, "failure-library");
        var backup = Path.Combine(probeRoot, "failure-backup");
        var fixture = BuildFixture(source, 300);
        var cluster = await PostgresPrivateCluster.OpenAsync(runtimeDirectory, library);
        try
        {
            CopyLegacyFilesForActivation(source, library);
            await PostgresLibraryMigration.ImportAsync(source, cluster.ConnectionString);
            await PostgresLibraryBackup.CreateAsync(cluster.ConnectionString, library, backup);
            await PostgresStorageModeActivation.ActivateAsync(cluster.ConnectionString, library, backup);
            await MutateAsync(cluster.ConnectionString, fixture);
            var mutatedBackup = Path.Combine(probeRoot, "failure-mutated-snapshot");

            Log(probeRoot, "failure 1");
            // 1. Interrupt a backup while it is copying: no final directory and
            //    no staging directory may survive.
            var interruptedTarget = Path.Combine(probeRoot, "failure-interrupted-backup");
            using var cancel = new CancellationTokenSource();
            var backupTask = Task.Run(() => PostgresLibraryBackup.CreateAsync(
                cluster.ConnectionString, library, interruptedTarget, cancel.Token));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline &&
                   !Directory.EnumerateDirectories(probeRoot, "failure-interrupted-backup.partial-*").Any())
                await Task.Delay(10);
            cancel.Cancel();
            var interruptedClean = false;
            try { await backupTask; }
            catch (OperationCanceledException)
            {
                interruptedClean = !Directory.Exists(interruptedTarget) &&
                    !Directory.EnumerateDirectories(probeRoot,
                        "failure-interrupted-backup.partial-*").Any();
            }

            Log(probeRoot, "failure 2");
            // 2. Space precheck rejects an impossible request before any write.
            var spaceRejected = false;
            try
            {
                PostgresLibraryExport.EnsureAvailableSpace(
                    Path.Combine(probeRoot, "failure-space"), long.MaxValue / 2);
            }
            catch (IOException) { spaceRejected = true; }
            spaceRejected &= !Directory.Exists(Path.Combine(probeRoot, "failure-space"));

            Log(probeRoot, "failure 3");
            // 3. Corrupt package: rejected before the sentinel or any write.
            var corrupt = Path.Combine(probeRoot, "failure-corrupt");
            CopyDirectory(backup, corrupt);
            var metadataPath = Path.Combine(corrupt, "metadata.json");
            var metadataBytes = await File.ReadAllBytesAsync(metadataPath);
            metadataBytes[0] ^= 0xFF;
            await File.WriteAllBytesAsync(metadataPath, metadataBytes);
            var corruptRejected = false;
            try { await PostgresLibraryRestore.RestoreInPlaceAsync(cluster.ConnectionString,
                library, corrupt); }
            catch (InvalidDataException) { corruptRejected = true; }
            corruptRejected &= PostgresStorageModeGuard.Current(library) ==
                PostgresStorageModeGuard.StorageMode.PostgreSql &&
                !File.Exists(Path.Combine(library, PostgresStorageModeGuard.RestoreSentinelName));

            Log(probeRoot, "failure 4");
            // 4. Old package version: explicit refusal, not a generic corruption.
            var oldPackage = Path.Combine(probeRoot, "failure-old-package");
            CopyDirectory(backup, oldPackage);
            var manifestPath = Path.Combine(oldPackage, PostgresLibraryExport.ManifestName);
            var manifest = JsonSerializer.Deserialize<PostgresLibraryExport.Manifest>(
                await File.ReadAllBytesAsync(manifestPath))!;
            await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(
                manifest with { SchemaVersion = 0 }));
            var oldPackageRejected = !await PostgresLibraryExport.VerifyOfflineAsync(oldPackage);

            Log(probeRoot, "failure 5");
            // 5. Old database shape is upgraded, a newer one is refused.
            var oldDatabase = Path.Combine(probeRoot, "failure-old-database");
            var oldCluster = await PostgresPrivateCluster.OpenAsync(runtimeDirectory, oldDatabase);
            bool oldUpgraded;
            bool newerRefused;
            try
            {
                await PostgresLibraryMigration.ImportAsync(source, oldCluster.ConnectionString);
                await ExecuteAsync(oldCluster.ConnectionString,
                    "DROP TABLE library_schema");
                var legacy = await PostgresLibraryMigration.InspectSchemaAsync(
                    oldCluster.ConnectionString);
                await PostgresLibraryMigration.UpgradeSchemaAsync(oldCluster.ConnectionString);
                var current = await PostgresLibraryMigration.InspectSchemaAsync(
                    oldCluster.ConnectionString);
                oldUpgraded = legacy.State == PostgresLibraryMigration.SchemaState.Legacy &&
                    current.State == PostgresLibraryMigration.SchemaState.Current;
                await ExecuteAsync(oldCluster.ConnectionString,
                    "UPDATE library_schema SET version=99 WHERE id=1");
                var newer = await PostgresLibraryMigration.InspectSchemaAsync(
                    oldCluster.ConnectionString);
                var refused = false;
                try { await PostgresLibraryMigration.UpgradeSchemaAsync(oldCluster.ConnectionString); }
                catch (NotSupportedException) { refused = true; }
                newerRefused = newer.State == PostgresLibraryMigration.SchemaState.Newer && refused;
            }
            finally { await oldCluster.DisposeAsync(); }

            Log(probeRoot, "failure 6");
            // 6. Interrupted restore: staging import succeeded, swap never ran.
            var interruptedRestore = false;
            try
            {
                await PostgresLibraryRestore.RestoreInPlaceAsync(cluster.ConnectionString,
                    library, backup, default,
                    interruptBeforeSwap: () => throw new OperationCanceledException());
            }
            catch (OperationCanceledException)
            {
                interruptedRestore = await HasMutatedStateAsync(cluster.ConnectionString) &&
                    PostgresStorageModeGuard.Current(library) ==
                        PostgresStorageModeGuard.StorageMode.PostgreSql;
            }
            // Simulate a killed process: sentinel plus a leftover staging schema.
            PostgresStorageModeGuard.BeginRestore(library,
                "yingge_restore_deadbeef", "yingge_prev_deadbeef");
            await ExecuteAsync(cluster.ConnectionString,
                "CREATE SCHEMA IF NOT EXISTS yingge_restore_deadbeef");
            var cleaned = await PostgresLibraryRestore.CleanupInterruptedAsync(
                cluster.ConnectionString, library);
            var cleanedOk = cleaned && !File.Exists(Path.Combine(library,
                PostgresStorageModeGuard.RestoreSentinelName)) &&
                PostgresStorageModeGuard.PostgresMayOpen(library);

            Log(probeRoot, "failure 7");
            // 7. Failed activation leaves no marker at all.
            var markerlessLibrary = Path.Combine(probeRoot, "failure-markerless");
            var markerlessCluster = await PostgresPrivateCluster.OpenAsync(runtimeDirectory,
                markerlessLibrary);
            bool markerlessRejected;
            try
            {
                await PostgresLibraryMigration.ImportAsync(source, markerlessCluster.ConnectionString);
                var emptyBackup = Path.Combine(probeRoot, "failure-markerless-backup");
                Directory.CreateDirectory(emptyBackup);
                try
                {
                    await PostgresStorageModeActivation.ActivateAsync(
                        markerlessCluster.ConnectionString, markerlessLibrary, emptyBackup);
                }
                catch (InvalidDataException) { }
                markerlessRejected = !File.Exists(Path.Combine(markerlessLibrary,
                    PostgresStorageModeGuard.MarkerName)) &&
                    PostgresStorageModeGuard.Current(markerlessLibrary) ==
                        PostgresStorageModeGuard.StorageMode.LegacyFiles;
            }
            finally { await markerlessCluster.DisposeAsync(); }

            Log(probeRoot, "failure 8");
            // 8. Failure after the schema swap must put the old database back.
            //    Snapshot the mutated state first so the comparison is exact.
            await PostgresLibraryBackup.CreateAsync(cluster.ConnectionString, library, mutatedBackup);
            var reverted = false;
            try
            {
                await PostgresLibraryRestore.RestoreInPlaceAsync(cluster.ConnectionString,
                    library, backup, default, interruptAfterSwap: () =>
                        throw new InvalidDataException("注入：替换后失败"));
            }
            catch (InvalidDataException)
            {
                reverted = await PostgresLibraryMigration.VerifyContentAsync(mutatedBackup,
                        cluster.ConnectionString) &&
                    PostgresStorageModeGuard.Current(library) ==
                        PostgresStorageModeGuard.StorageMode.PostgreSql &&
                    !File.Exists(Path.Combine(library,
                        PostgresStorageModeGuard.RestoreSentinelName));
            }
            Log(probeRoot, "failure 9");
            // 9. The sentinel blocks both storage modes while it exists.
            PostgresStorageModeGuard.BeginRestore(library);
            var sentinelBlocks = PostgresStorageModeGuard.Current(library) ==
                    PostgresStorageModeGuard.StorageMode.Blocked &&
                !PostgresStorageModeGuard.LegacyFilesMayOpen(library) &&
                !PostgresStorageModeGuard.PostgresMayOpen(library);
            PostgresStorageModeGuard.EndRestore(library);

            Log(probeRoot, "failure 10");
            // Marker replacement is also part of the restore transaction. A
            // failure after that step must restore its exact original bytes.
            var markerPath = Path.Combine(library, PostgresStorageModeGuard.MarkerName);
            var originalMarker = await File.ReadAllBytesAsync(markerPath);
            var markerReverted = false;
            try
            {
                await PostgresLibraryRestore.RestoreInPlaceAsync(cluster.ConnectionString,
                    library, backup, interruptAfterMarker: () =>
                        throw new InvalidDataException("注入：标记改写后失败"));
            }
            catch (InvalidDataException)
            {
                markerReverted = (await File.ReadAllBytesAsync(markerPath)).SequenceEqual(originalMarker) &&
                    await PostgresLibraryMigration.VerifyContentAsync(mutatedBackup,
                        cluster.ConnectionString) &&
                    PostgresStorageModeGuard.PostgresMayOpen(library);
            }

            Log(probeRoot, "failure 11");
            var assetSwapReverted = false;
            try
            {
                await PostgresLibraryRestore.RestoreInPlaceAsync(cluster.ConnectionString,
                    library, backup, interruptDuringAssetSwap: () =>
                        throw new InvalidDataException("注入：图片目录切换到一半"));
            }
            catch (InvalidDataException)
            {
                assetSwapReverted =
                    File.ReadAllBytes(Path.Combine(library, "posters", "sample.jpg"))
                        .SequenceEqual(new byte[] { 1, 2, 3, 4 }) &&
                    File.ReadAllBytes(Path.Combine(library, "thumbs", "sample-custom.png"))
                        .SequenceEqual(new byte[] { 5, 6, 7 }) &&
                    await PostgresLibraryMigration.VerifyContentAsync(mutatedBackup,
                        cluster.ConnectionString) &&
                    PostgresStorageModeGuard.PostgresMayOpen(library);
            }

            Log(probeRoot, "failure 12");
            await ExecuteAsync(cluster.ConnectionString, "CREATE SCHEMA yingge_restore_missing_table");
            var missingTableRejected = false;
            try
            {
                missingTableRejected = !await PostgresLibraryMigration.VerifyContentAsync(
                    backup, cluster.ConnectionString, default, "yingge_restore_missing_table");
            }
            catch (PostgresException ex) when (ex.SqlState == "42P01")
            {
                missingTableRejected = true;
            }
            finally
            {
                await ExecuteAsync(cluster.ConnectionString,
                    "DROP SCHEMA yingge_restore_missing_table CASCADE");
            }

            Log(probeRoot, "failure 13");
            var pendingState = new MissingCleanupState { PendingId = "pending-probe",
                History = [new MissingCleanupRecord { Id = "pending-probe",
                    StartedAt = DateTimeOffset.UtcNow,
                    Entries = [new MissingCleanupEntry { Path = Path.Combine(fixture.MediaRoot,
                        "video-000000.mp4"), Size = 1024, ModifiedTicks = DateTime.UtcNow.Ticks }] }] };
            var priorCleanup = await PostgresLibraryDocumentStore.ReadAsync(
                cluster.ConnectionString, MissingCleanupStore.FileName);
            await PostgresLibraryDocumentStore.SaveAsync(cluster.ConnectionString,
                MissingCleanupStore.FileName, JsonSerializer.SerializeToUtf8Bytes(pendingState),
                priorCleanup?.Revision);
            var pendingBackup = Path.Combine(probeRoot, "failure-pending-cleanup-backup");
            await PostgresLibraryBackup.CreateAsync(cluster.ConnectionString, library, pendingBackup);
            var markerBeforePending = await File.ReadAllBytesAsync(markerPath);
            var pendingCleanupBlocks = false;
            try
            {
                await PostgresStorageModeActivation.RewriteAfterRestoreAsync(
                    cluster.ConnectionString, library, pendingBackup);
            }
            catch (InvalidOperationException)
            {
                pendingCleanupBlocks = (await File.ReadAllBytesAsync(markerPath))
                    .SequenceEqual(markerBeforePending);
            }

            return new(interruptedClean, spaceRejected, corruptRejected, oldPackageRejected,
                oldUpgraded, newerRefused, interruptedRestore, cleanedOk, markerlessRejected,
                reverted, sentinelBlocks, markerReverted, assetSwapReverted,
                missingTableRejected, pendingCleanupBlocks);
        }
        finally
        {
            Log(probeRoot, "failure teardown start");
            try { await cluster.DisposeAsync(); Log(probeRoot, "failure teardown done"); }
            catch (Exception ex)
            {
                Log(probeRoot, "failure teardown error " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }
    }

    private static string VideoId(string path) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..24].ToLowerInvariant();
}

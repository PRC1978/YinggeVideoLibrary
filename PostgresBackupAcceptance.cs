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
        bool EntitiesVerified, long ImportMs, long BackupMs, long RestoreMs, long BackupBytes);

    internal sealed record FailureResult(bool InterruptedBackupClean, bool SpacePrecheckRejected,
        bool CorruptPackageRejected, bool OldPackageRejected, bool OldDatabaseUpgraded,
        bool NewerDatabaseRefused, bool InterruptedRestoreKeptOldData, bool InterruptedRestoreCleaned,
        bool FailedActivationLeftNoMarker, bool FailedRestoreReverted, bool SentinelBlocksBothModes,
        bool FailedAfterMarkerRestoredOriginal);

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
                item.MarkerRewritten && item.RestartVerified && item.EntitiesVerified);

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
                failures.FailedAfterMarkerRestoredOriginal;
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

            var reopened = await PostgresPrivateCluster.OpenAsync(runtimeDirectory, library);
            bool restartVerified;
            try
            {
                restartVerified = PostgresStorageModeGuard.PostgresMayOpen(library) &&
                    await PostgresLibraryMigration.VerifyContentAsync(backup,
                        reopened.ConnectionString) &&
                    await VerifyEntitiesAsync(reopened.ConnectionString, videos);
            }
            finally { await reopened.DisposeAsync(); }

            return new(count, importedOk, backupOk, markerOk, mutationApplied, restored,
                archiveVerified, previousSchemaVerified, markerRewritten, restartVerified,
                entities, importMs, backupMs, restoreMs,
                PostgresLibraryBackup.DirectorySize(backup));
        }
        catch
        {
            try { await cluster.DisposeAsync(); } catch { }
            throw;
        }
    }

    private sealed record Fixture(int Videos, string VideoId, string MediaRoot);

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
              {"Id":"person-1","Name":"人物甲","Aliases":["别名甲"],"Role":"演员","PersonKind":"演员"},
              {"Id":"person-2","Name":"人物乙","Aliases":[],"Role":"导演","PersonKind":"导演"}]}
            """);
        File.WriteAllText(Path.Combine(source, "plugins.json"), """
            {"schemaVersion":1,"enabled":["sample-plugin"],
             "consents":{"sample-plugin":true},"lastErrors":{}}
            """);
        File.WriteAllText(Path.Combine(source, "plugin-candidates.json"), "{\"videos\":{}}");
        File.WriteAllText(Path.Combine(source, "review-history.jsonl"),
            "{\"Id\":\"seed-review\",\"Action\":\"initial\"}\n");
        File.WriteAllText(Path.Combine(source, "personal-ratings.json"),
            "{\"schemaVersion\":1,\"ratings\":{}}");
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
            PostgresStorageModeGuard.BeginRestore(library);
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

            return new(interruptedClean, spaceRejected, corruptRejected, oldPackageRejected,
                oldUpgraded, newerRefused, interruptedRestore, cleanedOk, markerlessRejected,
                reverted, sentinelBlocks, markerReverted);
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

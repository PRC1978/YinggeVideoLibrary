using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace YinggeVideoLibrary;

// An isolated runtime probe. Normal startup must not use this until all JSON
// writers and backup/restore have been switched together.
internal static class BundledPostgresRuntime
{
    internal static async Task<int> RunProbeToFileAsync(string runtimeDirectory, string resultPath,
        bool scale = false)
    {
        var findings = new Dictionary<string, object?>();
        var probeRoot = Path.Combine(Path.GetTempPath(), "YinggePgProbe-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(probeRoot, "data");
        var passwordFile = Path.Combine(probeRoot, "init-password.txt");
        var credentialName = "PostgresProbe-" + Guid.NewGuid().ToString("N");
        var privateLibrary = Path.Combine(probeRoot, "private-library");
        var scaleLibrary = Path.Combine(probeRoot, "scale-library");
        var ctl = Path.Combine(runtimeDirectory, "bin", "pg_ctl.exe");
        var initdb = Path.Combine(runtimeDirectory, "bin", "initdb.exe");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var started = false;
        try
        {
            if (!File.Exists(initdb) || !File.Exists(ctl) ||
                !File.Exists(Path.Combine(runtimeDirectory, "share", "postgres.bki")))
                throw new InvalidDataException("PostgreSQL runtime is incomplete.");
            Directory.CreateDirectory(probeRoot);
            // Only a random test secret is temporarily written. The regular
            // user credential must live in Windows Credential Manager.
            await File.WriteAllTextAsync(passwordFile, password + Environment.NewLine,
                new UTF8Encoding(false));
            var initialized = await RunAsync(initdb, ["-D", data, "-U", "yingge_owner",
                "--auth-host=scram-sha-256", "--auth-local=scram-sha-256",
                "--encoding=UTF8", "--no-locale", "--pwfile=" + passwordFile,
                "--no-instructions"], runtimeDirectory);
            if (initialized.ExitCode != 0) throw new InvalidOperationException(
                "initdb failed: " + Tail(initialized.Error + initialized.Output));
            WindowsCredentialStore.Write(credentialName, password);
            File.Delete(passwordFile);

            // Hold a socket to choose a free loopback port, then release it
            // immediately before pg_ctl starts. Connection verification below
            // catches the unlikely race with another local process.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var startedResult = await RunAsync(ctl, ["-D", data, "-l", Path.Combine(probeRoot, "server.log"),
                "-o", $"-h 127.0.0.1 -p {port}", "-w", "start"], runtimeDirectory,
                captureOutput: false);
            if (startedResult.ExitCode != 0) throw new InvalidOperationException(
                "pg_ctl start failed: " + Tail(startedResult.Error + startedResult.Output));
            started = true;
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1", Port = port, Username = "yingge_owner",
                Password = WindowsCredentialStore.Read(credentialName), Database = "postgres",
                Pooling = false, Timeout = 30, SslMode = SslMode.Disable
            };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT current_setting('server_version'), host(inet_server_addr()), current_setting('listen_addresses')",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidDataException("PostgreSQL did not answer.");
            var version = reader.GetString(0);
            var address = reader.GetString(1);
            var listenAddresses = reader.GetString(2);
            findings["version"] = version;
            findings["serverAddress"] = address;
            findings["listenAddresses"] = listenAddresses;
            findings["loopbackOnly"] = address == "127.0.0.1" && listenAddresses == "127.0.0.1";
            findings["credentialRoundTrip"] = WindowsCredentialStore.Read(credentialName) == password;
            var sourceDirectory = Path.Combine(probeRoot, "source");
            Directory.CreateDirectory(sourceDirectory);
            var samplePath = Path.Combine(probeRoot, "media", "sample.mp4");
            var videoId = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(samplePath.ToLowerInvariant())))[..24].ToLowerInvariant();
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "library-index.json"),
                JsonSerializer.Serialize(new LibraryIndex
                {
                    Folders = [Path.GetDirectoryName(samplePath)!],
                    Entries = [new IndexEntry { Path = samplePath, Size = 42,
                        ModifiedTicks = DateTime.UtcNow.Ticks }]
                }));
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "metadata.json"),
                JsonSerializer.Serialize(new Dictionary<string, MovieMetadata>
                    { [videoId] = new MovieMetadata { Title = "isolated sample" } }));
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "config.json"),
                "{\"folders\":[],\"categories\":[\"isolated\"]}");
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "scrape-queue.json"),
                "[]");
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "role-profiles.json"),
                "{\"profiles\":[]}");
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "plugins.json"),
                "{\"schemaVersion\":1,\"enabled\":[],\"consents\":{},\"lastErrors\":{}}");
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "plugin-candidates.json"),
                "{\"videos\":{}}");
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "missing-cleanup.json"),
                "{\"records\":[]}");
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "review-history.jsonl"),
                "{\"id\":\"isolated-review\"}\n");
            ScrapeQueueStore.Add(sourceDirectory,
                [new ScrapeQueueItem { VideoId = videoId, Code = "ABC-001" }]);
            // The second task lives only in the post-checkpoint binary journal.
            ScrapeQueueStore.Add(sourceDirectory,
                [new ScrapeQueueItem { VideoId = "queue-second-item", Code = "ABC-002" }]);
            Directory.CreateDirectory(Path.Combine(sourceDirectory, "posters"));
            Directory.CreateDirectory(Path.Combine(sourceDirectory, "thumbs"));
            await File.WriteAllBytesAsync(Path.Combine(sourceDirectory, "posters", "sample.jpg"), [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(Path.Combine(sourceDirectory, "thumbs", "sample-custom.png"), [5, 6, 7]);
            Directory.CreateDirectory(Path.Combine(sourceDirectory, "title-candidates"));
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "title-candidates",
                new string('A', 64) + ".json"), "{\"candidate\":\"isolated\"}");
            Directory.CreateDirectory(Path.Combine(sourceDirectory, "ai-jobs"));
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "ai-jobs",
                new string('b', 32) + ".json"), "{\"job\":\"isolated\"}");
            var legacyBeforeCutover = PostgresStorageModeGuard.LegacyFilesMayOpen(sourceDirectory);
            var modeMarker = Path.Combine(sourceDirectory, PostgresStorageModeGuard.MarkerName);
            await File.WriteAllTextAsync(modeMarker, "postgresql");
            var legacyAfterCutover = PostgresStorageModeGuard.LegacyFilesMayOpen(sourceDirectory);
            File.Delete(modeMarker);
            findings["legacyModeGuardVerified"] = legacyBeforeCutover && !legacyAfterCutover &&
                PostgresStorageModeGuard.LegacyFilesMayOpen(sourceDirectory);
            var activeSessionBlocked = false;
            using (LibraryBackupService.AcquireSessionLease(sourceDirectory))
            {
                try { await PostgresLibraryMigration.ImportAsync(sourceDirectory, builder.ConnectionString); }
                catch (IOException) { activeSessionBlocked = true; }
            }
            findings["activeSessionImportBlocked"] = activeSessionBlocked;
            var imported = await PostgresLibraryMigration.ImportAsync(sourceDirectory, builder.ConnectionString);
            var verified = await PostgresLibraryMigration.VerifyContentAsync(sourceDirectory, builder.ConnectionString);
            findings["sampleImportVerified"] = imported.VideoCount == 1 &&
                imported.MetadataCount == 1 && verified;
            var exportDirectory = Path.Combine(probeRoot, "exported-records");
            var exported = await PostgresLibraryExport.ExportAsync(
                builder.ConnectionString, exportDirectory, assetDirectory: sourceDirectory);
            var exportVerified = await PostgresLibraryMigration.VerifyContentAsync(
                exportDirectory, builder.ConnectionString);
            var offlineVerified = await PostgresLibraryExport.VerifyOfflineAsync(exportDirectory);
            var exportedIndexPath = Path.Combine(exportDirectory, "library-index.json");
            var originalIndexBytes = await File.ReadAllBytesAsync(exportedIndexPath);
            await File.WriteAllTextAsync(exportedIndexPath, "{}");
            var tamperRejected = !await PostgresLibraryExport.VerifyOfflineAsync(exportDirectory);
            await File.WriteAllBytesAsync(exportedIndexPath, originalIndexBytes);
            offlineVerified &= await PostgresLibraryExport.VerifyOfflineAsync(exportDirectory);
            var overwriteBlocked = false;
            try { await PostgresLibraryExport.ExportAsync(builder.ConnectionString, exportDirectory); }
            catch (IOException) { overwriteBlocked = true; }
            findings["rollbackExportDocumentCount"] = exported.DocumentCount;
            findings["rollbackExportContentVerified"] = exportVerified;
            findings["rollbackOfflineVerified"] = offlineVerified;
            findings["rollbackExportVerified"] = exported.VideoCount == imported.VideoCount &&
                exported.MetadataCount == imported.MetadataCount && exported.DocumentCount >= 9 &&
                exportVerified && offlineVerified && tamperRejected && overwriteBlocked;
            long queueRows;
            await using var queueConnection = new NpgsqlConnection(builder.ConnectionString);
            await queueConnection.OpenAsync();
            await using (var queueCount = new NpgsqlCommand(
                "SELECT count(*) FROM library_scrape_queue", queueConnection))
                queueRows = (long)(await queueCount.ExecuteScalarAsync() ?? 0L);
            ScrapeQueueStore.ForgetCachedStateAfterOfflineRestore(sourceDirectory);
            findings["queueJournalMigrationVerified"] =
                queueRows == 2 &&
                ScrapeQueueStore.Load(sourceDirectory).Count == 2 &&
                ScrapeQueueStore.Load(exportDirectory).Select(item => item.Code)
                    .SequenceEqual(["ABC-001", "ABC-002"]) &&
                !File.Exists(Path.Combine(exportDirectory, "scrape-queue.journal.00000001.bin"));
            var removedQueueItem = await PostgresScrapeQueueStore.RemoveAsync(
                builder.ConnectionString, "queue-second-item");
            var addedQueueItem = await PostgresScrapeQueueStore.AddAsync(
                builder.ConnectionString,
                [new ScrapeQueueItem { VideoId = "queue-third-item", Code = "ABC-003" }]);
            var mutatedQueueExport = Path.Combine(probeRoot, "mutated-queue-export");
            await PostgresLibraryExport.ExportAsync(builder.ConnectionString, mutatedQueueExport);
            findings["queueMutationRollbackVerified"] = removedQueueItem && addedQueueItem == 1 &&
                (await PostgresScrapeQueueStore.LoadAsync(builder.ConnectionString))
                    .Select(item => item.Code).SequenceEqual(["ABC-001", "ABC-003"]) &&
                ScrapeQueueStore.Load(mutatedQueueExport).Select(item => item.Code)
                    .SequenceEqual(["ABC-001", "ABC-003"]) &&
                await PostgresLibraryExport.VerifyOfflineAsync(mutatedQueueExport) &&
                await PostgresLibraryMigration.VerifyContentAsync(
                    mutatedQueueExport, builder.ConnectionString);
            findings["assetBackupVerified"] =
                (await File.ReadAllBytesAsync(Path.Combine(exportDirectory, "posters", "sample.jpg")))
                    .SequenceEqual(new byte[] { 1, 2, 3, 4 }) &&
                (await File.ReadAllBytesAsync(Path.Combine(exportDirectory, "thumbs", "sample-custom.png")))
                    .SequenceEqual(new byte[] { 5, 6, 7 });
            var exportedPosterPath = Path.Combine(exportDirectory, "posters", "sample.jpg");
            await File.WriteAllBytesAsync(exportedPosterPath, [9, 9, 9]);
            var alteredAssetRejected = !await PostgresLibraryExport.VerifyOfflineAsync(exportDirectory);
            await File.WriteAllBytesAsync(exportedPosterPath, [1, 2, 3, 4]);
            findings["assetBackupVerified"] = findings["assetBackupVerified"] is true &&
                alteredAssetRejected && await PostgresLibraryExport.VerifyOfflineAsync(exportDirectory);
            var failedExportDirectory = Path.Combine(probeRoot, "failed-export");
            var lockedAssetRejected = false;
            using (var lockedPoster = new FileStream(Path.Combine(sourceDirectory,
                "posters", "sample.jpg"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { await PostgresLibraryExport.ExportAsync(builder.ConnectionString,
                    failedExportDirectory, assetDirectory: sourceDirectory); }
                catch (IOException) { lockedAssetRejected = true; }
            }
            findings["failedExportLeavesNoPartialBackup"] = lockedAssetRejected &&
                !Directory.Exists(failedExportDirectory) &&
                !Directory.EnumerateDirectories(probeRoot, "failed-export.partial-*").Any();
            findings["nestedDocumentPathsVerified"] =
                PostgresLibraryMigration.IsDocumentName("title-candidates/" + new string('A', 64) + ".json") &&
                PostgresLibraryMigration.IsDocumentName("ai-jobs/" + new string('b', 32) + ".json") &&
                !PostgresLibraryMigration.IsDocumentName("ai-jobs/../config.json") &&
                !PostgresLibraryMigration.IsDocumentName("title-candidates/" + new string('A', 63) + "Z.json") &&
                File.Exists(Path.Combine(exportDirectory, "title-candidates", new string('A', 64) + ".json")) &&
                File.Exists(Path.Combine(exportDirectory, "ai-jobs", new string('b', 32) + ".json"));
            var configDocument = await PostgresLibraryDocumentStore.ReadAsync(
                builder.ConnectionString, "config.json");
            var revisedConfig = Encoding.UTF8.GetBytes("{\"folders\":[],\"categories\":[\"verified\"]}");
            var revisedHash = await PostgresLibraryDocumentStore.SaveAsync(
                builder.ConnectionString, "config.json", revisedConfig,
                configDocument?.Revision);
            var reloadedConfig = await PostgresLibraryDocumentStore.ReadAsync(
                builder.ConnectionString, "config.json");
            var staleWriteRejected = false;
            try
            {
                await PostgresLibraryDocumentStore.SaveAsync(builder.ConnectionString,
                    "config.json", Encoding.UTF8.GetBytes("{}"), configDocument?.Revision);
            }
            catch (InvalidOperationException) { staleWriteRejected = true; }
            findings["stateDocumentCasVerified"] = configDocument is not null &&
                reloadedConfig?.Revision == revisedHash &&
                reloadedConfig.Content.SequenceEqual(revisedConfig) && staleWriteRejected;
            var metadataBeforeReview = await PostgresLibraryMetadataStore.ReadAsync(builder.ConnectionString);
            var historyBeforeReview = await PostgresLibraryDocumentStore.ReadAsync(
                builder.ConnectionString, "review-history.jsonl");
            var reviewMetadata = new Dictionary<string, MovieMetadata>(metadataBeforeReview.Records,
                StringComparer.Ordinal) { [videoId] = new MovieMetadata { Title = "review approved" } };
            var reviewConfig = Encoding.UTF8.GetBytes("{\"folders\":[],\"review\":\"approved\"}");
            var reviewHistory = Encoding.UTF8.GetBytes("{\"id\":\"isolated-review\"}\n{\"id\":\"approved\"}\n");
            var reviewRevision = await PostgresLibraryAtomicStateStore.CommitAsync(
                builder.ConnectionString, reviewMetadata, metadataBeforeReview.Revision,
                [new("config.json", reviewConfig, reloadedConfig?.Revision),
                 new("review-history.jsonl", reviewHistory, historyBeforeReview?.Revision)]);
            var metadataAfterReview = await PostgresLibraryMetadataStore.ReadAsync(builder.ConnectionString);
            var configAfterReview = await PostgresLibraryDocumentStore.ReadAsync(
                builder.ConnectionString, "config.json");
            var historyAfterReview = await PostgresLibraryDocumentStore.ReadAsync(
                builder.ConnectionString, "review-history.jsonl");
            var staleReviewRejected = false;
            try
            {
                await PostgresLibraryAtomicStateStore.CommitAsync(builder.ConnectionString,
                    new Dictionary<string, MovieMetadata>(StringComparer.Ordinal)
                        { [videoId] = new MovieMetadata { Title = "must roll back" } }, reviewRevision,
                    [new("config.json", Encoding.UTF8.GetBytes("{}"), reloadedConfig?.Revision)]);
            }
            catch (InvalidOperationException) { staleReviewRejected = true; }
            var metadataAfterRejection = await PostgresLibraryMetadataStore.ReadAsync(builder.ConnectionString);
            var lateFailureRejected = false;
            try
            {
                await PostgresLibraryAtomicStateStore.CommitAsync(builder.ConnectionString,
                    new Dictionary<string, MovieMetadata>(StringComparer.Ordinal)
                        { [videoId] = new MovieMetadata { Title = "must also roll back" } }, reviewRevision,
                    [new("config.json", Encoding.UTF8.GetBytes("{\"review\":\"wrong\"}"),
                        configAfterReview?.Revision),
                     new("review-history.jsonl", Encoding.UTF8.GetBytes("wrong"),
                        historyBeforeReview?.Revision)]);
            }
            catch (InvalidOperationException) { lateFailureRejected = true; }
            var configAfterLateFailure = await PostgresLibraryDocumentStore.ReadAsync(
                builder.ConnectionString, "config.json");
            var metadataAfterLateFailure = await PostgresLibraryMetadataStore.ReadAsync(builder.ConnectionString);
            findings["atomicReviewStateVerified"] = metadataAfterReview.Revision == reviewRevision &&
                metadataAfterReview.Records[videoId].Title == "review approved" &&
                configAfterReview?.Content.SequenceEqual(reviewConfig) is true &&
                historyAfterReview?.Content.SequenceEqual(reviewHistory) is true &&
                staleReviewRejected && metadataAfterRejection.Revision == reviewRevision &&
                metadataAfterRejection.Records[videoId].Title == "review approved" &&
                lateFailureRejected && configAfterLateFailure?.Content.SequenceEqual(reviewConfig) is true &&
                metadataAfterLateFailure.Revision == reviewRevision &&
                metadataAfterLateFailure.Records[videoId].Title == "review approved";
            // Exercise the typed review writer, including reload and stale-preview rejection.
            var typedConfigNode = JsonSerializer.SerializeToNode(new LibraryConfig())!.AsObject();
            typedConfigNode["futureSetting"] = "preserve-me";
            var typedConfig = JsonSerializer.SerializeToUtf8Bytes(typedConfigNode);
            await PostgresLibraryDocumentStore.SaveAsync(builder.ConnectionString,
                "config.json", typedConfig, configAfterReview?.Revision);
            var beforeState = new ReviewItemSnapshot
            {
                Metadata = metadataAfterLateFailure.Records[videoId].Clone()
            };
            var afterState = beforeState.Clone();
            afterState.Review = new AiReviewRecord { Status = "approved" };
            afterState.Tags = ["verified"];
            var typedReview = ReviewTransactionStore.Create("field-approve",
                new Dictionary<string, ReviewItemSnapshot> { [videoId] = beforeState },
                new Dictionary<string, ReviewItemSnapshot> { [videoId] = afterState });
            await PostgresReviewTransactionStore.CommitAsync(builder.ConnectionString, typedReview);
            var typedConfigAfter = await PostgresLibraryDocumentStore.ReadAsync(
                builder.ConnectionString, "config.json");
            var typedHistoryAfter = await PostgresLibraryDocumentStore.ReadAsync(
                builder.ConnectionString, "review-history.jsonl");
            var loadedReviewConfig = JsonSerializer.Deserialize<LibraryConfig>(typedConfigAfter!.Content)!;
            var staleTypedReviewRejected = false;
            try { await PostgresReviewTransactionStore.CommitAsync(builder.ConnectionString, typedReview); }
            catch (InvalidOperationException) { staleTypedReviewRejected = true; }
            findings["typedReviewRoundTripVerified"] =
                loadedReviewConfig.AiReviews.TryGetValue(videoId, out var savedReview) &&
                savedReview.Status == "approved" &&
                loadedReviewConfig.Tags.TryGetValue(videoId, out var savedTags) &&
                savedTags.SequenceEqual(["verified"]) &&
                JsonDocument.Parse(typedConfigAfter.Content).RootElement
                    .GetProperty("futureSetting").GetString() == "preserve-me" &&
                Encoding.UTF8.GetString(typedHistoryAfter!.Content).Contains(typedReview.Id, StringComparison.Ordinal) &&
                staleTypedReviewRejected;
            var undoReview = ReviewTransactionStore.Create("undo",
                new Dictionary<string, ReviewItemSnapshot> { [videoId] = afterState },
                new Dictionary<string, ReviewItemSnapshot> { [videoId] = beforeState },
                undoesTransactionId: typedReview.Id);
            await PostgresReviewTransactionStore.CommitAsync(builder.ConnectionString, undoReview);
            var undoConfigDocument = await PostgresLibraryDocumentStore.ReadAsync(
                builder.ConnectionString, "config.json");
            var undoHistoryDocument = await PostgresLibraryDocumentStore.ReadAsync(
                builder.ConnectionString, "review-history.jsonl");
            var undoneConfig = JsonSerializer.Deserialize<LibraryConfig>(undoConfigDocument!.Content)!;
            var reloadedHistory = await PostgresReviewTransactionStore.LoadAsync(
                builder.ConnectionString);
            findings["typedReviewUndoVerified"] =
                !undoneConfig.AiReviews.ContainsKey(videoId) &&
                !undoneConfig.Tags.ContainsKey(videoId) &&
                Encoding.UTF8.GetString(undoHistoryDocument!.Content).Contains(undoReview.Id, StringComparison.Ordinal) &&
                reloadedHistory.Records.Any(entry => entry.Id == typedReview.Id) &&
                reloadedHistory.Records.Any(entry => entry.Id == undoReview.Id) &&
                ReviewTransactionStore.LastUndoable(reloadedHistory.Records) is null;
            var configBeforeMerge = await PostgresLibraryDocumentStore.ReadAsync(
                builder.ConnectionString, "config.json");
            var configBaseline = JsonSerializer.Deserialize<LibraryConfig>(configBeforeMerge!.Content)!;
            var localConfig = MainWindow.CloneLibraryConfig(configBaseline);
            localConfig.Favorites.Add("local-favorite");
            var concurrentConfig = MainWindow.CloneLibraryConfig(configBaseline);
            concurrentConfig.Favorites.Add("other-favorite");
            var concurrentDocument = JsonSerializer.SerializeToNode(concurrentConfig)!.AsObject();
            concurrentDocument["futureSetting"] = "preserve-me";
            await PostgresLibraryDocumentStore.SaveAsync(builder.ConnectionString,
                "config.json", JsonSerializer.SerializeToUtf8Bytes(concurrentDocument),
                configBeforeMerge.Revision);
            await PostgresLibraryConfigStore.SaveAsync(builder.ConnectionString,
                configBaseline, localConfig);
            var configAfterMerge = await PostgresLibraryDocumentStore.ReadAsync(
                builder.ConnectionString, "config.json");
            var mergedConfig = JsonSerializer.Deserialize<LibraryConfig>(configAfterMerge!.Content)!;
            findings["typedConfigMergeVerified"] =
                mergedConfig.Favorites.Contains("local-favorite") &&
                mergedConfig.Favorites.Contains("other-favorite") &&
                mergedConfig.TmdbToken.Length == 0 && mergedConfig.AiKey.Length == 0 &&
                mergedConfig.JavinfoKey.Length == 0 &&
                JsonDocument.Parse(configAfterMerge.Content).RootElement
                    .GetProperty("futureSetting").GetString() == "preserve-me";
            if (scale)
            {
                var scaleSource = Path.Combine(probeRoot, "scale-source");
                Directory.CreateDirectory(scaleSource);
                var scaleRoot = Path.Combine(probeRoot, "scale-import-media");
                var scaleIndex = new LibraryIndex
                {
                    Folders = [scaleRoot],
                    Entries = Enumerable.Range(0, 30_000).Select(number => new IndexEntry
                    {
                        Path = Path.Combine(scaleRoot, $"import-{number:D6}.mp4"),
                        Size = 2048 + number, ModifiedTicks = DateTime.UtcNow.Ticks
                    }).ToList()
                };
                await File.WriteAllTextAsync(Path.Combine(scaleSource, "library-index.json"),
                    JsonSerializer.Serialize(scaleIndex));
                await File.WriteAllTextAsync(Path.Combine(scaleSource, "metadata.json"), "{}");
                await File.WriteAllTextAsync(Path.Combine(scaleSource, "config.json"), "{\"folders\":[]}");
                await using (var scaleCluster = await PostgresPrivateCluster.OpenAsync(
                    runtimeDirectory, scaleLibrary))
                {
                    var importWatch = Stopwatch.StartNew();
                    var importedScale = await PostgresLibraryMigration.ImportAsync(
                        scaleSource, scaleCluster.ConnectionString);
                    findings["scaleFileImportMilliseconds"] = importWatch.ElapsedMilliseconds;
                    var scaleExport = Path.Combine(probeRoot, "scale-export");
                    var exportedScale = await PostgresLibraryExport.ExportAsync(
                        scaleCluster.ConnectionString, scaleExport);
                    findings["scaleFileRoundTripVerified"] = importedScale.VideoCount == 30_000 &&
                        exportedScale.VideoCount == 30_000 &&
                        await PostgresLibraryExport.VerifyOfflineAsync(scaleExport) &&
                        await PostgresLibraryMigration.VerifyContentAsync(
                            scaleExport, scaleCluster.ConnectionString);
                }
                var baseline = await PostgresLibraryIndexStore.ReadAsync(builder.ConnectionString);
                var mediaRoot = Path.Combine(probeRoot, "scale-media");
                var bulk = new LibraryIndex
                {
                    Folders = [mediaRoot],
                    Entries = Enumerable.Range(0, 30_000).Select(number => new IndexEntry
                    {
                        Path = Path.Combine(mediaRoot, $"video-{number:D6}.mp4"),
                        Size = 1024 + number, ModifiedTicks = DateTime.UtcNow.Ticks
                    }).ToList()
                };
                var scaleWatch = Stopwatch.StartNew();
                var revision = await PostgresLibraryIndexStore.SaveAsync(
                    builder.ConnectionString, bulk, baseline.Revision);
                var saveMilliseconds = scaleWatch.ElapsedMilliseconds;
                scaleWatch.Restart();
                var reloaded = await PostgresLibraryIndexStore.ReadAsync(builder.ConnectionString);
                findings["scaleReadMilliseconds"] = scaleWatch.ElapsedMilliseconds;
                findings["scaleSaveMilliseconds"] = saveMilliseconds;
                findings["scaleVideoCount"] = reloaded.Index.Entries.Count;
                findings["scaleVerified"] = reloaded.Revision == revision &&
                    reloaded.Index.Entries.Count == 30_000 &&
                    reloaded.Index.Entries[0].Path.EndsWith("video-000000.mp4", StringComparison.Ordinal) &&
                    reloaded.Index.Entries[^1].Path.EndsWith("video-029999.mp4", StringComparison.Ordinal);
                bulk.Entries[0].Missing = true;
                scaleWatch.Restart();
                var deltaRevision = await PostgresLibraryIndexStore.SaveDeltaAsync(
                    builder.ConnectionString, bulk, revision);
                findings["scaleOneRowDeltaMilliseconds"] = scaleWatch.ElapsedMilliseconds;
                var afterDelta = await PostgresLibraryIndexStore.ReadAsync(builder.ConnectionString);
                findings["scaleDeltaVerified"] = afterDelta.Revision == deltaRevision &&
                    afterDelta.Index.Entries.Count == 30_000 && afterDelta.Index.Entries[0].Missing;
                bulk.Entries.RemoveAt(1);
                bulk.Entries.Add(new IndexEntry
                {
                    Path = Path.Combine(mediaRoot, "video-new.mp4"), Size = 2048,
                    ModifiedTicks = DateTime.UtcNow.Ticks
                });
                var replacementRevision = await PostgresLibraryIndexStore.SaveDeltaAsync(
                    builder.ConnectionString, bulk, deltaRevision);
                var afterReplacement = await PostgresLibraryIndexStore.ReadAsync(builder.ConnectionString);
                var staleDeltaRejected = false;
                try { await PostgresLibraryIndexStore.SaveDeltaAsync(
                    builder.ConnectionString, bulk, deltaRevision); }
                catch (InvalidOperationException) { staleDeltaRejected = true; }
                findings["scaleDeltaVerified"] = findings["scaleDeltaVerified"] is true &&
                    afterReplacement.Revision == replacementRevision &&
                    afterReplacement.Index.Entries.Count == 30_000 &&
                    afterReplacement.Index.Entries.Any(entry => entry.Path.EndsWith("video-new.mp4",
                        StringComparison.Ordinal)) &&
                    !afterReplacement.Index.Entries.Any(entry => entry.Path.EndsWith("video-000001.mp4",
                        StringComparison.Ordinal)) && staleDeltaRejected;
            }
            var duplicateOwnerBlocked = false;
            await using (var privateCluster = await PostgresPrivateCluster.OpenAsync(
                runtimeDirectory, privateLibrary))
            {
                await using var privateConnection = new NpgsqlConnection(privateCluster.ConnectionString);
                await privateConnection.OpenAsync();
                var corruptExportDirectory = Path.Combine(probeRoot, "corrupt-export");
                Directory.CreateDirectory(corruptExportDirectory);
                foreach (var path in Directory.EnumerateFiles(exportDirectory, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(corruptExportDirectory,
                        Path.GetRelativePath(exportDirectory, path));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(path, target);
                }
                await File.AppendAllTextAsync(Path.Combine(corruptExportDirectory,
                    "metadata.json"), "corrupt");
                var corruptRestoreRejected = false;
                try { await PostgresLibraryRecovery.RestoreIntoFreshDatabaseAsync(
                    corruptExportDirectory, privateCluster.ConnectionString); }
                catch (InvalidDataException) { corruptRestoreRejected = true; }
                await using (var markerCheck = new NpgsqlCommand(
                    "SELECT to_regclass('public.library_migration') IS NULL", privateConnection))
                    findings["corruptRestoreStoppedBeforeDatabaseWrite"] =
                        corruptRestoreRejected && await markerCheck.ExecuteScalarAsync() is true;
                var restored = await PostgresLibraryRecovery.RestoreIntoFreshDatabaseAsync(
                    exportDirectory, privateCluster.ConnectionString);
                var secondRestoreBlocked = false;
                try { await PostgresLibraryRecovery.RestoreIntoFreshDatabaseAsync(
                    exportDirectory, privateCluster.ConnectionString); }
                catch (InvalidOperationException) { secondRestoreBlocked = true; }
                var restoredExportDirectory = Path.Combine(probeRoot, "restored-queue-export");
                var restoredAssetsDirectory = Path.Combine(probeRoot, "restored-assets");
                await PostgresLibraryRecovery.RestoreAssetsToNewDirectoryAsync(
                    exportDirectory, restoredAssetsDirectory);
                await PostgresLibraryExport.ExportAsync(
                    privateCluster.ConnectionString, restoredExportDirectory,
                    assetDirectory: restoredAssetsDirectory);
                findings["freshDatabaseRestoreVerified"] =
                    await PostgresLibraryMigration.VerifyContentAsync(
                        exportDirectory, privateCluster.ConnectionString) &&
                    restored.VideoCount == exported.VideoCount &&
                    restored.MetadataCount == exported.MetadataCount && secondRestoreBlocked &&
                    ScrapeQueueStore.Load(restoredExportDirectory).Select(item => item.Code)
                        .SequenceEqual(["ABC-001", "ABC-002"]) &&
                    (await File.ReadAllBytesAsync(Path.Combine(restoredExportDirectory,
                        "posters", "sample.jpg"))).SequenceEqual(new byte[] { 1, 2, 3, 4 });
                try { await using var duplicate = await PostgresPrivateCluster.OpenAsync(
                    runtimeDirectory, privateLibrary); }
                catch (IOException) { duplicateOwnerBlocked = true; }
            }
            await using (var restarted = await PostgresPrivateCluster.OpenAsync(
                runtimeDirectory, privateLibrary))
            {
                await using var connectionAfterRestart = new NpgsqlConnection(restarted.ConnectionString);
                await connectionAfterRestart.OpenAsync();
                findings["privateClusterLifecycleVerified"] = duplicateOwnerBlocked &&
                    connectionAfterRestart.State == System.Data.ConnectionState.Open &&
                    await PostgresLibraryMigration.VerifyContentAsync(
                        exportDirectory, restarted.ConnectionString);
            }
            findings["passed"] = version.StartsWith("18.6", StringComparison.Ordinal) &&
                findings["loopbackOnly"] is true && findings["credentialRoundTrip"] is true &&
                findings["sampleImportVerified"] is true && findings["stateDocumentCasVerified"] is true &&
                findings["activeSessionImportBlocked"] is true &&
                findings["rollbackExportVerified"] is true &&
                findings["nestedDocumentPathsVerified"] is true &&
                findings["queueJournalMigrationVerified"] is true &&
                findings["queueMutationRollbackVerified"] is true &&
                findings["assetBackupVerified"] is true &&
                findings["failedExportLeavesNoPartialBackup"] is true &&
                findings["legacyModeGuardVerified"] is true &&
                findings["atomicReviewStateVerified"] is true &&
                findings["typedReviewRoundTripVerified"] is true &&
                findings["typedReviewUndoVerified"] is true &&
                findings["typedConfigMergeVerified"] is true &&
                findings["freshDatabaseRestoreVerified"] is true &&
                findings["corruptRestoreStoppedBeforeDatabaseWrite"] is true &&
                findings["privateClusterLifecycleVerified"] is true &&
                (!scale || (findings["scaleVerified"] is true &&
                    findings["scaleDeltaVerified"] is true &&
                    findings["scaleFileRoundTripVerified"] is true));
        }
        catch (Exception ex)
        {
            findings["passed"] = false;
            findings["error"] = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            if (started)
            {
                try
                {
                    var stopped = await RunAsync(ctl, ["-D", data, "-m", "fast", "-w", "stop"], runtimeDirectory,
                        captureOutput: false);
                    findings["stopped"] = stopped.ExitCode == 0;
                }
                catch { findings["stopped"] = false; }
            }
            if (File.Exists(passwordFile)) File.Delete(passwordFile);
            WindowsCredentialStore.Write(credentialName, "");
            WindowsCredentialStore.Write(PostgresPrivateCluster.CredentialNameFor(privateLibrary), "");
            WindowsCredentialStore.Write(PostgresPrivateCluster.CredentialNameFor(scaleLibrary), "");
        }
        findings["realLibraryRead"] = false;
        findings["probeDirectory"] = probeRoot;
        findings["passed"] = findings["passed"] is true && findings.GetValueOrDefault("stopped") is true;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(findings,
            new JsonSerializerOptions { WriteIndented = true }));
        return findings["passed"] is true ? 0 : 1;
    }

    static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string executable, IEnumerable<string> arguments, string workDirectory,
        bool captureOutput = true)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workDirectory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = captureOutput, RedirectStandardError = captureOutput
        } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = captureOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var stderr = captureOutput ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(Path.GetFileName(executable) + " exceeded two minutes.");
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    static string Tail(string text) => text.Length <= 1000 ? text : text[^1000..];
}

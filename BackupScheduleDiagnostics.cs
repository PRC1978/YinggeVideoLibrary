using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class BackupScheduleDiagnostics
{
    internal static Dictionary<string, bool> Run()
    {
        var findings = new Dictionary<string, bool>(StringComparer.Ordinal);
        var scratch = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "YinggeBackupScheduleTest-" + Guid.NewGuid().ToString("N")));
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!scratch.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("测试目录不在临时文件夹中。");
        Directory.CreateDirectory(scratch);
        try
        {
            var data = Path.Combine(scratch, "data");
            var videos = Path.Combine(scratch, "videos");
            var target = Path.Combine(scratch, "destination");
            var second = Path.Combine(scratch, "destination-two");
            foreach (var path in new[] { data, videos, target, second }) Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(data, "config.json"),
                JsonSerializer.Serialize(new { folders = new[] { videos } }));
            File.WriteAllText(Path.Combine(data, "metadata.json"), "{}");
            File.WriteAllText(Path.Combine(target, "user-file.txt"), "keep");

            ReviewSavedViewStore.Upsert(data, new ReviewSavedView
                { Name = "低可信待审", Status = "pending", Source = "JavDB", Confidence = "low" });
            ScrapeCandidateCache.SaveTitleCandidates(data, new ScrapeCandidateCache.TitleCandidateSnapshot
            {
                VideoId = "sample-video", Query = "原始片名", SearchedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddDays(3),
                Candidates = [new ScrapeCandidateCache.RankedTitleCandidate
                {
                    Score = 80, SearchMetadata = new MovieMetadata
                    { Title = "原始片名", Source = "TMDB", SourceUrl = "https://example.test/title" }
                }]
            });
            var legacy = LibraryBackupService.Create(data);
            findings["savedReviewViewsIncludedInVerifiedBackup"] =
                File.Exists(Path.Combine(legacy, "review-views.json"))
                && LibraryBackupService.Verify(legacy).IsValid;
            findings["titleCandidateEvidenceIncludedInVerifiedBackup"] =
                Directory.EnumerateFiles(Path.Combine(legacy, "title-candidates"), "*.json").Count() == 1
                && LibraryBackupService.Verify(legacy).IsValid;
            var policy = new LibraryBackupService.BackupSchedulePolicy
            {
                Enabled = true, Frequency = "daily", RetentionCount = 3,
                DestinationDirectory = target
            };
            LibraryBackupService.SaveSchedule(data, policy, [videos]);
            var saved = LibraryBackupService.ReadSchedule(data);
            var managedRoot = LibraryBackupService.CurrentBackupDirectory(data);
            findings["periodicBackupSettingsAndCustomDestination"] = saved.Enabled
                && saved.RetentionCount == 3 && saved.NextDueUtc > DateTimeOffset.UtcNow
                && managedRoot.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(managedRoot, ".backup-owner.json"));

            bool rejectedVideoFolder;
            try { LibraryBackupService.ValidateBackupDestination(data, videos, [videos]); rejectedVideoFolder = false; }
            catch (InvalidDataException) { rejectedVideoFolder = true; }
            bool rejectedDiskRoot;
            try
            {
                LibraryBackupService.ValidateBackupDestination(data, Path.GetPathRoot(target)!, [videos]);
                rejectedDiskRoot = false;
            }
            catch (InvalidDataException) { rejectedDiskRoot = true; }
            findings["periodicBackupRejectsVideoFolderAndDiskRoot"] = rejectedVideoFolder && rejectedDiskRoot;

            var manual = LibraryBackupService.Create(data);
            ForceDue(data);
            LibraryBackupService.StageOfflineBackup(data);
            var manualCompleted = LibraryBackupService.ApplyPendingOfflineBackupAtStartup(data, out var manualError, false);
            var dueAfterManual = LibraryBackupService.ReadSchedule(data).NextDueUtc <= DateTimeOffset.UtcNow;
            findings["manualBackupDoesNotConsumeDuePeriodicRun"] = manualCompleted
                && manualError.Length == 0 && dueAfterManual
                && LibraryBackupService.CheckAndStageDuePeriodicBackup(data);
            // Execute that first periodic request before repeating the rotation.
            var firstPeriodic = LibraryBackupService.ApplyPendingOfflineBackupAtStartup(data, out var firstPeriodicError, false);
            findings["manualThenPeriodicBackupRunsAtStartup"] = firstPeriodic && firstPeriodicError.Length == 0;
            var gateBlocked = false;
            for (var i = 0; i < 3; i++)
            {
                ForceDue(data);
                if (!LibraryBackupService.CheckAndStageDuePeriodicBackup(data)) break;
                if (i == 0)
                {
                    using var active = LibraryBackupService.AcquireSessionLease(data);
                    gateBlocked = !LibraryBackupService.ApplyPendingOfflineBackupAtStartup(data, out _, false)
                        && LibraryBackupService.HasPendingOfflineBackup(data);
                }
                if (!LibraryBackupService.ApplyPendingOfflineBackupAtStartup(data, out var error, false)
                    || error.Length != 0) break;
            }
            var periodicPackages = Directory.EnumerateDirectories(managedRoot, "auto-*", SearchOption.TopDirectoryOnly).ToArray();
            findings["periodicBackupExclusiveStartupAndThreeGenerations"] = gateBlocked
                && periodicPackages.Length == 3
                && periodicPackages.All(path => LibraryBackupService.Verify(path).IsValid)
                && Directory.Exists(manual) && Directory.Exists(legacy)
                && File.ReadAllText(Path.Combine(target, "user-file.txt")) == "keep";

            policy.RetentionCount = 2;
            LibraryBackupService.SaveSchedule(data, policy, [videos]);
            ForceDue(data);
            var customCountStaged = LibraryBackupService.CheckAndStageDuePeriodicBackup(data);
            var customCountApplied = LibraryBackupService.ApplyPendingOfflineBackupAtStartup(data, out _, false);
            periodicPackages = Directory.EnumerateDirectories(managedRoot, "auto-*", SearchOption.TopDirectoryOnly).ToArray();
            findings["customRetentionKeepsTwoPeriodicGenerations"] = customCountStaged
                && customCountApplied && periodicPackages.Length == 2
                && periodicPackages.All(path => LibraryBackupService.Verify(path).IsValid)
                && Directory.Exists(manual) && Directory.Exists(legacy);

            policy.DestinationDirectory = second;
            LibraryBackupService.SaveSchedule(data, policy, [videos]);
            var newRoot = LibraryBackupService.CurrentBackupDirectory(data);
            var newManual = LibraryBackupService.Create(data);
            LibraryBackupService.StageRestore(data, legacy);
            var olderDefaultAccepted = File.Exists(Path.Combine(data, "restore-pending.json"));
            File.Delete(Path.Combine(data, "restore-pending.json"));
            LibraryBackupService.StageRestore(data, periodicPackages[0]);
            var olderCustomAccepted = File.Exists(Path.Combine(data, "restore-pending.json"));
            File.Delete(Path.Combine(data, "restore-pending.json"));
            findings["customAndLegacyBackupRemainRestorable"] = olderDefaultAccepted
                && olderCustomAccepted && Directory.Exists(newManual)
                && newRoot.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && LibraryBackupService.LatestBackup(data) == newManual;

            LibraryBackupService.StageOfflineBackup(data);
            Directory.Move(second, second + "-unavailable");
            var allowedStart = LibraryBackupService.ApplyPendingOfflineBackupAtStartup(data,
                out var unavailableMessage, false);
            var keptPending = allowedStart && unavailableMessage.Contains("失败", StringComparison.Ordinal)
                && LibraryBackupService.HasPendingOfflineBackup(data);
            Directory.Move(second + "-unavailable", second);
            var recovered = LibraryBackupService.ApplyPendingOfflineBackupAtStartup(data, out var recoveredMessage, false);
            findings["unavailableCustomTargetKeepsPendingBackup"] = keptPending && recovered
                && recoveredMessage.Length == 0 && !LibraryBackupService.HasPendingOfflineBackup(data);

            // Only a completed staging directory carrying this library's owner ID may
            // be removed after 24 hours. An unmarked directory must remain untouched.
            using (var ownerDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(newRoot, ".backup-owner.json"))))
            {
                var ownerId = ownerDoc.RootElement.GetProperty("LibraryId").GetString()!;
                var ownedStaging = Path.Combine(newRoot, ".building-" + Guid.NewGuid().ToString("N"));
                var unknownStaging = Path.Combine(newRoot, ".building-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ownedStaging);
                Directory.CreateDirectory(unknownStaging);
                File.WriteAllText(Path.Combine(ownedStaging, ".staging-owner"), ownerId);
                File.WriteAllText(Path.Combine(unknownStaging, "user-file.txt"), "keep");
                Directory.SetCreationTimeUtc(ownedStaging, DateTime.UtcNow.AddDays(-2));
                Directory.SetCreationTimeUtc(unknownStaging, DateTime.UtcNow.AddDays(-2));
                LibraryBackupService.StageOfflineBackup(data);
                var cleanupApplied = LibraryBackupService.ApplyPendingOfflineBackupAtStartup(data, out _, false);
                var cleanupResult = LibraryBackupService.LastOfflineBackupResult(data);
                findings["staleOwnedStagingOnlyIsRemoved"] = cleanupApplied
                    && !Directory.Exists(ownedStaging) && Directory.Exists(unknownStaging)
                    && File.ReadAllText(Path.Combine(unknownStaging, "user-file.txt")) == "keep"
                    && cleanupResult is { Succeeded: true }
                    && cleanupResult.Message.Contains("人工核查", StringComparison.Ordinal);
            }

            File.WriteAllText(Path.Combine(data, "backup-prune-pending.json"), "retry");
            var retryPrune = LibraryBackupService.ApplyPendingOfflineBackupAtStartup(data, out var pruneMessage, false);
            findings["incompleteRotationRetriesAtNextStartup"] = retryPrune
                && pruneMessage.Contains("补做", StringComparison.Ordinal)
                && !File.Exists(Path.Combine(data, "backup-prune-pending.json"));

            var policyFile = Path.Combine(data, "backup-policy.json");
            var policyFallback = policyFile + ".bak";
            File.WriteAllText(policyFile, "{broken");
            var recoveredPolicy = LibraryBackupService.ReadSchedule(data);
            findings["damagedBackupPolicyRecoversHistoryFromCopy"] = recoveredPolicy.Enabled
                && recoveredPolicy.KnownBackupRoots.Contains(managedRoot, StringComparer.OrdinalIgnoreCase)
                && Directory.EnumerateFiles(data, "backup-policy.json.corrupt-*", SearchOption.TopDirectoryOnly).Any();

            LibraryBackupService.StageOfflineBackup(data);
            File.WriteAllText(policyFile, "{broken");
            File.WriteAllText(policyFallback, "{broken");
            var manualWithCorruptPolicy = LibraryBackupService.ApplyPendingOfflineBackupAtStartup(data,
                out var policyWarning, false);
            var fallbackResult = LibraryBackupService.LastOfflineBackupResult(data);
            findings["manualPendingSurvivesDoubleCorruptPolicy"] = manualWithCorruptPolicy
                && policyWarning.Contains("默认数据目录", StringComparison.Ordinal)
                && fallbackResult is { Succeeded: true }
                && fallbackResult.Path.StartsWith(Path.Combine(data, "backups") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                && !LibraryBackupService.HasPendingOfflineBackup(data)
                && LibraryBackupService.LatestBackup(data) is not null;
            LibraryBackupService.StageRestore(data, legacy);
            var legacyWhileCorrupt = File.Exists(Path.Combine(data, "restore-pending.json"));
            File.Delete(Path.Combine(data, "restore-pending.json"));
            LibraryBackupService.ResetDamagedSchedule(data);
            var reset = LibraryBackupService.ReadSchedule(data);
            findings["corruptPolicyResetPreservesLegacyRestore"] = legacyWhileCorrupt
                && !reset.Enabled && reset.RetentionCount == 3
                && Directory.EnumerateFiles(data, "backup-policy.json*.corrupt-*", SearchOption.TopDirectoryOnly).Count() >= 2
                && LibraryBackupService.Verify(legacy).IsValid;
            ReviewSavedViewStore.Upsert(data, new ReviewSavedView
                { Name = "待恢复前的视图", Status = "rejected" });
            ScrapeCandidateCache.SaveTitleCandidates(data, new ScrapeCandidateCache.TitleCandidateSnapshot
            {
                VideoId = "sample-video", Query = "待恢复前的查询", SearchedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddDays(3),
                Candidates = [new ScrapeCandidateCache.RankedTitleCandidate
                {
                    Score = 10, SearchMetadata = new MovieMetadata
                    { Title = "待恢复前的查询", Source = "TMDB", SourceUrl = "https://example.test/changed" }
                }]
            });
            LibraryBackupService.StageRestore(data, legacy);
            var restored = LibraryBackupService.ApplyPendingAtStartup(data, out var restoreError, false);
            var restoredViews = ReviewSavedViewStore.Load(data, out _);
            findings["savedReviewViewsRestoreWithBackup"] = restored && restoreError.Length == 0
                && restoredViews.Count == 1 && restoredViews[0].Name == "低可信待审";
            findings["titleCandidateEvidenceRestoresWithBackup"] = restored && restoreError.Length == 0
                && ScrapeCandidateCache.LoadTitleCandidates(data, "sample-video")?.Query == "原始片名";
            return findings;
        }
        finally
        {
            var resolved = Path.GetFullPath(scratch);
            if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolved).StartsWith("YinggeBackupScheduleTest-", StringComparison.Ordinal)
                && Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    static void ForceDue(string dataDir)
    {
        var policy = LibraryBackupService.ReadSchedule(dataDir);
        policy.NextDueUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        File.WriteAllText(Path.Combine(dataDir, "backup-policy.json"), JsonSerializer.Serialize(policy));
    }
}

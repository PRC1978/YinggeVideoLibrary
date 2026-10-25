using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class BackupRestorePreviewDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Run(string root)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        var data = Path.Combine(root, "restore-preview-diagnostics");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "config.json"), "{\"folders\":[]}");
        var samplePath = Path.Combine(data, "sample.mp4");
        File.WriteAllBytes(samplePath, [0x41]);
        var sampleInfo = new FileInfo(samplePath);
        File.WriteAllText(Path.Combine(data, "library-index.json"),
            JsonSerializer.Serialize(new LibraryIndex
            {
                Folders = [data], Entries = [new IndexEntry
                {
                    Path = samplePath, Size = sampleInfo.Length,
                    ModifiedTicks = sampleInfo.LastWriteTime.Ticks
                }]
            }));
        File.WriteAllText(Path.Combine(data, "metadata.json"), "{\"sample\":{\"Title\":\"backup\"}}");
        var graphHistory = Path.Combine(data, "graph-move-history.json");
        File.WriteAllText(graphHistory, "{\"Version\":1,\"Entries\":[]}");
        const string aiRequest = "{\"messages\":[{\"role\":\"user\",\"content\":\"backup diagnostic\"}]}";
        var savedAiJob = AiJobStore.Create(data, "diagnostic", "https://example.test/v1", aiRequest);
        ScrapeQueueStore.Add(data, [new ScrapeQueueItem { VideoId = "sample", Code = "ABC-123" }]);
        var package = LibraryBackupService.Create(data);
        result["backupIncludesGraphUndoHistory"] = File.Exists(Path.Combine(package, "graph-move-history.json"))
            && LibraryBackupService.Verify(package).IsValid;
        result["backupIncludesProtectedAiJob"] =
            File.Exists(Path.Combine(package, "ai-jobs", savedAiJob.Id + ".json"))
            && AiJobStore.ReadProtectedRequest(AiJobStore.Get(package, savedAiJob.Id)) == aiRequest;
        File.WriteAllText(Path.Combine(data, "metadata.json"), "{\"sample\":{\"Title\":\"current\"}}");
        var preview = LibraryBackupService.CreateRestorePreview(data, package);
        result["restorePreviewShowsBothStates"] = preview.BackupVideoCount == 1
            && preview.CurrentVideoCount == 1 && preview.BackupQueueCount == 1
            && preview.CurrentQueueCount == 1 && preview.OverwriteFileCount >= 3
            && preview.ConfirmationText.Contains("受管文件", StringComparison.Ordinal)
            && preview.ConfirmationText.Contains(package, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(data, "metadata.json"), "{\"sample\":{\"Title\":\"changed-again\"}}");
        try
        {
            LibraryBackupService.StageRestore(data, preview);
            result["restoreRejectsStalePreviewBeforeStaging"] = false;
        }
        catch (InvalidOperationException)
        {
            result["restoreRejectsStalePreviewBeforeStaging"] =
                !File.Exists(Path.Combine(data, "restore-pending.json"));
        }

        var fresh = LibraryBackupService.CreateRestorePreview(data, package);
        LibraryBackupService.StageRestore(data, fresh);
        File.WriteAllText(Path.Combine(data, "metadata.json"), "{\"sample\":{\"Title\":\"newer-user-data\"}}");
        var rejectedAtStartup = !LibraryBackupService.ApplyPendingAtStartup(data, out var staleMessage, false);
        result["restoreRejectsStaleLiveStateAtStartup"] = rejectedAtStartup
            && staleMessage.Contains("重新预览", StringComparison.Ordinal)
            && File.ReadAllText(Path.Combine(data, "metadata.json")).Contains("newer-user-data", StringComparison.Ordinal)
            && !File.Exists(Path.Combine(data, "restore-pending.json"));

        var aiJobPath = AiJobStore.PathFor(data, savedAiJob.Id);
        var originalAiJob = File.ReadAllText(aiJobPath);
        var originalAiJobTime = File.GetLastWriteTimeUtc(aiJobPath);
        var aiJobPreview = LibraryBackupService.CreateRestorePreview(data, package);
        try
        {
            var changedAiJob = originalAiJob.Replace("diagnostic", "diagnostiX", StringComparison.Ordinal);
            if (changedAiJob.Length != originalAiJob.Length || changedAiJob == originalAiJob)
                throw new InvalidDataException("AI 作业诊断样例不完整。");
            File.WriteAllText(aiJobPath, changedAiJob);
            File.SetLastWriteTimeUtc(aiJobPath, originalAiJobTime);
            try
            {
                LibraryBackupService.StageRestore(data, aiJobPreview);
                result["restoreDetectsChangedAiJobWithSameTimeAndSize"] = false;
            }
            catch (InvalidOperationException)
            {
                result["restoreDetectsChangedAiJobWithSameTimeAndSize"] =
                    !File.Exists(Path.Combine(data, "restore-pending.json"));
            }
        }
        finally
        {
            File.WriteAllText(aiJobPath, originalAiJob);
            File.SetLastWriteTimeUtc(aiJobPath, originalAiJobTime);
        }

        File.WriteAllText(graphHistory, "{\"Version\":1,\"Entries\":[] ,\"CurrentOnly\":true}");
        var currentOnlyAiJob = AiJobStore.Create(data, "diagnostic", "https://example.test/v1", aiRequest);
        LibraryBackupService.StageRestore(data, package);
        var restored = LibraryBackupService.ApplyPendingAtStartup(data, out var restoreMessage, false);
        result["restorePreviewAllowsUnchangedOfflineRestore"] = restored && restoreMessage.Length == 0
            && File.ReadAllText(Path.Combine(data, "metadata.json")).Contains("backup", StringComparison.Ordinal);
        result["restoreReplacesGraphUndoHistory"] = restored
            && File.ReadAllText(graphHistory) == File.ReadAllText(Path.Combine(package, "graph-move-history.json"));
        result["restoreReplacesAiJobsAndCanDecrypt"] = restored
            && !File.Exists(AiJobStore.PathFor(data, currentOnlyAiJob.Id))
            && AiJobStore.ReadProtectedRequest(AiJobStore.Get(data, savedAiJob.Id)) == aiRequest;

        File.WriteAllText(graphHistory, "{\"Version\":2,\"Entries\":[]}");
        try
        {
            LibraryBackupService.Create(data);
            result["backupRejectsInvalidGraphUndoHistory"] = false;
        }
        catch (InvalidDataException)
        {
            result["backupRejectsInvalidGraphUndoHistory"] = true;
        }
        finally
        {
            File.WriteAllText(graphHistory, "{\"Version\":1,\"Entries\":[]}");
        }

        var malicious = LibraryBackupService.Create(data);
        var manifestPath = Path.Combine(malicious, "backup-manifest.json");
        var manifest = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, manifest.Replace("config.json", "../config.json", StringComparison.Ordinal));
        result["restoreRejectsTraversalManifest"] = !LibraryBackupService.Verify(malicious).IsValid;
        return result;
    }
}

using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class PostgresSelectiveImportDiagnostics
{
    internal static async Task<int> RunToFileAsync(string runtimeDirectory, string outputPath)
    {
        var root = Path.Combine(Path.GetTempPath(),
            "YinggePgSelectiveProbe-" + Guid.NewGuid().ToString("N"));
        var report = new Dictionary<string, object?>
        {
            ["passed"] = false, ["realLibraryRead"] = false, ["probeDirectory"] = root
        };
        try
        {
            Directory.CreateDirectory(root);
            var library = Path.Combine(root, "library");
            var source = library;
            var videoId = PostgresBackupAcceptance.BuildSingleVideoFixtureForSelectiveImport(source);
            // The general PostgreSQL fixture deliberately includes malformed
            // opaque candidate/job bytes to test byte preservation. A legacy
            // backup validates those optional files, so omit them here.
            Directory.Delete(Path.Combine(source, "title-candidates"), true);
            Directory.Delete(Path.Combine(source, "ai-jobs"), true);
            var legacyBackup = LibraryBackupService.Create(source);
            await using (var cluster = await PostgresPrivateCluster.OpenAsync(runtimeDirectory, library))
            {
                await PostgresLibraryMigration.ImportAsync(source, cluster.ConnectionString);
                var activationBackup = Path.Combine(root, "activation-backup");
                await PostgresLibraryBackup.CreateAsync(cluster.ConnectionString, library,
                    activationBackup);
                await PostgresStorageModeActivation.ActivateAsync(cluster.ConnectionString,
                    library, activationBackup);
                var original = await ReadConfigAsync(cluster.ConnectionString);
                var edited = MainWindow.CloneLibraryConfig(original);
                edited.Tags.Remove(videoId);
                await PostgresLibraryConfigStore.SaveAsync(cluster.ConnectionString,
                    original, edited);
                report["tagRemovedBeforeImport"] = !(await ReadConfigAsync(
                    cluster.ConnectionString)).Tags.ContainsKey(videoId);

                var preview = await PostgresSelectiveImport.PreviewAsync(cluster.ConnectionString,
                    library, legacyBackup, LibraryImportScope.Tags,
                    LibraryImportConflictPolicy.KeepCurrent, null, false);
                report["previewHasChanges"] = preview.HasChanges && preview.MappingComplete;
                await PostgresSelectiveImport.StageAsync(cluster.ConnectionString, library, preview);
                report["pendingSaved"] = await PostgresSelectiveImport.HasPendingAsync(
                    cluster.ConnectionString);
            }
            report["applied"] = await PostgresSelectiveImport.ApplyPendingAtStartupAsync(
                runtimeDirectory, library);
            await using (var reopened = await PostgresLibraryRuntimeSession.OpenAsync(
                runtimeDirectory, library))
            {
                report["tagRestored"] = reopened.Config.Tags.TryGetValue(videoId, out var tags) &&
                    tags.Contains("标签甲") && tags.Contains("标签乙");
                report["indexPreserved"] = reopened.Index.Index.Entries.Count == 1;
                report["pendingRemoved"] = !await PostgresSelectiveImport.HasPendingAsync(
                    reopened.ConnectionString);
                report["rollbackRetained"] = Directory.Exists(Path.Combine(library,
                    "backups")) && Directory.EnumerateDirectories(Path.Combine(library,
                    "backups"), "pre-restore-*").Any();
                var marker = PostgresStorageModeActivation.TryRead(library);
                report["markerBackupRetained"] = marker is not null &&
                    PostgresStorageModeActivation.BackupStillVerifies(marker) &&
                    await PostgresLibraryExport.VerifyOfflineAsync(marker.BackupDirectory);
                var changed = MainWindow.CloneLibraryConfig(reopened.Config);
                changed.Tags.Remove(videoId);
                await reopened.SaveConfigAsync(changed);
                var stalePreview = await PostgresSelectiveImport.PreviewAsync(
                    reopened.ConnectionString, library, legacyBackup,
                    LibraryImportScope.Tags, LibraryImportConflictPolicy.KeepCurrent,
                    null, false);
                await PostgresSelectiveImport.StageAsync(reopened.ConnectionString,
                    library, stalePreview);
                var changedAfterStage = MainWindow.CloneLibraryConfig(reopened.Config);
                changedAfterStage.Favorites.Clear();
                await reopened.SaveConfigAsync(changedAfterStage);
            }
            var staleRejected = false;
            try { await PostgresSelectiveImport.ApplyPendingAtStartupAsync(runtimeDirectory, library); }
            catch (InvalidOperationException) { staleRejected = true; }
            await using (var reopened = await PostgresLibraryRuntimeSession.OpenAsync(
                runtimeDirectory, library))
            {
                report["stalePreviewRejected"] = staleRejected &&
                    !reopened.Config.Tags.ContainsKey(videoId) &&
                    await PostgresSelectiveImport.HasPendingAsync(reopened.ConnectionString) &&
                    PostgresStorageModeGuard.PostgresMayOpen(library);
                await PostgresSelectiveImport.CancelAsync(reopened.ConnectionString);
                report["cancelClearedPending"] = !await PostgresSelectiveImport.HasPendingAsync(
                    reopened.ConnectionString);
            }
            report["passed"] = new[] { "tagRemovedBeforeImport", "previewHasChanges",
                "pendingSaved", "applied", "tagRestored", "indexPreserved", "pendingRemoved",
                "rollbackRetained", "markerBackupRetained", "stalePreviewRejected",
                "cancelClearedPending" }.All(key => Equals(report[key], true));
        }
        catch (Exception error) { report["error"] = error.ToString(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllBytesAsync(outputPath, JsonSerializer.SerializeToUtf8Bytes(report));
        return Equals(report["passed"], true) ? 0 : 1;
    }

    private static async Task<LibraryConfig> ReadConfigAsync(string connectionString)
    {
        var snapshot = await PostgresLibraryDocumentStore.ReadAsync(connectionString,
            "config.json") ?? throw new InvalidDataException("诊断库缺少配置。");
        return JsonSerializer.Deserialize<LibraryConfig>(snapshot.Content)
            ?? throw new InvalidDataException("诊断库配置损坏。");
    }
}

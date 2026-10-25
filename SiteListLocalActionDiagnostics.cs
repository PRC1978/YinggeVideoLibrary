using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class SiteListLocalActionDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "yingge-list-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, "data");
            var file = Path.Combine(root, "sample.mp4");
            var export = Path.Combine(root, "favorites.csv");
            var configPath = Path.Combine(data, "config.json");
            File.WriteAllBytes(file, [1, 2, 3, 4]);
            File.WriteAllText(export,
                "source,title,code,url\r\nSiteA,Sample Work,,\r\nSiteB,Sample Work,,\r\n",
                new UTF8Encoding(false));
            SiteListImportStore.LocalVideo Local()
            {
                var info = new FileInfo(file);
                return new SiteListImportStore.LocalVideo("local-1", "Sample Work", "Sample Work",
                    "", "", "", "", file, info.Length, info.LastWriteTime.Ticks);
            }
            var local = Local();
            var imported = SiteListImportStore.CreatePreview(data, export, [local]);
            File.WriteAllBytes(file, [9, 8, 7, 6]);
            File.SetLastWriteTime(file, new DateTime(local.ModifiedTicks, DateTimeKind.Local));
            var sameImportStamp = new FileInfo(file).LastWriteTime.Ticks == local.ModifiedTicks;
            var importCommitRejected = false;
            try { SiteListImportStore.Commit(data, imported, [local]); }
            catch (InvalidOperationException) { importCommitRejected = true; }
            var findings = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["siteListImportEqualStampReplacementZeroWrite"] = sameImportStamp &&
                    importCommitRejected && !File.Exists(Path.Combine(data, "site-list-import.json"))
            };
            File.WriteAllBytes(file, [1, 2, 3, 4]);
            File.SetLastWriteTime(file, new DateTime(local.ModifiedTicks, DateTimeKind.Local));
            imported = SiteListImportStore.CreatePreview(data, export, [local]);
            SiteListImportStore.Commit(data, imported, [local]);
            var list = SiteListImportStore.Load(data);
            var selected = list.Entries.ToArray();
            var priorHash = selected[0].ContentSha256;
            selected[0].ContentSha256 = "";
            var legacyRejected = false;
            try { SiteListLocalActionStore.Prepare(list, [selected[0]], [local], new LibraryConfig(), true, "片单"); }
            catch (InvalidOperationException) { legacyRejected = true; }
            selected[0].ContentSha256 = priorHash;
            findings["siteListLocalLegacyEntryMustReimport"] = legacyRejected && !File.Exists(configPath);
            File.WriteAllBytes(file, [9, 8, 7, 6]);
            File.SetLastWriteTime(file, new DateTime(local.ModifiedTicks, DateTimeKind.Local));
            var beforePreviewRejected = false;
            try { SiteListLocalActionStore.Prepare(list, selected, [local], new LibraryConfig(), true, "片单"); }
            catch (InvalidOperationException) { beforePreviewRejected = true; }
            findings["siteListImportToLocalPreviewEqualStampReplacementZeroWrite"] =
                beforePreviewRejected && !File.Exists(configPath);
            File.WriteAllBytes(file, [1, 2, 3, 4]);
            File.SetLastWriteTime(file, new DateTime(local.ModifiedTicks, DateTimeKind.Local));
            var preview = SiteListLocalActionStore.Prepare(list, selected, [local], new LibraryConfig(), true, "片单");
            findings["siteListLocalPreviewAndDuplicatesZeroWrite"] =
                preview.Changes.Count == 1 && preview.DuplicateLocalVideos == 1 &&
                !File.Exists(configPath);

            // The replacement deliberately keeps both the size and timestamp.
            // Path/stamp-only checks would accept it; the full digest must not.
            File.WriteAllBytes(file, [9, 8, 7, 6]);
            File.SetLastWriteTime(file, new DateTime(local.ModifiedTicks, DateTimeKind.Local));
            var replacement = new FileInfo(file);
            var sameStamp = replacement.Length == local.Size &&
                replacement.LastWriteTime.Ticks == local.ModifiedTicks;
            var staleRejected = false;
            try { MainWindow.SaveSiteListLocalAction(preview, [local], configPath, data, true, true); }
            catch (InvalidOperationException) { staleRejected = true; }
            findings["siteListLocalEqualStampReplacementZeroWrite"] = sameStamp && staleRejected &&
                !File.Exists(configPath);
            File.WriteAllBytes(file, [1, 2, 3, 4]);
            // Explicitly rebuild the preview after the file stamp changed.
            local = Local();
            SiteListImportStore.Undo(data, list.Actions.Single(action => action.Kind == "import").BatchId);
            imported = SiteListImportStore.CreatePreview(data, export, [local]);
            SiteListImportStore.Commit(data, imported, [local]);
            list = SiteListImportStore.Load(data);
            selected = list.Entries.ToArray();
            preview = SiteListLocalActionStore.Prepare(list, selected, [local], new LibraryConfig(), true, "片单");
            var applied = MainWindow.SaveSiteListLocalAction(preview, [local], configPath, data, true, true);
            var saved = JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(configPath));
            findings["siteListLocalAtomicApplyAndHistory"] = saved is not null &&
                saved.Favorites.SequenceEqual(["local-1"]) &&
                saved.Tags.TryGetValue("local-1", out var tags) && tags.SequenceEqual(["片单"]) &&
                saved.SiteListLocalActions.Count == 1 &&
                saved.SiteListLocalActions.ContainsKey(applied.Action.Id);
            var undone = MainWindow.UndoSavedSiteListLocalAction(applied.Action.Id, [local], configPath, true, true);
            saved = JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(configPath));
            findings["siteListLocalDurableUndo"] = saved is not null &&
                saved.Favorites.Count == 0 && saved.Tags.Count == 0 &&
                saved.SiteListLocalActions.Count == 2 && undone.Action.UndoesActionId == applied.Action.Id;

            // If the user edits a tag afterwards, undo must leave it intact.
            preview = SiteListLocalActionStore.Prepare(list, selected, [local], saved!, true, "片单");
            applied = MainWindow.SaveSiteListLocalAction(preview, [local], configPath, data, true, true);
            saved = JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(configPath))!;
            saved.Tags["local-1"] = ["manual"];
            File.WriteAllText(configPath, JsonSerializer.Serialize(saved));
            var before = SHA256.HashData(File.ReadAllBytes(configPath));
            var newerEditRejected = false;
            try { MainWindow.UndoSavedSiteListLocalAction(applied.Action.Id, [local], configPath, true, true); }
            catch (InvalidOperationException) { newerEditRejected = true; }
            findings["siteListLocalUndoPreservesNewerEdit"] = newerEditRejected &&
                SHA256.HashData(File.ReadAllBytes(configPath)).SequenceEqual(before);

            var sourceHistory = saved;
            var mappedHistory = JsonSerializer.Deserialize<LibraryConfig>(JsonSerializer.Serialize(sourceHistory))!;
            foreach (var action in mappedHistory.SiteListLocalActions.Values)
                foreach (var change in action.Changes) change.VideoId = "local-2";
            var targetFile = Path.Combine(root, "moved.mp4");
            File.WriteAllBytes(targetFile, File.ReadAllBytes(file));
            var targetInfo = new FileInfo(targetFile);
            var targetLocal = local with
            {
                VideoId = "local-2", Path = targetFile,
                Size = targetInfo.Length, ModifiedTicks = targetInfo.LastWriteTime.Ticks
            };
            LibraryBackupService.RemapSiteListLocalActions(sourceHistory, mappedHistory,
                new Dictionary<string, string> { ["local-1"] = "local-2" },
                id => id == "local-1" ? local : null,
                id => id == "local-2" ? targetLocal : null);
            var targetPrint = SiteListImportStore.ComputeLocalFingerprint(targetLocal);
            findings["siteListLocalRelocationRewritesEveryFingerprint"] =
                mappedHistory.SiteListLocalActions.Values.SelectMany(action => action.Changes)
                    .All(change => change.VideoId == "local-2" && change.LocalFingerprint == targetPrint);
            var unsafeTarget = JsonSerializer.Deserialize<LibraryConfig>(JsonSerializer.Serialize(sourceHistory))!;
            foreach (var action in unsafeTarget.SiteListLocalActions.Values)
                foreach (var change in action.Changes) change.VideoId = "local-2";
            var missingTargetRejected = false;
            try
            {
                LibraryBackupService.RemapSiteListLocalActions(sourceHistory, unsafeTarget,
                    new Dictionary<string, string> { ["local-1"] = "local-2" },
                    _ => local, _ => null);
            }
            catch (InvalidDataException) { missingTargetRejected = true; }
            findings["siteListLocalRelocationMissingTargetRejected"] = missingTargetRejected;
            var changedTargetFile = Path.Combine(root, "moved-other.mp4");
            File.WriteAllBytes(changedTargetFile, [8, 7, 6, 5]);
            File.SetLastWriteTime(changedTargetFile, new DateTime(targetLocal.ModifiedTicks, DateTimeKind.Local));
            var changedTarget = targetLocal with { Path = changedTargetFile };
            var unsafeContent = JsonSerializer.Deserialize<LibraryConfig>(JsonSerializer.Serialize(sourceHistory))!;
            foreach (var action in unsafeContent.SiteListLocalActions.Values)
                foreach (var change in action.Changes) change.VideoId = "local-2";
            var changedContentRejected = false;
            try
            {
                LibraryBackupService.RemapSiteListLocalActions(sourceHistory, unsafeContent,
                    new Dictionary<string, string> { ["local-1"] = "local-2" },
                    _ => local, _ => changedTarget);
            }
            catch (InvalidDataException) { changedContentRejected = true; }
            findings["siteListLocalRelocationDifferentContentRejected"] = changedContentRejected;
            return findings;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}

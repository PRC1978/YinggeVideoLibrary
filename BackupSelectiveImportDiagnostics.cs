using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class BackupSelectiveImportDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Run(string root)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        // The original fixture has data in six scopes. Site lists and role
        // profiles have dedicated fixtures because this one contains neither.
        var coreScopes = LibraryImportScope.All &
            ~(LibraryImportScope.SiteLists | LibraryImportScope.RoleProfiles);
        var source = Path.Combine(root, "selective-import-source");
        // This suite exercises same-library selective merge. Cross-device
        // identity is covered separately with real path-derived IDs.
        var target = source;
        var targetFixture = Path.Combine(root, "selective-import-target-fixture");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(targetFixture);
        File.WriteAllText(Path.Combine(source, "config.json"), """
            {"folders":[],"categories":{"v1":"来源分类","v2":"新增分类"},"multiCategories":{"v1":["来源分类"],"v2":["新增分类"]},"categoryNames":["来源分类","新增分类"],"tags":{"v1":["来源标签"],"v2":["新增标签"]},"autoAmateurTags":["v2"],"aiReviews":{"v1":{"VideoId":"v1","Status":"approved"},"v2":{"VideoId":"v2","Status":"pending"}}}
            """);
        File.WriteAllText(Path.Combine(targetFixture, "config.json"), """
            {"folders":[],"categories":{"v1":"当前分类"},"multiCategories":{"v1":["当前分类"]},"categoryNames":["当前分类"],"tags":{"v1":["当前标签"]},"autoAmateurTags":[],"aiReviews":{"v1":{"VideoId":"v1","Status":"rejected"}}}
            """);
        File.WriteAllText(Path.Combine(source, "metadata.json"),
            "{\"v1\":{\"Code\":\"SRC-001\",\"Title\":\"来源资料\"},\"v2\":{\"Code\":\"SRC-002\",\"Title\":\"新增资料\"}}");
        File.WriteAllText(Path.Combine(targetFixture, "metadata.json"),
            "{\"v1\":{\"Code\":\"CUR-001\",\"Title\":\"当前资料\"}}");
        File.WriteAllText(Path.Combine(source, "personal-ratings.json"),
            "{\"v1\":{\"Score\":9},\"v2\":{\"Score\":8}}");
        File.WriteAllText(Path.Combine(targetFixture, "personal-ratings.json"),
            "{\"v1\":{\"Score\":5}}");
        File.WriteAllText(Path.Combine(source, "review-history.jsonl"), "{\"Action\":\"source-action\"}\n");
        File.WriteAllText(Path.Combine(targetFixture, "review-history.jsonl"), "{\"Action\":\"current-action\"}\n");
        File.WriteAllText(Path.Combine(source, "review-views.json"), """
            {"Version":1,"Views":[{"Name":"共享","Status":"rejected","Source":"","Confidence":"all"},{"Name":"新增","Status":"pending","Source":"","Confidence":"all"}]}
            """);
        File.WriteAllText(Path.Combine(targetFixture, "review-views.json"), """
            {"Version":1,"Views":[{"Name":"共享","Status":"approved","Source":"","Confidence":"all"}]}
            """);
        ScrapeQueueStore.Add(source,
        [
            new ScrapeQueueItem { VideoId = "v1", Code = "SRC-001" },
            new ScrapeQueueItem { VideoId = "v2", Code = "SRC-002", RefreshExisting = true }
        ]);
        var package = LibraryBackupService.Create(source);
        foreach (var name in new[] { "config.json", "metadata.json", "personal-ratings.json",
                     "review-history.jsonl", "review-views.json" })
            File.Copy(Path.Combine(targetFixture, name), Path.Combine(target, name), true);
        foreach (var path in Directory.EnumerateFiles(target, "scrape-queue*", SearchOption.TopDirectoryOnly))
            File.Delete(path);
        ScrapeQueueStore.ForgetCachedStateAfterOfflineRestore(target);
        ScrapeQueueStore.Add(target, [new ScrapeQueueItem { VideoId = "v1", Code = "CUR-001", RefreshExisting = true }]);
        var before = DigestSelected(target);
        var keep = LibraryBackupService.CreateImportPreview(target, package, coreScopes);
        result["selectiveImportPreviewAllSixScopes"] = keep.Sections.Count == 6
            && keep.Sections.All(section => section.Added > 0)
            && keep.ConflictPolicy == LibraryImportConflictPolicy.KeepCurrent;
        result["selectiveImportPreviewZeroDataWrites"] = DigestSelected(target) == before;
        LibraryBackupService.StageImport(target, keep);
        LibraryBackupService.CancelPendingImport(target);
        result["selectiveImportCancelZeroDataWrites"] = DigestSelected(target) == before
            && !LibraryBackupService.HasPendingImport(target);

        LibraryBackupService.StageImport(target, keep);
        var imported = LibraryBackupService.ApplyPendingImportAtStartup(target, out var importError, false);
        var currentConfig = ReadConfig(target);
        var currentQueue = ScrapeQueueStore.Load(target);
        var currentViews = ReviewSavedViewStore.Load(target, out _);
        result["selectiveImportKeepsCurrentAndAddsMissing"] = imported && importError.Length == 0
            && MetadataStore.Load(target)["v1"].Title == "当前资料"
            && MetadataStore.Load(target)["v2"].Title == "新增资料"
            && currentConfig.Categories["v1"] == "当前分类"
            && currentConfig.Categories["v2"] == "新增分类"
            && currentConfig.Tags["v1"].SequenceEqual(["当前标签"])
            && currentConfig.Tags["v2"].SequenceEqual(["新增标签"])
            && PersonalRatingStore.Get(target, "v1") == 5
            && PersonalRatingStore.Get(target, "v2") == 8
            && currentConfig.AiReviews["v1"].Status == "rejected"
            && currentConfig.AiReviews["v2"].Status == "pending"
            && File.ReadAllText(Path.Combine(target, "review-history.jsonl")).Contains("source-action", StringComparison.Ordinal)
            && File.ReadAllText(Path.Combine(target, "review-history.jsonl")).Contains("current-action", StringComparison.Ordinal)
            && currentViews.Single(view => view.Name == "共享").Status == "approved"
            && currentViews.Any(view => view.Name == "新增")
            && currentQueue.Single(item => item.VideoId == "v1").Code == "CUR-001"
            && currentQueue.Single(item => item.VideoId == "v1").RefreshExisting
            && currentQueue.Single(item => item.VideoId == "v2").Code == "SRC-002"
            && currentQueue.Single(item => item.VideoId == "v2").RefreshExisting;

        var once = DigestSelected(target);
        var repeat = LibraryBackupService.CreateImportPreview(target, package, coreScopes);
        LibraryBackupService.StageImport(target, repeat);
        var repeatApplied = LibraryBackupService.ApplyPendingImportAtStartup(target, out _, false);
        result["selectiveImportRepeatedIsIdempotent"] = !repeat.HasChanges
            && repeatApplied && DigestSelected(target) == once
            && File.ReadLines(Path.Combine(target, "review-history.jsonl")).Count() == 2;

        var prefer = LibraryBackupService.CreateImportPreview(target, package, coreScopes,
            LibraryImportConflictPolicy.PreferBackup);
        LibraryBackupService.StageImport(target, prefer);
        var preferApplied = LibraryBackupService.ApplyPendingImportAtStartup(target, out _, false);
        var replacedConfig = ReadConfig(target);
        var replacedQueue = ScrapeQueueStore.Load(target);
        var replacedViews = ReviewSavedViewStore.Load(target, out _);
        result["selectiveImportPreferBackupReplacesConflicts"] = preferApplied
            && MetadataStore.Load(target)["v1"].Title == "来源资料"
            && replacedConfig.Categories["v1"] == "来源分类"
            && replacedConfig.Tags["v1"].SequenceEqual(["来源标签"])
            && PersonalRatingStore.Get(target, "v1") == 9
            && replacedConfig.AiReviews["v1"].Status == "approved"
            && replacedViews.Single(view => view.Name == "共享").Status == "rejected"
            && replacedQueue.Single(item => item.VideoId == "v1").Code == "SRC-001"
            && !replacedQueue.Single(item => item.VideoId == "v1").RefreshExisting;
        var afterPrefer = DigestSelected(target);
        var preferRepeat = LibraryBackupService.CreateImportPreview(target, package, coreScopes,
            LibraryImportConflictPolicy.PreferBackup);
        LibraryBackupService.StageImport(target, preferRepeat);
        result["selectiveImportPreferBackupIdempotent"] = !preferRepeat.HasChanges
            && LibraryBackupService.ApplyPendingImportAtStartup(target, out _, false)
            && DigestSelected(target) == afterPrefer;

        var stale = LibraryBackupService.CreateImportPreview(target, package, LibraryImportScope.Metadata);
        LibraryBackupService.StageImport(target, stale);
        File.WriteAllText(Path.Combine(target, "metadata.json"),
            "{\"v1\":{\"Code\":\"CUR-NEW\",\"Title\":\"用户刚修改\"}}");
        var staleRejected = !LibraryBackupService.ApplyPendingImportAtStartup(target, out var staleError, false);
        result["selectiveImportStaleStateRejectedWithoutOverwrite"] = staleRejected
            && staleError.Contains("重新预览", StringComparison.Ordinal)
            && MetadataStore.Load(target)["v1"].Title == "用户刚修改"
            && !LibraryBackupService.HasPendingImport(target);

        var configBeforeNarrowImport = File.ReadAllBytes(Path.Combine(target, "config.json"));
        var ratingsBeforeNarrowImport = File.ReadAllBytes(Path.Combine(target, "personal-ratings.json"));
        var historyBeforeNarrowImport = File.ReadAllBytes(Path.Combine(target, "review-history.jsonl"));
        var narrow = LibraryBackupService.CreateImportPreview(target, package,
            LibraryImportScope.Metadata, LibraryImportConflictPolicy.PreferBackup);
        LibraryBackupService.StageImport(target, narrow);
        var narrowApplied = LibraryBackupService.ApplyPendingImportAtStartup(target, out _, false);
        result["selectiveImportHonorsChosenScope"] = narrowApplied && narrow.Sections.Count == 1
            && MetadataStore.Load(target)["v1"].Title == "来源资料"
            && File.ReadAllBytes(Path.Combine(target, "config.json")).SequenceEqual(configBeforeNarrowImport)
            && File.ReadAllBytes(Path.Combine(target, "personal-ratings.json")).SequenceEqual(ratingsBeforeNarrowImport)
            && File.ReadAllBytes(Path.Combine(target, "review-history.jsonl")).SequenceEqual(historyBeforeNarrowImport);

        var rollback = LibraryBackupService.Create(target, rollback: true);
        File.WriteAllText(Path.Combine(target, "import-active.json"),
            JsonSerializer.Serialize(new { BackupPath = package, RollbackPath = rollback }));
        File.WriteAllText(Path.Combine(target, "metadata.json"), "{\"v1\":{\"Title\":\"半途导入\"}}");
        var interrupted = !LibraryBackupService.ApplyPendingImportAtStartup(target, out var interruptedError, false);
        result["selectiveImportCrashRollbackRestoresPriorState"] = interrupted
            && interruptedError.Contains("原数据", StringComparison.Ordinal)
            && MetadataStore.Load(target)["v1"].Title == "来源资料"
            && !File.Exists(Path.Combine(target, "import-active.json"));

        var tamperPreview = LibraryBackupService.CreateImportPreview(target, package, LibraryImportScope.Metadata);
        LibraryBackupService.StageImport(target, tamperPreview);
        File.AppendAllText(Path.Combine(package, "metadata.json"), "tampered");
        var tamperRejected = !LibraryBackupService.ApplyPendingImportAtStartup(target, out var tamperError, false);
        result["selectiveImportTamperedPackageRejected"] = tamperRejected
            && tamperError.Contains("重新预览", StringComparison.Ordinal)
            && MetadataStore.Load(target)["v1"].Title == "来源资料"
            && !LibraryBackupService.HasPendingImport(target);
        return result;
    }

    static LibraryConfig ReadConfig(string directory) =>
        JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(Path.Combine(directory, "config.json")))
        ?? throw new InvalidDataException("诊断配置损坏。");

    static string DigestSelected(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in new[] { "config.json", "metadata.json", "personal-ratings.json",
                     "review-history.jsonl", "review-views.json", "scrape-queue.json" })
        {
            var path = Path.Combine(directory, name);
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            if (File.Exists(path)) hash.AppendData(File.ReadAllBytes(path));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

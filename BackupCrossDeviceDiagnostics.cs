using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Both libraries use real path-derived IDs and separate absolute video roots.
internal static class BackupCrossDeviceDiagnostics
{
    internal static int RunToFile(string resultPath, string externalTargetRoot)
    {
        var ownedRoot = Path.Combine(Path.GetTempPath(),
            "yingge-cross-device-source-" + Guid.NewGuid().ToString("N"));
        var findings = new Dictionary<string, bool>(StringComparer.Ordinal);
        string? error = null;
        try
        {
            Directory.CreateDirectory(ownedRoot);
            foreach (var (name, passed) in Run(ownedRoot, externalTargetRoot)) findings[name] = passed;
        }
        catch (Exception ex)
        {
            findings["crossDevicePhysicalDiskDiagnostics"] = false;
            error = ex.ToString();
        }
        finally
        {
            try
            {
                var root = Path.GetFullPath(ownedRoot);
                if (Path.GetDirectoryName(root)!.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(
                        Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(root).StartsWith("yingge-cross-device-source-", StringComparison.Ordinal)
                    && Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (Exception ex) { error ??= "清理隔离样例失败：" + ex; }
        }
        var result = new
        {
            AllPassed = error is null && findings.Count > 0 && findings.Values.All(value => value),
            Checks = findings,
            Error = error,
            SourceVolume = Path.GetPathRoot(ownedRoot),
            TargetVolume = Path.GetPathRoot(externalTargetRoot)
        };
        var output = Path.GetFullPath(resultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.AllPassed ? 0 : 1;
    }

    // An optional second volume lets an isolated diagnostic exercise actual
    // cross-disk IO. Only a uniquely named directory created by this method
    // is removed; never point cleanup at the caller's root.
    internal static IReadOnlyDictionary<string, bool> Run(string root, string? externalTargetRoot = null)
    {
        if (externalTargetRoot is null) return RunCore(root, Path.Combine(root, "videos-b"));
        if (!Path.IsPathFullyQualified(externalTargetRoot) || !Directory.Exists(externalTargetRoot))
            throw new ArgumentException("异盘诊断目标必须是现有绝对目录。", nameof(externalTargetRoot));
        var parent = Path.GetFullPath(externalTargetRoot);
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("异盘诊断目标不能是链接目录。");
        var owned = Path.Combine(parent, "yingge-cross-device-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owned);
        try { return RunCore(root, owned); }
        finally
        {
            var verified = Path.GetFullPath(owned);
            if (Path.GetDirectoryName(verified)!.Equals(parent, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(verified).StartsWith("yingge-cross-device-test-", StringComparison.Ordinal)
                && Directory.Exists(verified)) Directory.Delete(verified, true);
        }
    }

    static IReadOnlyDictionary<string, bool> RunCore(string root, string bVideos)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var a = Path.Combine(root, "device-a");
        var b = Path.Combine(root, "device-b");
        var aVideos = Path.Combine(root, "videos-a");
        foreach (var path in new[] { a, b, aVideos, bVideos }) Directory.CreateDirectory(path);
        var a1 = Path.Combine(aVideos, "Movie One.mp4");
        var a2 = Path.Combine(aVideos, "second.mp4");
        var b1 = Path.Combine(bVideos, "Movie One.mp4");
        var b2 = Path.Combine(bVideos, "second.mp4");
        File.WriteAllBytes(a1, Encoding.UTF8.GetBytes("first-video-same-content-123456"));
        File.WriteAllBytes(a2, Encoding.UTF8.GetBytes("second-video-same-content-abcdef"));
        File.SetLastWriteTimeUtc(a1, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(a2, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.Copy(a1, b1); File.Copy(a2, b2);
        WriteIndex(a, aVideos, a1, a2);
        WriteIndex(b, bVideos, b1, b2);
        var ai1 = LibraryRelocationService.IdForPath(a1);
        var ai2 = LibraryRelocationService.IdForPath(a2);
        var bi1 = LibraryRelocationService.IdForPath(b1);
        var bi2 = LibraryRelocationService.IdForPath(b2);
        checks["crossDeviceRealPathIdsDiffer"] = ai1 != bi1 && ai2 != bi2;
        WriteConfig(a, new() { [ai1] = ["甲端标签"], [ai2] = ["甲端新增"] },
            new() { [ai1] = "甲端分类", ["historic-no-index"] = "历史分类" });
        WriteConfig(b, new() { [bi1] = ["乙端标签"] });
        WriteRatings(a, new() { [ai1] = 8, [ai2] = 9 });
        WriteRatings(b, new() { [bi1] = 5 });
        File.WriteAllText(Path.Combine(a, "metadata.json"), JsonSerializer.Serialize(
            new Dictionary<string, MovieMetadata>
            {
                [ai1] = new() { Code = "TEST-100", Title = "Movie One" }
            }));
        var beforeReview = new Dictionary<string, ReviewItemSnapshot>
            { [ai1] = new() { Review = new AiReviewRecord { VideoId = ai1, Status = "pending" } } };
        var afterReview = new Dictionary<string, ReviewItemSnapshot>
            { [ai1] = new() { Review = new AiReviewRecord { VideoId = ai1, Status = "approved" } } };
        ReviewTransactionStore.Append(a, ReviewTransactionStore.Create("approve", beforeReview, afterReview));
        var export = Path.Combine(root, "source-list.csv");
        File.WriteAllText(export, "source,title,code,url\nTMDB,Movie One,TEST-100,\n");
        var localA = new[] { new SiteListImportStore.LocalVideo(ai1, "Movie One", "Movie One",
            "TEST-100", "Movie One", "", "", a1,
            new FileInfo(a1).Length, new FileInfo(a1).LastWriteTime.Ticks) };
        var listPreview = SiteListImportStore.CreatePreview(a, export, localA);
        SiteListImportStore.Commit(a, listPreview, localA);
        var sourceList = SiteListImportStore.Load(a);
        var plain = LibraryBackupService.Create(a);
        try
        {
            _ = LibraryBackupService.CreateImportPreview(b, plain, LibraryImportScope.Tags);
            checks["crossDevicePlainBackupFailsClosed"] = false;
        }
        catch (InvalidOperationException ex)
        {
            checks["crossDevicePlainBackupFailsClosed"] = ex.Message.Contains("跨设备迁移包", StringComparison.Ordinal);
        }
        var package = LibraryBackupService.CreateCrossDevicePackage(a);
        var originalVideo = File.ReadAllBytes(a1);
        var originalWriteTime = File.GetLastWriteTimeUtc(a1);
        var stampPreserved = false;
        try
        {
            var replacement = (byte[])originalVideo.Clone();
            replacement[0] ^= 0x01;
            File.WriteAllBytes(a1, replacement);
            File.SetLastWriteTimeUtc(a1, originalWriteTime);
            stampPreserved = File.GetLastWriteTimeUtc(a1) == originalWriteTime;
            var replacedAtSamePath = LibraryBackupService.CreateImportPreview(a, package,
                LibraryImportScope.Tags);
            checks["crossDeviceSamePathSameSizeTimeButDifferentHashBlocked"] =
                stampPreserved && !replacedAtSamePath.MappingComplete;
        }
        catch (InvalidOperationException)
        {
            checks["crossDeviceSamePathSameSizeTimeButDifferentHashBlocked"] = stampPreserved;
        }
        finally
        {
            File.WriteAllBytes(a1, originalVideo);
            File.SetLastWriteTimeUtc(a1, originalWriteTime);
        }
        var transfer = Path.Combine(root, "transported-package");
        CopyPackage(package, transfer);
        checks["crossDeviceTransportPackageVerifies"] = LibraryBackupService.Verify(transfer).IsValid;
        var originalA = Digest(a);
        var originalB = Digest(b);
        var unavailable = a + "-unavailable";
        Directory.Move(a, unavailable);
        var scopes = LibraryImportScope.Tags | LibraryImportScope.Ratings
            | LibraryImportScope.Reviews | LibraryImportScope.SiteLists;
        var keep = LibraryBackupService.CreateImportPreview(b, transfer, scopes);
        checks["crossDevicePreviewShowsBothOriginalTagAndRatingValues"] = keep.MappingComplete
            && keep.MappingRows.Count == 2
            && keep.ConflictDetails.Any(row => row.Scope == LibraryImportScope.Tags
                && row.VideoId == bi1 && row.CurrentValue == "乙端标签" && row.BackupValue == "甲端标签")
            && keep.ConflictDetails.Any(row => row.Scope == LibraryImportScope.Ratings
                && row.VideoId == bi1 && row.CurrentValue == "5 分" && row.BackupValue == "8 分");
        checks["crossDevicePreviewMakesNoWrites"] = Digest(b) == originalB && Digest(unavailable) == originalA;
        LibraryBackupService.StageImport(b, keep);
        LibraryBackupService.CancelPendingImport(b);
        checks["crossDeviceCancelMakesNoWrites"] = Digest(b) == originalB
            && !LibraryBackupService.HasPendingImport(b);
        LibraryBackupService.StageImport(b, keep);
        var applied = LibraryBackupService.ApplyPendingImportAtStartup(b, out var error, false);
        var current = ReadConfig(b);
        checks["crossDeviceOfflineImportKeepsCurrentAndAddsMissing"] = applied && error.Length == 0
            && current.Tags[bi1].SequenceEqual(["乙端标签"])
            && current.Tags[bi2].SequenceEqual(["甲端新增"])
            && PersonalRatingStore.Get(b, bi1) == 5 && PersonalRatingStore.Get(b, bi2) == 9;
        var importedList = SiteListImportStore.Load(b);
        checks["crossDeviceSiteListMapsOnlyLocalVideoIdAndKeepsAudit"] = applied
            && importedList.Entries.Single().VideoId == bi1
            && importedList.Entries.Single().Key == sourceList.Entries.Single().Key
            && importedList.Actions.Single().EntryKeys.SequenceEqual(sourceList.Actions.Single().EntryKeys);
        var localB = new SiteListImportStore.LocalVideo(bi1, "Movie One", "Movie One",
            "", "", "", "", b1, new FileInfo(b1).Length, new FileInfo(b1).LastWriteTime.Ticks);
        checks["crossDeviceSiteListPlaybackGuardUsesTargetFingerprint"] = applied
            && SiteListImportStore.CanPlayMatchedEntry(importedList.Entries.Single(), localB);
        var importedReviews = ReviewTransactionStore.Load(b, out var invalidReviewLines);
        checks["crossDeviceReviewHistoryRemappedAndSealed"] = applied && invalidReviewLines == 0
            && importedReviews.Count == 1 && importedReviews[0].Before.ContainsKey(bi1)
            && importedReviews[0].After.ContainsKey(bi1)
            && !importedReviews[0].Before.ContainsKey(ai1);
        var after = Digest(b);
        var repeat = LibraryBackupService.CreateImportPreview(b, transfer, scopes);
        LibraryBackupService.StageImport(b, repeat);
        checks["crossDeviceOfflineRetryDoesNotDuplicate"] = !repeat.HasChanges
            && LibraryBackupService.ApplyPendingImportAtStartup(b, out _, false)
            && Digest(b) == after && SiteListImportStore.Load(b).Actions.Count == 1
            && ReviewTransactionStore.Load(b, out _).Count == 1;
        var prefer = LibraryBackupService.CreateImportPreview(b, transfer, scopes,
            LibraryImportConflictPolicy.PreferBackup);
        checks["crossDevicePreferBackupPreviewsOriginalValues"] = prefer.ConflictDetails.Count >= 2
            && Digest(b) == after;
        LibraryBackupService.StageImport(b, prefer);
        checks["crossDevicePreferBackupReplacesMappedValues"] =
            LibraryBackupService.ApplyPendingImportAtStartup(b, out _, false)
            && ReadConfig(b).Tags[bi1].SequenceEqual(["甲端标签"])
            && PersonalRatingStore.Get(b, bi1) == 8;

        var beforeOrphanChoice = Digest(b);
        var blockedOrphans = LibraryBackupService.CreateImportPreview(b, transfer,
            LibraryImportScope.Categories);
        checks["crossDeviceUnindexedCategoryVisibleAndBlockedByDefault"] =
            !blockedOrphans.MappingComplete && blockedOrphans.UnindexedCategoryIds.Count == 1
            && blockedOrphans.UnindexedCategoryIds[0] == "historic-no-index"
            && Digest(b) == beforeOrphanChoice;
        var skipOrphans = LibraryBackupService.CreateImportPreview(b, transfer,
            LibraryImportScope.Categories, skipUnindexedCategories: true);
        checks["crossDeviceExplicitlySkipOnlyUnindexedCategories"] = skipOrphans.MappingComplete
            && skipOrphans.HasChanges && skipOrphans.ConfirmationText.Contains("1 个旧视频编号", StringComparison.Ordinal);
        LibraryBackupService.StageImport(b, skipOrphans);
        checks["crossDeviceSkippedOrphansKeepOriginalPackage"] =
            LibraryBackupService.ApplyPendingImportAtStartup(b, out _, false)
            && ReadConfig(b).Categories.GetValueOrDefault(bi1) == "甲端分类"
            && !ReadConfig(b).Categories.ContainsKey("historic-no-index")
            && File.ReadAllText(Path.Combine(transfer, "config.json"))
                .Contains("historic-no-index", StringComparison.Ordinal);

        var duplicate = Path.Combine(bVideos, "same-content-copy.mp4");
        File.Copy(b1, duplicate);
        WriteIndex(b, bVideos, b1, b2, duplicate);
        var beforeAmbiguous = Digest(b);
        var ambiguous = LibraryBackupService.CreateImportPreview(b, transfer, scopes);
        checks["crossDeviceDuplicateContentNeedsManualChoice"] = !ambiguous.MappingComplete
            && ambiguous.MappingRows.Any(row => row.OldVideoId == ai1 && row.ExactCandidates.Count == 2)
            && Digest(b) == beforeAmbiguous;
        try { LibraryBackupService.StageImport(b, ambiguous); checks["crossDeviceAmbiguousCannotStage"] = false; }
        catch (InvalidOperationException) { checks["crossDeviceAmbiguousCannotStage"] = !LibraryBackupService.HasPendingImport(b); }
        var manual = LibraryBackupService.CreateImportPreview(b, transfer, scopes,
            manualChoices: new Dictionary<string, string> { [ai1] = b1 });
        checks["crossDeviceManualExactChoiceResolvesAmbiguity"] = manual.MappingComplete
            && manual.MappingRows.Any(row => row.OldVideoId == ai1 && row.TargetPath == b1);
        File.Delete(duplicate);
        File.WriteAllBytes(b1, Encoding.UTF8.GetBytes("same-name-but-different-bytes"));
        WriteIndex(b, bVideos, b1, b2);
        var beforeDifferent = Digest(b);
        var different = LibraryBackupService.CreateImportPreview(b, transfer, scopes);
        checks["crossDeviceSameNameDifferentContentFailsClosed"] = !different.MappingComplete
            && different.MappingRows.Any(row => row.OldVideoId == ai1 && row.Status.Contains("同名但内容不同", StringComparison.Ordinal))
            && Digest(b) == beforeDifferent;
        return checks;
    }

    static void WriteIndex(string data, string root, params string[] paths) =>
        File.WriteAllText(Path.Combine(data, "library-index.json"), JsonSerializer.Serialize(new LibraryIndex
        {
            Folders = [root], Entries = paths.Select(path => new IndexEntry
            {
                Path = path, Size = new FileInfo(path).Length,
                ModifiedTicks = new FileInfo(path).LastWriteTime.Ticks
            }).ToList()
        }));

    static void WriteConfig(string root, Dictionary<string, List<string>> tags,
        Dictionary<string, string>? categories = null) =>
        File.WriteAllText(Path.Combine(root, "config.json"),
            JsonSerializer.Serialize(new { folders = Array.Empty<string>(), tags,
                categories = categories ?? new Dictionary<string, string>() }));

    static void WriteRatings(string root, Dictionary<string, int> ratings) =>
        File.WriteAllText(Path.Combine(root, "personal-ratings.json"), JsonSerializer.Serialize(
            ratings.ToDictionary(item => item.Key, item => new { Score = item.Value })));

    static LibraryConfig ReadConfig(string root) =>
        JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(Path.Combine(root, "config.json")))
        ?? throw new InvalidDataException("诊断配置损坏。");

    static string Digest(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in new[] { "config.json", "personal-ratings.json",
                     "site-list-import.json", "review-history.jsonl" })
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            var path = Path.Combine(root, name);
            if (File.Exists(path)) hash.AppendData(File.ReadAllBytes(path));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    static void CopyPackage(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}

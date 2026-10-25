using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static partial class LibraryBackupService
{
    // This is a description of one verified package against one particular
    // live library state. The two digests make a confirmation expire rather
    // than silently replacing data which changed while the app was closing.
    internal sealed class RestorePreview
    {
        public required string BackupPath { get; init; }
        public required string DestinationPath { get; init; }
        public required DateTimeOffset CreatedUtc { get; init; }
        public required string Purpose { get; init; }
        public required string BackupManifestSha256 { get; init; }
        public required string LiveStateSha256 { get; init; }
        public required int BackupFileCount { get; init; }
        public required int CurrentManagedFileCount { get; init; }
        public required int OverwriteFileCount { get; init; }
        public required int AddFileCount { get; init; }
        public required int RemoveFileCount { get; init; }
        public required int QueueSidecarCleanupCount { get; init; }
        public int? BackupVideoCount { get; init; }
        public int? CurrentVideoCount { get; init; }
        public int? BackupQueueCount { get; init; }
        public int? CurrentQueueCount { get; init; }

        public string ConfirmationText =>
            "备份时间：" + CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) + "\n"
            + "备份位置：" + BackupPath + "\n"
            + "恢复到：" + DestinationPath + "\n\n"
            + "视频索引：" + CountText(CurrentVideoCount) + " → " + CountText(BackupVideoCount) + "\n"
            + "搜刮队列：" + CountText(CurrentQueueCount) + " → " + CountText(BackupQueueCount) + "\n"
            + "受管文件：当前 " + CurrentManagedFileCount + " 个，备份 " + BackupFileCount + " 个；"
            + "将覆盖 " + OverwriteFileCount + " 个、加入 " + AddFileCount + " 个、移除 " + RemoveFileCount
            + " 个，另清理 " + QueueSidecarCleanupCount + " 个旧队列缓存。\n\n"
            + "恢复策略：分类、标签、元数据、审核历史、AI 作业、图片和队列按备份替换；"
            + "备份中没有的受管文件会移除。API 凭据留在 Windows 凭据管理器，"
            + "AI / 付费来源用量计数不会倒退。AI 作业的加密请求只可由原 Windows 用户解密。视频原文件不受影响。\n"
            + "若备份是在软件运行中创建，多个文件可能不是同一时刻的数据。\n"
            + "确认后安排在下次启动前恢复；如备份或当前资料在此后发生变化，会停止恢复并要求重新预览。";

        static string CountText(int? count) => count?.ToString("N0", CultureInfo.CurrentCulture) ?? "无法读取";
    }

    internal static RestorePreview CreateRestorePreview(string dataDir, string backupDirectory)
    {
        var destination = Path.GetFullPath(dataDir);
        var source = EligibleBackupPath(destination, backupDirectory);
        var verification = Verify(source);
        if (!verification.IsValid) throw new InvalidDataException("备份校验失败：" + verification.Message);
        var manifestPath = Path.Combine(source, ManifestName);
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("备份清单无法读取。");
        if (RequiresCrossDeviceMapping(destination, source, manifest))
            throw new InvalidDataException("完整恢复不能把另一设备的视频路径直接覆盖到当前库。请使用选择性导入并先完成内容匹配。");
        var currentQueueCount = TryCountCurrentQueue(destination);
        var backedNames = manifest.Files.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentAllNames = EnumerateLiveManagedFiles(destination)
            .Select(item => item.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentNames = new HashSet<string>(currentAllNames, StringComparer.OrdinalIgnoreCase);
        // The queue's historical journal/snapshot files and usage ledgers have
        // their own restore rules. Count exact-replacement files separately.
        backedNames.RemoveWhere(name => QuotaFiles.Contains(name));
        currentNames.RemoveWhere(name => QuotaFiles.Contains(name) || IsQueueSidecar(name));
        var replace = backedNames.Count(currentNames.Contains);
        var added = backedNames.Count - replace;
        var removed = currentNames.Count(name => !backedNames.Contains(name));
        return new RestorePreview
        {
            BackupPath = source,
            DestinationPath = destination,
            CreatedUtc = manifest.CreatedUtc,
            Purpose = manifest.Purpose,
            BackupManifestSha256 = HashFile(manifestPath),
            LiveStateSha256 = SnapshotLiveState(destination),
            BackupFileCount = manifest.Files.Count,
            CurrentManagedFileCount = currentAllNames.Count,
            OverwriteFileCount = replace,
            AddFileCount = added,
            RemoveFileCount = removed,
            QueueSidecarCleanupCount = currentAllNames.Count(IsQueueSidecar),
            BackupVideoCount = CountJsonArrayProperty(Path.Combine(source, "library-index.json"), "Entries"),
            CurrentVideoCount = CountJsonArrayProperty(Path.Combine(destination, "library-index.json"), "Entries"),
            BackupQueueCount = CountJsonArray(Path.Combine(source, "scrape-queue.json")),
            CurrentQueueCount = currentQueueCount
        };
    }

    static string EligibleBackupPath(string dataDir, string backupDirectory)
    {
        var full = Path.GetFullPath(backupDirectory);
        if (!ReadableBackupRoots(dataDir).Any(root =>
                string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("只能恢复本机备份目录中的完整备份。");
        RejectReparseAncestors(full);
        RejectReparseAncestors(dataDir);
        return full;
    }

    static string SnapshotLiveState(string dataDir)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append("YinggeRestorePreviewV1|" + LibraryId(dataDir));
        foreach (var (relative, path) in EnumerateLiveManagedFiles(dataDir)
                     .OrderBy(item => item.Relative, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("资料目录包含文件链接，无法安全预览恢复：" + relative);
            Append(relative.ToUpperInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
            // AI jobs can change status without changing their length, so
            // their contents participate in the confirmation digest. Images
            // use name, length and timestamp to keep preview responsive.
            if (!relative.Contains('/') || relative.StartsWith("ai-jobs/", StringComparison.Ordinal))
                Append(HashFile(path));
        }
        return Convert.ToHexString(hash.GetHashAndReset());

        void Append(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
    }

    static IEnumerable<(string Relative, string Path)> EnumerateLiveManagedFiles(string dataDir)
    {
        RejectReparseAncestors(dataDir);
        foreach (var name in DataFiles.Append("scrape-queue.json")
                     .Concat(new[] { "scrape-queue.state.json", "scrape-queue.state.json.bak", "scrape-queue.json.bak",
                         RoleProfileStore.FileName + ".bak", "site-list-import.json.bak" }))
        {
            var path = Path.Combine(dataDir, name);
            if (File.Exists(path)) yield return (name, path);
        }
        foreach (var pattern in new[] { "scrape-queue.snapshot.*.json", "scrape-queue.journal.*.bin" })
            foreach (var path in Directory.EnumerateFiles(dataDir, pattern, SearchOption.TopDirectoryOnly))
                yield return (Path.GetFileName(path), path);
        foreach (var (subdir, allowed) in new[]
                 { ("posters", SafeImageName), ("thumbs", SafeCustomName), ("title-candidates", SafeTitleCandidateName), ("ai-jobs", SafeAiJobName) })
        {
            var directory = Path.Combine(dataDir, subdir);
            if (!Directory.Exists(directory)) continue;
            RejectReparseAncestors(directory);
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (allowed.IsMatch(name)) yield return (subdir + "/" + name, path);
            }
        }
    }

    static bool IsQueueSidecar(string name) =>
        name is "scrape-queue.state.json" or "scrape-queue.state.json.bak" or "scrape-queue.json.bak"
        || name.StartsWith("scrape-queue.snapshot.", StringComparison.Ordinal)
        || name.StartsWith("scrape-queue.journal.", StringComparison.Ordinal);

    static IEnumerable<string> EnumerateOrdinaryBackupFiles(string backupDirectory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(backupDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("备份目录包含文件链接。");
            var name = Path.GetFileName(entry);
            if (!Directory.Exists(entry))
            {
                yield return name;
                continue;
            }
            if (name is not ("posters" or "thumbs" or "title-candidates" or "ai-jobs"))
                throw new InvalidDataException("备份目录包含未知子目录：" + name);
            foreach (var child in Directory.EnumerateFileSystemEntries(entry, "*", SearchOption.TopDirectoryOnly))
            {
                if (Directory.Exists(child) || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("备份子目录包含文件链接或嵌套目录。");
                yield return name + "/" + Path.GetFileName(child);
            }
        }
    }

    static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    static int? CountJsonArrayProperty(string path, string property)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
                ? array.GetArrayLength() : null;
        }
        catch { return null; }
    }

    static int? CountJsonArray(string path)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.GetArrayLength() : null;
        }
        catch { return null; }
    }

    static int? TryCountCurrentQueue(string dataDir)
    {
        try { return ScrapeQueueStore.Load(dataDir).Count; }
        catch { return null; }
    }
}

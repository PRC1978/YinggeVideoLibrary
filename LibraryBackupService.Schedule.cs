using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static partial class LibraryBackupService
{
    const string ScheduleName = "backup-policy.json";
    const string PrunePendingName = "backup-prune-pending.json";
    const string OwnerName = ".backup-owner.json";
    const string StagingOwnerName = ".staging-owner";
    const int MaxKnownRoots = 32;
    static readonly object ScheduleGate = new();

    internal sealed class BackupSchedulePolicy
    {
        public bool Enabled { get; set; }
        public string Frequency { get; set; } = "daily";
        public int RetentionCount { get; set; } = 3;
        // Empty means the original data-directory/backups location.
        public string DestinationDirectory { get; set; } = "";
        public DateTimeOffset? NextDueUtc { get; set; }
        public List<string> KnownBackupRoots { get; set; } = [];
    }

    sealed class OfflineBackupRequest
    {
        public DateTimeOffset RequestedUtc { get; set; }
        public string Kind { get; set; } = "manual";
    }

    sealed class BackupOwner
    {
        public int Version { get; set; } = 1;
        public string LibraryId { get; set; } = "";
    }

    static string DefaultBackupRoot(string dataDir) => Path.Combine(dataDir, "backups");
    static string SchedulePath(string dataDir) => Path.Combine(dataDir, ScheduleName);
    static string ScheduleBackupPath(string dataDir) => SchedulePath(dataDir) + ".bak";
    static string PrunePendingPath(string dataDir) => Path.Combine(dataDir, PrunePendingName);

    internal static BackupSchedulePolicy ReadSchedule(string dataDir)
    {
        lock (ScheduleGate)
        {
            var path = SchedulePath(dataDir);
            var fallback = ScheduleBackupPath(dataDir);
            if (!File.Exists(path) && !File.Exists(fallback)) return new BackupSchedulePolicy();
            if (TryReadScheduleFile(path, out var primary)) return primary!;
            if (TryReadScheduleFile(fallback, out var recovered))
            {
                var archived = true;
                if (File.Exists(path))
                    try { ArchiveDamagedSchedule(path); }
                    catch (Exception) { archived = false; }
                if (archived)
                    try { File.Copy(fallback, path, overwrite: true); }
                    catch (Exception) { /* The validated fallback remains usable. */ }
                return recovered!;
            }
            throw new InvalidDataException("定期备份设置及其副本均损坏。请在设置页重置，原文件会保留供恢复历史备份位置。");
        }
    }

    static bool TryReadScheduleFile(string path, out BackupSchedulePolicy? policy)
    {
        policy = null;
        if (!File.Exists(path)) return false;
        try
        {
            policy = JsonSerializer.Deserialize<BackupSchedulePolicy>(File.ReadAllText(path));
            if (policy is null) return false;
            ValidatePolicyShape(policy);
            return true;
        }
        catch (Exception) { policy = null; return false; }
    }

    static void ArchiveDamagedSchedule(string path)
    {
        var archive = path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")
            + "-" + Guid.NewGuid().ToString("N")[..8];
        File.Copy(path, archive, overwrite: false);
    }

    internal static void ResetDamagedSchedule(string dataDir)
    {
        lock (ScheduleGate)
        {
            if (TryReadScheduleFile(SchedulePath(dataDir), out _)
                || TryReadScheduleFile(ScheduleBackupPath(dataDir), out _))
                throw new InvalidOperationException("仍可从原设置或副本恢复，无须重置。");
            Directory.CreateDirectory(dataDir);
            foreach (var path in new[] { SchedulePath(dataDir), ScheduleBackupPath(dataDir) })
                if (File.Exists(path)) ArchiveDamagedSchedule(path);
            WriteSchedule(dataDir, new BackupSchedulePolicy());
        }
    }

    internal static void SaveSchedule(string dataDir, BackupSchedulePolicy requested, IEnumerable<string> videoRoots)
    {
        lock (ScheduleGate)
        {
            ValidatePolicyShape(requested);
            var existing = ReadSchedule(dataDir);
            var saved = new BackupSchedulePolicy
            {
                Enabled = requested.Enabled,
                Frequency = requested.Frequency,
                RetentionCount = requested.RetentionCount,
                DestinationDirectory = requested.DestinationDirectory,
                KnownBackupRoots = [.. existing.KnownBackupRoots]
            };
            var oldRoot = CurrentBackupRoot(dataDir, existing);
            if (!string.Equals(oldRoot, DefaultBackupRoot(dataDir), StringComparison.OrdinalIgnoreCase))
                saved.KnownBackupRoots.Add(oldRoot);
            if (!string.IsNullOrWhiteSpace(saved.DestinationDirectory))
            {
                saved.DestinationDirectory = ValidateBackupDestination(dataDir,
                    saved.DestinationDirectory, videoRoots);
                var newRoot = CurrentBackupRoot(dataDir, saved);
                EnsureOwnedRoot(dataDir, newRoot);
                saved.KnownBackupRoots.Add(newRoot);
            }
            saved.KnownBackupRoots = saved.KnownBackupRoots
                .Where(path => !string.IsNullOrWhiteSpace(path) && path.Length <= 240)
                .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .TakeLast(MaxKnownRoots).ToList();
            saved.NextDueUtc = saved.Enabled
                ? existing.Enabled && existing.Frequency == saved.Frequency && existing.NextDueUtc is { } prior
                    ? prior : ComputeNextDue(DateTimeOffset.Now, saved.Frequency).ToUniversalTime()
                : null;
            Directory.CreateDirectory(dataDir);
            WriteSchedule(dataDir, saved);
            if (!saved.Enabled && File.Exists(PendingOfflineBackupPath(dataDir))
                && ReadOfflineBackupRequest(PendingOfflineBackupPath(dataDir)).Kind == "periodic")
                File.Delete(PendingOfflineBackupPath(dataDir));
        }
    }

    static void ValidatePolicyShape(BackupSchedulePolicy policy)
    {
        if (policy.DestinationDirectory is null || policy.KnownBackupRoots is null
            || policy.KnownBackupRoots.Any(path => path is null))
            throw new InvalidDataException("备份位置记录无效。");
        if (policy.Frequency is not ("daily" or "weekly" or "monthly"))
            throw new InvalidDataException("备份周期只能选每天、每周或每月。");
        if (policy.RetentionCount is < 1 or > 30)
            throw new InvalidDataException("自动备份保留轮数须为 1 到 30。");
        if (policy.DestinationDirectory.Length > 240)
            throw new InvalidDataException("备份目标路径过长。");
        if (policy.KnownBackupRoots.Count > MaxKnownRoots || policy.KnownBackupRoots.Any(path => path.Length > 240))
            throw new InvalidDataException("历史备份位置记录无效。");
    }

    static void WriteSchedule(string dataDir, BackupSchedulePolicy policy)
    {
        var path = SchedulePath(dataDir);
        var fallback = ScheduleBackupPath(dataDir);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var fallbackTemporary = fallback + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var serialized = JsonSerializer.Serialize(policy, JsonOptions);
            File.WriteAllText(temporary, serialized);
            File.WriteAllText(fallbackTemporary, serialized);
            File.Move(fallbackTemporary, fallback, true);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(fallbackTemporary)) File.Delete(fallbackTemporary);
        }
    }

    internal static string ValidateBackupDestination(string dataDir, string selectedDirectory,
        IEnumerable<string> videoRoots)
    {
        if (string.IsNullOrWhiteSpace(selectedDirectory)) return "";
        var path = Path.GetFullPath(selectedDirectory.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var driveRoot = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.Length > 240 || string.Equals(path, driveRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择已有的普通文件夹，不能选磁盘或网络共享根目录。");
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("备份目标不存在或目前不可访问。");
        RejectReparseAncestors(path);
        var library = Path.GetFullPath(dataDir);
        if (PathsOverlap(path, library))
            throw new InvalidDataException("自定义备份位置不能与资料库数据目录重叠。可留空使用默认位置。");
        foreach (var videoRoot in videoRoots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            string source;
            try { source = Path.GetFullPath(videoRoot); }
            catch (Exception) { continue; }
            if (PathsOverlap(path, source))
                throw new InvalidDataException("备份目标不能位于视频文件夹内，也不能包含视频文件夹。");
        }
        var probe = Path.Combine(path, ".yingge-write-test-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(probe, ""); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
        return path;
    }

    static bool PathsOverlap(string first, string second) =>
        IsWithin(first, second) || IsWithin(second, first);

    static bool IsWithin(string path, string directory)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static void RejectReparseAncestors(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("备份目标不能使用文件夹链接或联接点。");
    }

    static string LibraryId(string dataDir)
    {
        var canonical = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    static string CurrentBackupRoot(string dataDir) => CurrentBackupRoot(dataDir, ReadSchedule(dataDir));

    static string CurrentBackupRoot(string dataDir, BackupSchedulePolicy policy) =>
        string.IsNullOrWhiteSpace(policy.DestinationDirectory)
            ? DefaultBackupRoot(dataDir)
            : Path.Combine(policy.DestinationDirectory, "YinggeVideoLibrary-" + LibraryId(dataDir));

    internal static string CurrentBackupDirectory(string dataDir) => CurrentBackupRoot(dataDir);

    static string PrepareBackupRoot(string dataDir, bool forceDefault = false)
    {
        if (forceDefault)
        {
            RejectReparseAncestors(Directory.Exists(DefaultBackupRoot(dataDir))
                ? DefaultBackupRoot(dataDir) : dataDir);
            return DefaultBackupRoot(dataDir);
        }
        var policy = ReadSchedule(dataDir);
        if (string.IsNullOrWhiteSpace(policy.DestinationDirectory))
        {
            RejectReparseAncestors(Directory.Exists(DefaultBackupRoot(dataDir))
                ? DefaultBackupRoot(dataDir) : dataDir);
            return DefaultBackupRoot(dataDir);
        }
        var destination = ValidateBackupDestination(dataDir, policy.DestinationDirectory, ReadVideoRoots(dataDir));
        var root = CurrentBackupRoot(dataDir, policy);
        if (!IsWithin(root, destination)) throw new InvalidDataException("备份目标路径无效。");
        EnsureOwnedRoot(dataDir, root);
        return root;
    }

    static IEnumerable<string> ReadVideoRoots(string dataDir)
    {
        var config = Path.Combine(dataDir, "config.json");
        if (!File.Exists(config)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(config));
        return document.RootElement.TryGetProperty("folders", out var roots) && roots.ValueKind == JsonValueKind.Array
            ? roots.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "").ToArray() : [];
    }

    static void EnsureOwnedRoot(string dataDir, string root)
    {
        if (!Directory.Exists(root)) Directory.CreateDirectory(root);
        RejectReparseAncestors(root);
        var marker = Path.Combine(root, OwnerName);
        var id = LibraryId(dataDir);
        if (File.Exists(marker))
        {
            var owner = JsonSerializer.Deserialize<BackupOwner>(File.ReadAllText(marker));
            if (owner?.Version != 1 || owner.LibraryId != id)
                throw new InvalidDataException("备份专用目录的归属标记不匹配。");
            return;
        }
        if (Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly).Any())
            throw new InvalidDataException("备份专用目录已有文件但没有归属标记，不能自动接管。");
        File.WriteAllText(marker, JsonSerializer.Serialize(new BackupOwner { LibraryId = id }, JsonOptions));
    }

    static IEnumerable<string> ReadableBackupRoots(string dataDir)
    {
        var defaultRoot = Path.GetFullPath(DefaultBackupRoot(dataDir));
        if (!Directory.Exists(defaultRoot) || IsOrdinaryDirectory(defaultRoot)) yield return defaultRoot;
        BackupSchedulePolicy? policy = null;
        try { policy = ReadSchedule(dataDir); }
        catch (Exception) { /* The legacy default root remains readable. */ }
        if (policy is null) yield break;
        foreach (var candidate in policy.KnownBackupRoots.Append(CurrentBackupRoot(dataDir, policy))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string root;
            try { root = Path.GetFullPath(candidate); }
            catch (Exception) { continue; }
            if (root.Equals(defaultRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsOwnedRoot(dataDir, root)) yield return root;
        }
    }

    static bool IsOrdinaryDirectory(string directory)
    {
        try { RejectReparseAncestors(directory); return true; }
        catch (Exception) { return false; }
    }

    static bool IsOwnedRoot(string dataDir, string root)
    {
        try
        {
            if (!Directory.Exists(root)) return false;
            RejectReparseAncestors(root);
            var marker = Path.Combine(root, OwnerName);
            var owner = JsonSerializer.Deserialize<BackupOwner>(File.ReadAllText(marker));
            return owner?.Version == 1 && owner.LibraryId == LibraryId(dataDir);
        }
        catch { return false; }
    }

    static DateTimeOffset ReadBackupCreationTime(string backupDirectory)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(backupDirectory, ManifestName)));
            return manifest?.CreatedUtc ?? DateTimeOffset.MinValue;
        }
        catch { return DateTimeOffset.MinValue; }
    }

    static DateTimeOffset ComputeNextDue(DateTimeOffset localNow, string frequency) => frequency switch
    {
        "weekly" => localNow.AddDays(7),
        "monthly" => localNow.AddMonths(1),
        _ => localNow.AddDays(1)
    };

    internal static bool CheckAndStageDuePeriodicBackup(string dataDir)
    {
        lock (ScheduleGate)
        {
            var policy = ReadSchedule(dataDir);
            if (!policy.Enabled || policy.NextDueUtc is not { } due || due > DateTimeOffset.UtcNow
                || File.Exists(PendingOfflineBackupPath(dataDir))) return false;
            if (File.Exists(PendingPath(dataDir)) || File.Exists(ActivePath(dataDir))
                || File.Exists(PendingImportPath(dataDir)) || File.Exists(ActiveImportPath(dataDir))
                || DuplicateMetadataTransactionStore.HasPending(dataDir)) return false;
            Directory.CreateDirectory(dataDir);
            var path = PendingOfflineBackupPath(dataDir);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new OfflineBackupRequest
                    { RequestedUtc = DateTimeOffset.UtcNow, Kind = "periodic" }));
                File.Move(temporary, path, false);
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    static OfflineBackupRequest ReadOfflineBackupRequest(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<OfflineBackupRequest>(File.ReadAllText(path))
                ?? throw new InvalidDataException("待执行备份记录为空。");
        }
        catch (JsonException ex) { throw new InvalidDataException("待执行备份记录损坏。", ex); }
    }

    static void AdvancePeriodicScheduleAfterSuccess(string dataDir)
    {
        lock (ScheduleGate)
        {
            var policy = ReadSchedule(dataDir);
            if (!policy.Enabled || policy.NextDueUtc is not { } due || due > DateTimeOffset.UtcNow) return;
            policy.NextDueUtc = ComputeNextDue(DateTimeOffset.Now, policy.Frequency).ToUniversalTime();
            WriteSchedule(dataDir, policy);
        }
    }

    static void PrunePeriodicBackups(string dataDir)
    {
        var keep = ReadSchedule(dataDir).RetentionCount;
        var completed = new List<(string Path, string Root, DateTimeOffset Created)>();
        foreach (var root in ReadableBackupRoots(dataDir).Where(root => Directory.Exists(root) && IsOrdinaryDirectory(root)))
        foreach (var directory in Directory.EnumerateDirectories(root, "auto-*", SearchOption.TopDirectoryOnly))
        {
            if (!Path.GetDirectoryName(directory)!.Equals(root, StringComparison.OrdinalIgnoreCase)
                || ContainsReparsePoint(directory) || !Verify(directory).IsValid) continue;
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(directory, ManifestName)));
            if (manifest?.Purpose == "periodic") completed.Add((directory, root, manifest.CreatedUtc));
        }
        foreach (var old in completed.OrderByDescending(item => item.Created).Skip(keep))
        {
            // Recheck containment, owner and every reparse point immediately before deletion.
            if (!IsWithin(old.Path, old.Root) || !Path.GetDirectoryName(old.Path)!.Equals(old.Root, StringComparison.OrdinalIgnoreCase)
                || !IsOrdinaryDirectory(old.Root)
                || (!old.Root.Equals(DefaultBackupRoot(dataDir), StringComparison.OrdinalIgnoreCase)
                    && !IsOwnedRoot(dataDir, old.Root))
                || ContainsReparsePoint(old.Path) || !Verify(old.Path).IsValid) continue;
            Directory.Delete(old.Path, recursive: true);
        }
    }

    static string CleanupStaleStagingAtStartup(string dataDir, bool forceDefault)
    {
        string[] roots = forceDefault ? [DefaultBackupRoot(dataDir)] : ReadableBackupRoots(dataDir).ToArray();
        var removed = 0;
        var kept = 0;
        foreach (var root in roots.Where(Directory.Exists))
        {
            if (!IsOrdinaryDirectory(root))
                throw new InvalidDataException("备份根目录是文件夹链接，不能安全清理或创建备份。");
            foreach (var directory in Directory.EnumerateDirectories(root, ".building-*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(directory);
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^\.building-[0-9a-fA-F]{32}$")
                    || !Path.GetDirectoryName(directory)!.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || Directory.GetCreationTimeUtc(directory) > DateTime.UtcNow.AddHours(-24)
                    || ContainsReparsePoint(directory)
                    || !File.Exists(Path.Combine(directory, StagingOwnerName))
                    || File.ReadAllText(Path.Combine(directory, StagingOwnerName)) != LibraryId(dataDir))
                {
                    kept++;
                    continue;
                }
                try { Directory.Delete(directory, recursive: true); removed++; }
                catch (Exception) { kept++; }
            }
        }
        return kept > 0 ? $"另有 {kept} 个临时备份目录需人工核查；已清理 {removed} 个旧目录。"
            : removed > 0 ? $"已清理 {removed} 个过期临时备份目录。" : "";
    }

    static string RetryPendingPruneAtStartup(string dataDir)
    {
        var marker = PrunePendingPath(dataDir);
        if (!File.Exists(marker)) return "";
        try
        {
            using var gate = new FileStream(Path.Combine(dataDir, SessionLeaseName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            PrunePeriodicBackups(dataDir);
            File.Delete(marker);
            return "之前未完成的自动备份轮换已补做。";
        }
        catch (Exception ex) { return "自动备份轮换仍待重试：" + ex.Message; }
    }

    static bool ContainsReparsePoint(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return true;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
            if ((attributes & FileAttributes.Directory) != 0 && ContainsReparsePoint(entry)) return true;
        }
        return false;
    }
}

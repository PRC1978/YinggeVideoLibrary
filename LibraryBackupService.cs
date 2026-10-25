using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

// Backups contain user data, not videos, regenerated thumbnails, or Windows credentials.
// The manifest proves integrity of the copied bytes; it does not make writes to
// different live stores one atomic snapshot. Offline restore uses a session lease.
internal static partial class LibraryBackupService
{
    const int FormatVersion = 1;
    const string ManifestName = "backup-manifest.json";
    const string PendingName = "restore-pending.json";
    const string ActiveName = "restore-active.json";
    const string PendingOfflineBackupName = "backup-pending.json";
    const string LastOfflineBackupName = "backup-last-result.json";
    const string SessionLeaseName = ".library-session.lock";
    static readonly string[] DataFiles =
    [
        "config.json", "metadata.json", "library-index.json", "personal-ratings.json",
        "review-history.jsonl", "review-views.json", "graph-move-history.json",
        "duplicate-metadata-history.json",
        "site-list-import.json",
        RoleProfileStore.FileName,
        "ai-budget.json", "javinfo-usage.json",
        "scrape-source-backoff.json"
    ];
    static readonly HashSet<string> QuotaFiles = new(StringComparer.Ordinal)
        { "ai-budget.json", "javinfo-usage.json" };
    static readonly Regex SafeImageName = new(@"^[a-zA-Z0-9_-]+\.(?:jpg|jpeg|png|webp|bmp)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex SafeCustomName = new(@"^[a-zA-Z0-9_-]+-custom\.(?:jpg|jpeg|png|webp|bmp)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex SafeTitleCandidateName = new(@"^[0-9A-F]{64}\.json$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex SafeAiJobName = new(@"^[0-9a-fA-F]{32}\.json$", RegexOptions.Compiled);
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal sealed class Manifest
    {
        public int Version { get; set; }
        public string Purpose { get; set; } = "user";
        public DateTimeOffset CreatedUtc { get; set; }
        public string SourceLibraryId { get; set; } = "";
        public string SourceMachineId { get; set; } = "";
        public List<VideoFingerprint>? VideoFingerprints { get; set; }
        public List<Entry> Files { get; set; } = [];
    }

    internal sealed class VideoFingerprint
    {
        public string OldVideoId { get; set; } = "";
        public string OldPath { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public long Length { get; set; }
        public string Sha256 { get; set; } = "";
    }

    internal sealed class Entry
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
    }

    sealed class RestoreRequest
    {
        public string BackupPath { get; set; } = "";
        public string RollbackPath { get; set; } = "";
        public string BackupManifestSha256 { get; set; } = "";
        public string LiveStateSha256 { get; set; } = "";
    }

    internal sealed class OfflineBackupResult
    {
        public DateTimeOffset CompletedUtc { get; set; }
        public bool Succeeded { get; set; }
        public string Path { get; set; } = "";
        public string Message { get; set; } = "";
    }

    internal readonly record struct Verification(bool IsValid, string Message, int FileCount);

    static string BackupRoot(string dataDir) => CurrentBackupRoot(dataDir);
    static string PendingPath(string dataDir) => Path.Combine(dataDir, PendingName);
    static string ActivePath(string dataDir) => Path.Combine(dataDir, ActiveName);
    static string PendingOfflineBackupPath(string dataDir) => Path.Combine(dataDir, PendingOfflineBackupName);
    static string LastOfflineBackupPath(string dataDir) => Path.Combine(dataDir, LastOfflineBackupName);

    internal static bool HasPendingOfflineBackup(string dataDir) => File.Exists(PendingOfflineBackupPath(dataDir));

    internal static void CancelPendingOfflineBackup(string dataDir) =>
        File.Delete(PendingOfflineBackupPath(dataDir));

    internal static OfflineBackupResult? LastOfflineBackupResult(string dataDir)
    {
        try
        {
            var path = LastOfflineBackupPath(dataDir);
            return File.Exists(path) ? JsonSerializer.Deserialize<OfflineBackupResult>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    internal static void StageOfflineBackup(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        if (File.Exists(PendingPath(dataDir)) || File.Exists(ActivePath(dataDir))
            || File.Exists(PendingImportPath(dataDir)) || File.Exists(ActiveImportPath(dataDir))
            || DuplicateMetadataTransactionStore.HasPending(dataDir))
            throw new InvalidOperationException("资料恢复或导入尚未完成，不能同时排定一致性备份。");
        var pending = PendingOfflineBackupPath(dataDir);
        var temporary = pending + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new OfflineBackupRequest
            { RequestedUtc = DateTimeOffset.UtcNow, Kind = "manual" }));
        File.Move(temporary, pending, true);
    }

    // The user schedules this while the app runs. The next launch copies the
    // library before any MainWindow loads it, with exclusive access to the
    // shared data directory. This is the consistency path for a full backup.
    internal static bool ApplyPendingOfflineBackupAtStartup(string dataDir, out string error,
        bool requireSingleInstance = true)
    {
        error = "";
        string scheduleWarning = "";
        try { CheckAndStageDuePeriodicBackup(dataDir); }
        catch (Exception ex)
        {
            scheduleWarning = "定期备份设置无法读取或排定：" + ex.Message;
        }
        var pending = PendingOfflineBackupPath(dataDir);
        if (!File.Exists(pending))
        {
            var retryMessage = RetryPendingPruneAtStartup(dataDir);
            error = string.Join(" ", new[] { scheduleWarning, retryMessage }.Where(message => message.Length > 0));
            return true;
        }
        FileStream gate;
        try
        {
            gate = new FileStream(Path.Combine(dataDir, SessionLeaseName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            error = "一致性备份尚未执行：另一份映格仍在使用资料库。请关闭所有窗口后重启。";
            return false;
        }
        using (gate)
        try
        {
            if (requireSingleInstance && LegacyInstanceLikelyRunning())
            {
                error = "一致性备份尚未执行：还有旧版映格窗口正在运行。请关闭后重启。";
                return false;
            }
            var periodic = ReadOfflineBackupRequest(pending).Kind == "periodic";
            var forceDefault = false;
            if (!periodic)
            {
                try { ReadSchedule(dataDir); }
                catch (Exception ex)
                {
                    forceDefault = true;
                    scheduleWarning = "备份设置损坏，手动待办已安全改用默认数据目录：" + ex.Message;
                }
            }
            var stagingWarning = CleanupStaleStagingAtStartup(dataDir, forceDefault);
            var completed = Create(dataDir, periodic: periodic, forceDefault: forceDefault);
            string completionMessage = "一致性备份已完成并校验。";
            if (scheduleWarning.Length > 0) completionMessage += " " + scheduleWarning;
            if (stagingWarning.Length > 0) completionMessage += " " + stagingWarning;
            try
            {
                if (periodic)
                {
                    AdvancePeriodicScheduleAfterSuccess(dataDir);
                    try { PrunePeriodicBackups(dataDir); File.Delete(PrunePendingPath(dataDir)); }
                    catch
                    {
                        File.WriteAllText(PrunePendingPath(dataDir), DateTimeOffset.UtcNow.ToString("O"));
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                completionMessage += " 轮换清理或下次计划未完成：" + ex.Message;
            }
            WriteOfflineBackupResult(dataDir, new OfflineBackupResult
            {
                CompletedUtc = DateTimeOffset.UtcNow, Succeeded = true, Path = completed,
                Message = completionMessage
            });
            File.Delete(pending);
            error = scheduleWarning;
            return true;
        }
        catch (Exception ex)
        {
            error = "一致性备份失败：" + ex.Message;
            try
            {
                WriteOfflineBackupResult(dataDir, new OfflineBackupResult
                {
                    CompletedUtc = DateTimeOffset.UtcNow, Succeeded = false, Message = error
                });
            }
            catch { /* Preserve the pending marker even when the result cannot be written. */ }
            // Keep the request for the next launch, but let the user reach
            // Settings to cancel or retry after an I/O or configuration error.
            return true;
        }
    }

    static void WriteOfflineBackupResult(string dataDir, OfflineBackupResult result)
    {
        var path = LastOfflineBackupPath(dataDir);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(result, JsonOptions));
        File.Move(temporary, path, true);
    }

    internal static FileStream AcquireSessionLease(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        // Every updated app copy, including an exe launched from another folder,
        // holds a compatible handle for its entire library session.
        return new FileStream(Path.Combine(dataDir, SessionLeaseName), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.ReadWrite);
    }

    internal static string? LatestBackup(string dataDir)
    {
        return ReadableBackupRoots(dataDir)
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
            .Select(path => (Path: path, Created: ReadBackupCreationTime(path)))
            .OrderByDescending(item => item.Created)
            .Select(item => item.Path)
            .FirstOrDefault(path => Verify(path).IsValid);
    }

    internal static string Create(string dataDir, bool rollback = false, bool periodic = false,
        bool forceDefault = false)
    {
        Directory.CreateDirectory(dataDir);
        var root = rollback ? DefaultBackupRoot(dataDir) : PrepareBackupRoot(dataDir, forceDefault);
        Directory.CreateDirectory(root);
        RejectReparseAncestors(root);
        var staging = Path.Combine(root, ".building-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllText(Path.Combine(staging, StagingOwnerName), LibraryId(dataDir));
            var manifest = new Manifest { Version = FormatVersion, SourceLibraryId = LibraryId(dataDir),
                SourceMachineId = CurrentMachineId(),
                Purpose = rollback ? "rollback" : periodic ? "periodic" : "user",
                CreatedUtc = DateTimeOffset.UtcNow };
            foreach (var name in DataFiles)
            {
                var source = name switch
                {
                    "scrape-source-backoff.json" => ValidSourceBackoffForBackup(dataDir),
                    RoleProfileStore.FileName => ValidRoleProfilesForBackup(dataDir),
                    _ => Path.Combine(dataDir, name)
                };
                if (source is null || !File.Exists(source)) continue;
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("资料文件不能是文件链接：" + name);
                var destination = Path.Combine(staging, name);
                if (name == "config.json" && !rollback) CopyConfigWithoutSecrets(source, destination);
                else File.Copy(source, destination);
                AddEntry(staging, name, manifest);
            }
            if (!rollback && !manifest.Files.Any(item => item.Path == "config.json"))
                throw new InvalidDataException("未找到有效配置，无法创建完整备份。");

            // Load takes the queue cache gate and returns independent item copies.
            // A single JSON snapshot avoids copying an active journal mid-write.
            var queuePath = Path.Combine(staging, "scrape-queue.json");
            File.WriteAllText(queuePath, JsonSerializer.Serialize(ScrapeQueueStore.Load(dataDir), JsonOptions));
            AddEntry(staging, "scrape-queue.json", manifest);

            CopyImages(Path.Combine(dataDir, "posters"), staging, "posters", SafeImageName, manifest);
            CopyImages(Path.Combine(dataDir, "thumbs"), staging, "thumbs", SafeCustomName, manifest);
            CopyTitleCandidateSnapshots(dataDir, staging, manifest);
            CopyAiJobSnapshots(dataDir, staging, manifest);
            File.Delete(Path.Combine(staging, StagingOwnerName));
            File.WriteAllText(Path.Combine(staging, ManifestName), JsonSerializer.Serialize(manifest, JsonOptions));
            var verified = Verify(staging, allowRawRollback: rollback);
            if (!verified.IsValid) throw new InvalidDataException("备份创建后校验失败：" + verified.Message);
            var completed = Path.Combine(root, (rollback ? ".rollback-" : periodic ? "auto-" : "")
                + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.Move(staging, completed);
            return completed;
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch { }
            throw;
        }
    }

    static string? ValidSourceBackoffForBackup(string dataDir)
    {
        var primary = Path.Combine(dataDir, "scrape-source-backoff.json");
        var fallback = primary + ".bak";
        if (ScrapeQueueStore.ValidateSourceBackoffSnapshot(primary)) return primary;
        if (ScrapeQueueStore.ValidateSourceBackoffSnapshot(fallback)) return fallback;
        if (File.Exists(primary) || File.Exists(fallback))
            throw new InvalidDataException("搜刮来源冷却记录及其备份均不可读取；请先修复队列记录。");
        return null;
    }

    static string? ValidRoleProfilesForBackup(string dataDir)
    {
        var primary = Path.Combine(dataDir, RoleProfileStore.FileName);
        var fallback = primary + ".bak";
        foreach (var path in new[] { primary, fallback })
        {
            if (!File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("角色档案文件不能是文件链接。");
            try { RoleProfileStore.VerifyFile(path); return path; }
            catch (InvalidDataException) { }
        }
        if (File.Exists(primary) || File.Exists(fallback))
            throw new InvalidDataException("角色档案及其安全副本都无法读取；请先修复或恢复备份。");
        return null;
    }

    static void CopyConfigWithoutSecrets(string source, string destination)
    {
        var config = JsonNode.Parse(File.ReadAllText(source)) as JsonObject
            ?? throw new InvalidDataException("配置文件不是有效 JSON 对象。");
        foreach (var key in config.Select(pair => pair.Key).Where(key =>
                     key.Equals("tmdbToken", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("aiKey", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("javinfoKey", StringComparison.OrdinalIgnoreCase)).ToArray())
            config[key] = "";
        File.WriteAllText(destination, config.ToJsonString(JsonOptions));
    }

    static void CopyImages(string sourceDirectory, string staging, string relativeDirectory, Regex allowed, Manifest manifest)
    {
        if (!Directory.Exists(sourceDirectory)) return;
        RejectReparseAncestors(sourceDirectory);
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(source);
            if (!allowed.IsMatch(name)) continue;
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("图片文件不能是文件链接：" + name);
            var relative = relativeDirectory + "/" + name;
            var destination = Path.Combine(staging, relativeDirectory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            AddEntry(staging, relative, manifest);
        }
    }

    static void CopyTitleCandidateSnapshots(string dataDir, string staging, Manifest manifest)
    {
        var sourceDirectory = Path.Combine(dataDir, "title-candidates");
        if (!Directory.Exists(sourceDirectory)) return;
        RejectReparseAncestors(sourceDirectory);
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(source);
            if (!SafeTitleCandidateName.IsMatch(name)) continue;
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0
                || !ValidTitleCandidateSnapshot(source, name))
                throw new InvalidDataException("标题候选快照损坏或是文件链接：" + name);
            var destination = Path.Combine(staging, "title-candidates", name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            AddEntry(staging, "title-candidates/" + name, manifest);
        }
    }

    static bool ValidTitleCandidateSnapshot(string path, string name)
    {
        try
        {
            if (new FileInfo(path).Length > 4L * 1024 * 1024) return false;
            var snapshot = JsonSerializer.Deserialize<ScrapeCandidateCache.TitleCandidateSnapshot>(File.ReadAllText(path));
            if (snapshot is not { SchemaVersion: 1, Candidates: { Count: > 0 and <= 12 } }
                || string.IsNullOrWhiteSpace(snapshot.VideoId)
                || snapshot.Candidates.Any(candidate => candidate is null || candidate.SearchMetadata is null)) return false;
            var expected = Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(snapshot.VideoId.Trim().ToUpperInvariant()))) + ".json";
            return name.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static void CopyAiJobSnapshots(string dataDir, string staging, Manifest manifest)
    {
        var sourceDirectory = Path.Combine(dataDir, "ai-jobs");
        if (!Directory.Exists(sourceDirectory)) return;
        RejectReparseAncestors(sourceDirectory);
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*.json", SearchOption.TopDirectoryOnly))
            if (!SafeAiJobName.IsMatch(Path.GetFileName(source)) ||
                (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("AI 作业文件名无效或指向文件链接：" + Path.GetFileName(source));
        // List holds the cross-process AI job gate and returns a validated,
        // independent snapshot. A job may be atomically replaced after it
        // returns, so copying the original files would reintroduce a race.
        foreach (var job in AiJobStore.List(dataDir))
        {
            var name = job.Id + ".json";
            if (!SafeAiJobName.IsMatch(name))
                throw new InvalidDataException("AI 作业文件名无效：" + name);
            var destination = Path.Combine(staging, "ai-jobs", name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, JsonSerializer.Serialize(job, JsonOptions));
            AddEntry(staging, "ai-jobs/" + name, manifest);
        }
    }

    static void AddEntry(string root, string relative, Manifest manifest)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        using var stream = File.OpenRead(path);
        manifest.Files.Add(new Entry
        {
            Path = relative, Size = stream.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()
        });
    }

    static bool IsAllowed(string relative)
    {
        if (DataFiles.Contains(relative, StringComparer.Ordinal) || relative == "scrape-queue.json") return true;
        var slash = relative.IndexOf('/');
        if (slash < 0 || relative.LastIndexOf('/') != slash) return false;
        var directory = relative[..slash];
        var name = relative[(slash + 1)..];
        return directory switch
        {
            "posters" => SafeImageName.IsMatch(name),
            "thumbs" => SafeCustomName.IsMatch(name),
            "title-candidates" => SafeTitleCandidateName.IsMatch(name),
            "ai-jobs" => SafeAiJobName.IsMatch(name),
            _ => false
        };
    }

    internal static Verification Verify(string backupDirectory, bool allowRawRollback = false)
    {
        try
        {
            RejectReparseAncestors(backupDirectory);
            var manifestPath = Path.Combine(backupDirectory, ManifestName);
            if (!File.Exists(manifestPath)) return new(false, "缺少备份清单；旧式无清单备份不能自动恢复。", 0);
            if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
                return new(false, "备份清单不能是文件链接。", 0);
            if (new FileInfo(manifestPath).Length > 32L * 1024 * 1024)
                return new(false, "备份清单过大。", 0);
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
            if (manifest?.Version != FormatVersion || manifest.Files is null)
                return new(false, "不支持的备份版本或清单格式。", 0);
            if (!VerifyCrossDeviceFingerprintManifest(backupDirectory, manifest))
                return new(false, "跨设备视频指纹清单格式或索引不匹配。", 0);
            if (allowRawRollback ? manifest.Purpose != "rollback" : manifest.Purpose is not ("user" or "periodic"))
                return new(false, "备份类型不符，无法恢复。", 0);
            if (manifest.Files.Count > 200_000) return new(false, "备份清单文件数量超出限制。", 0);
            // Inspect the tree before opening entries: directory links must
            // never cause verification to read outside the selected package.
            var actual = EnumerateOrdinaryBackupFiles(backupDirectory)
                .Where(path => path != ManifestName).ToArray();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in manifest.Files)
            {
                if (!IsAllowed(item.Path) || !names.Add(item.Path) || item.Size < 0
                    || item.Sha256.Length != 64 || !item.Sha256.All(Uri.IsHexDigit))
                    return new(false, "清单含有非法或重复文件。", 0);
                var path = Path.Combine(backupDirectory, item.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) return new(false, "缺少文件：" + item.Path, 0);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    return new(false, "备份包含文件链接：" + item.Path, 0);
                using var stream = File.OpenRead(path);
                if (stream.Length != item.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new(false, "文件校验失败：" + item.Path, 0);
                if (item.Path.EndsWith(".json", StringComparison.Ordinal) && (!allowRawRollback || item.Path == "scrape-queue.json"))
                {
                    if (item.Path == "duplicate-metadata-history.json" && item.Size > 32L * 1024 * 1024)
                        return new(false, "重复资料撤销历史过大。", 0);
                    using var json = JsonDocument.Parse(File.ReadAllText(path));
                    if (item.Path == "config.json"
                        && json.RootElement.TryGetProperty("schemaVersion", out var configVersion)
                        && (configVersion.ValueKind != JsonValueKind.Number
                            || !configVersion.TryGetInt32(out var configSchema)
                            || configSchema is < 1 or > 2))
                        return new(false, "备份配置格式版本不受当前程序支持。", 0);
                    if (item.Path == "metadata.json" && json.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var metadata in json.RootElement.EnumerateObject())
                            if (metadata.Value.ValueKind != JsonValueKind.Object
                                || (metadata.Value.TryGetProperty("SchemaVersion", out var metadataVersion)
                                    && (metadataVersion.ValueKind != JsonValueKind.Number
                                        || !metadataVersion.TryGetInt32(out var schema)
                                        || schema is < 1 or > 2)))
                                return new(false, "备份元数据格式版本不受当前程序支持。", 0);
                    if (item.Path == "scrape-queue.json" && json.RootElement.ValueKind != JsonValueKind.Array)
                        return new(false, "搜刮队列快照格式不正确。", 0);
                    if (item.Path == "scrape-queue.json")
                    {
                        var jobs = JsonSerializer.Deserialize<List<ScrapeQueueItem>>(File.ReadAllText(path));
                        if (jobs is null || jobs.Any(job => job is null || string.IsNullOrWhiteSpace(job.VideoId)
                                || (job.Kind == "title" ? string.IsNullOrWhiteSpace(job.SearchTitle)
                                    : job.Kind != "code" || string.IsNullOrWhiteSpace(job.Code)))
                            || jobs.Select(job => job.VideoId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != jobs.Count)
                            return new(false, "搜刮队列快照包含非法或重复任务。", 0);
                    }
                    if (item.Path == "scrape-source-backoff.json" &&
                        !ScrapeQueueStore.ValidateSourceBackoffSnapshot(path))
                        return new(false, "搜刮来源冷却记录格式不正确。", 0);
                    if (item.Path == "graph-move-history.json" &&
                        (item.Size > 128L * 1024 * 1024 ||
                         json.RootElement.TryGetProperty("Version", out var graphVersion) == false ||
                         graphVersion.ValueKind != JsonValueKind.Number || graphVersion.GetInt32() != 1 ||
                         json.RootElement.TryGetProperty("Entries", out var graphEntries) == false ||
                         graphEntries.ValueKind != JsonValueKind.Array || graphEntries.GetArrayLength() > 3 ||
                         CategoryGraphMoveHistoryStore.Read(path).Count != graphEntries.GetArrayLength()))
                        return new(false, "图形分类撤销历史格式不正确。", 0);
                    if (item.Path == "duplicate-metadata-history.json")
                    {
                        if (json.RootElement.ValueKind != JsonValueKind.Array)
                            return new(false, "重复资料撤销历史格式不正确。", 0);
                        DuplicateMetadataTransactionStore.VerifyHistoryFile(path);
                    }
                    if (item.Path == "site-list-import.json")
                    {
                        if (item.Size > 128L * 1024 * 1024)
                            return new(false, "站点导入清单过大。", 0);
                        _ = SiteListImportStore.Load(backupDirectory);
                    }
                    if (item.Path == RoleProfileStore.FileName)
                        RoleProfileStore.VerifyFile(path);
                    if (item.Path.StartsWith("title-candidates/", StringComparison.Ordinal)
                        && !ValidTitleCandidateSnapshot(path, Path.GetFileName(path)))
                        return new(false, "标题候选快照格式不正确。", 0);
                    if (item.Path.StartsWith("ai-jobs/", StringComparison.Ordinal))
                    {
                        if (item.Size > 8L * 1024 * 1024) return new(false, "AI 作业快照过大。", 0);
                        try { AiJobStore.Get(backupDirectory, Path.GetFileNameWithoutExtension(path)); }
                        catch { return new(false, "AI 作业快照格式不正确。", 0); }
                    }
                    if (item.Path is not ("scrape-queue.json" or "duplicate-metadata-history.json")
                        && json.RootElement.ValueKind != JsonValueKind.Object)
                        return new(false, "JSON 文件格式不正确：" + item.Path, 0);
                    if (QuotaFiles.Contains(item.Path) && !allowRawRollback
                        && !ValidQuotaSnapshot(item.Path, json.RootElement))
                        return new(false, "用量账本格式不正确：" + item.Path, 0);
                }
                else if (!allowRawRollback && item.Path == "review-history.jsonl")
                    foreach (var line in File.ReadLines(path))
                        if (!string.IsNullOrWhiteSpace(line)) using (JsonDocument.Parse(line)) { }
            }
            if ((!allowRawRollback && !names.Contains("config.json")) || !names.Contains("scrape-queue.json"))
                return new(false, "缺少配置或搜刮队列快照。", 0);
            if (actual.Any(path => !names.Contains(path))) return new(false, "备份目录包含清单外文件。", 0);
            if (actual.Count() != names.Count) return new(false, "备份目录与清单文件数量不一致。", 0);
            return new(true, "校验通过", manifest.Files.Count);
        }
        catch (Exception ex) { return new(false, ex.Message, 0); }
    }

    internal static void StageRestore(string dataDir, string backupDirectory)
        => StageRestore(dataDir, CreateRestorePreview(dataDir, backupDirectory));

    internal static void StageRestore(string dataDir, RestorePreview preview)
    {
        if (File.Exists(PendingOfflineBackupPath(dataDir)))
            throw new InvalidOperationException("一致性备份已排定；请先重启完成备份，再排定恢复。");
        if (File.Exists(ActivePath(dataDir)) || File.Exists(PendingImportPath(dataDir))
            || File.Exists(ActiveImportPath(dataDir))
            || DuplicateMetadataTransactionStore.HasPending(dataDir))
            throw new InvalidOperationException("上次恢复或导入尚未处理完，不能排定新的恢复。");
        var fresh = CreateRestorePreview(dataDir, preview.BackupPath);
        if (!string.Equals(fresh.DestinationPath, preview.DestinationPath, StringComparison.OrdinalIgnoreCase)
            || fresh.BackupManifestSha256 != preview.BackupManifestSha256
            || fresh.LiveStateSha256 != preview.LiveStateSha256)
            throw new InvalidOperationException("备份或当前资料在预览后发生变化，请重新预览后再确认恢复。");
        var pending = PendingPath(dataDir);
        var temporary = pending + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new RestoreRequest
        {
            BackupPath = fresh.BackupPath, BackupManifestSha256 = fresh.BackupManifestSha256,
            LiveStateSha256 = fresh.LiveStateSha256
        }));
        File.Move(temporary, pending, true);
    }

    // Called before MainWindow loads any shared data. A crash during replacement
    // leaves ActiveName and a complete rollback package for the next launch.
    internal static bool ApplyPendingAtStartup(string dataDir, out string error, bool requireSingleInstance = true)
    {
        error = "";
        if (!File.Exists(PendingPath(dataDir)) && !File.Exists(ActivePath(dataDir))) return true;
        try
        {
            Directory.CreateDirectory(dataDir);
            using var gate = new FileStream(Path.Combine(dataDir, SessionLeaseName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            if (requireSingleInstance && LegacyInstanceLikelyRunning())
            {
                error = "检测到旧版或另一份映格仍在运行；请关闭所有窗口后重新启动再恢复。";
                return false;
            }
            if (File.Exists(ActivePath(dataDir)))
            {
                var active = ReadRequest(ActivePath(dataDir));
                var rollbackCheck = Verify(active.RollbackPath, allowRawRollback: true);
                if (!rollbackCheck.IsValid) throw new InvalidDataException("上次恢复中断，回滚包损坏：" + rollbackCheck.Message);
                RestoreFiles(dataDir, active.RollbackPath, rawRollback: true);
                File.Delete(ActivePath(dataDir));
                File.Delete(PendingPath(dataDir));
                error = "上次恢复中断，已恢复原数据。请重新选择备份。";
                return false;
            }
            var pending = ReadRequest(PendingPath(dataDir));
            try { EligibleBackupPath(dataDir, pending.BackupPath); }
            catch
            {
                File.Delete(PendingPath(dataDir));
                throw new InvalidDataException("待恢复备份路径已变更或不再可信，当前数据未修改。请重新预览。");
            }
            var check = Verify(pending.BackupPath);
            if (!check.IsValid)
            {
                File.Delete(PendingPath(dataDir));
                throw new InvalidDataException("待恢复备份校验失败，当前数据未修改：" + check.Message);
            }
            if (pending.BackupManifestSha256.Length != 64 || pending.LiveStateSha256.Length != 64
                || !HashFile(Path.Combine(pending.BackupPath, ManifestName)).Equals(
                    pending.BackupManifestSha256, StringComparison.OrdinalIgnoreCase)
                || !SnapshotLiveState(dataDir).Equals(pending.LiveStateSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(PendingPath(dataDir));
                throw new InvalidDataException("备份或当前资料在确认后发生变化；已取消恢复，当前数据未修改。请重新预览。");
            }
            var restoreManifest = JsonSerializer.Deserialize<Manifest>(
                File.ReadAllText(Path.Combine(pending.BackupPath, ManifestName)))
                ?? throw new InvalidDataException("待恢复清单损坏。");
            if (RequiresCrossDeviceMapping(dataDir, pending.BackupPath, restoreManifest))
                throw new InvalidDataException("来源视频路径与当前库不同，完整恢复已停止。请改用选择性导入。");
            var rollback = Create(dataDir, rollback: true);
            var activePath = ActivePath(dataDir);
            var activeTemporary = activePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(activeTemporary, JsonSerializer.Serialize(new RestoreRequest
                { BackupPath = pending.BackupPath, RollbackPath = rollback }));
            File.Move(activeTemporary, activePath, true);
            RestoreFiles(dataDir, pending.BackupPath);
            File.Delete(PendingPath(dataDir));
            File.Delete(activePath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    static RestoreRequest ReadRequest(string path) =>
        JsonSerializer.Deserialize<RestoreRequest>(File.ReadAllText(path))
        ?? throw new InvalidDataException("恢复请求损坏。");

    static bool LegacyInstanceLikelyRunning()
    {
        var currentId = Environment.ProcessId;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == currentId) continue;
                try
                {
                    if (process.ProcessName.Contains("映格", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Contains("YinggeVideoLibrary", StringComparison.OrdinalIgnoreCase)
                        || process.MainWindowTitle.Contains("映格", StringComparison.OrdinalIgnoreCase)) return true;
                    var version = process.MainModule?.FileVersionInfo;
                    if (version is not null && (string.Equals(version.ProductName, "映格视频库", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(version.FileDescription, "映格视频库", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(version.OriginalFilename, "映格视频库.dll", StringComparison.OrdinalIgnoreCase))) return true;
                }
                catch (Exception) { /* Restricted processes are covered by the cooperative lease when updated. */ }
            }
        }
        return false;
    }

    static void RestoreFiles(string dataDir, string backupDirectory, bool rawRollback = false)
    {
        var verification = Verify(backupDirectory, allowRawRollback: rawRollback);
        if (!verification.IsValid) throw new InvalidDataException(verification.Message);
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(backupDirectory, ManifestName)))!;
        RejectReparseAncestors(dataDir);
        foreach (var name in new[] { "posters", "thumbs", "title-candidates", "ai-jobs" })
        {
            var directory = Path.Combine(dataDir, name);
            if (Directory.Exists(directory)) RejectReparseAncestors(directory);
        }
        foreach (var (relative, path) in EnumerateLiveManagedFiles(dataDir))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("当前资料包含文件链接，已停止恢复：" + relative);
        var included = manifest.Files.Select(item => item.Path).ToHashSet(StringComparer.Ordinal);
        // A restore is an exact replacement of the managed data set. In particular,
        // rolling back a crash must remove files introduced by the interrupted run.
        // Store sidecars are local recovery state, never a separate version
        // of the restored library. Clear them for both ordinary restore and
        // rollback before publishing the selected snapshot.
        foreach (var sidecar in new[] { RoleProfileStore.FileName + ".bak", "site-list-import.json.bak" })
            File.Delete(Path.Combine(dataDir, sidecar));
        foreach (var name in DataFiles)
            if (!QuotaFiles.Contains(name) && !included.Contains(name)) File.Delete(Path.Combine(dataDir, name));
        foreach (var (subdir, pattern) in new[] { ("posters", SafeImageName), ("thumbs", SafeCustomName), ("title-candidates", SafeTitleCandidateName), ("ai-jobs", SafeAiJobName) })
        {
            var folder = Path.Combine(dataDir, subdir);
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
                if (pattern.IsMatch(Path.GetFileName(path)) && !included.Contains(subdir + "/" + Path.GetFileName(path)))
                    File.Delete(path);
        }
        // Keep usage counters monotonic across restoration. Missing local
        // ledgers recover from the package; same-day counters use the maximum.
        foreach (var item in manifest.Files.Where(item => item.Path != "scrape-queue.json" && !QuotaFiles.Contains(item.Path)))
        {
            var destination = Path.Combine(dataDir, item.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            File.Copy(Path.Combine(backupDirectory, item.Path.Replace('/', Path.DirectorySeparatorChar)), temporary);
            File.Move(temporary, destination, true);
        }
        foreach (var name in QuotaFiles)
            if (included.Contains(name)) RestoreQuotaLedger(name, dataDir, backupDirectory);
        // The restored source cooldown is a single verified file. A stale
        // sidecar backup must not resurrect pre-restore limits on the next load.
        File.Delete(Path.Combine(dataDir, "scrape-source-backoff.json.bak"));
        // The old manifest/snapshot/journal family wins over a legacy queue file.
        // Reset that family only while offline, then publish the verified snapshot.
        var queueSource = Path.Combine(backupDirectory, "scrape-queue.json");
        var queueDestination = Path.Combine(dataDir, "scrape-queue.json");
        var queueTemporary = queueDestination + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        File.Copy(queueSource, queueTemporary);
        foreach (var name in new[] { "scrape-queue.state.json", "scrape-queue.state.json.bak", "scrape-queue.json.bak" })
            if (File.Exists(Path.Combine(dataDir, name))) File.Delete(Path.Combine(dataDir, name));
        foreach (var pattern in new[] { "scrape-queue.snapshot.*.json", "scrape-queue.journal.*.bin" })
            foreach (var path in Directory.EnumerateFiles(dataDir, pattern, SearchOption.TopDirectoryOnly)) File.Delete(path);
        File.Move(queueTemporary, queueDestination, true);
        ScrapeQueueStore.ForgetCachedStateAfterOfflineRestore(dataDir);
    }

    static void RestoreQuotaLedger(string name, string dataDir, string backupDirectory)
    {
        var destination = Path.Combine(dataDir, name);
        var source = Path.Combine(backupDirectory, name);
        if (!File.Exists(destination))
        {
            var missingTemporary = destination + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            File.Copy(source, missingTemporary);
            File.Move(missingTemporary, destination, true);
            return;
        }

        // A corrupt current counter must stay corrupt so the paid-source guard
        // fails closed; replacing it with an older value could reset the quota.
        JsonObject? current, saved;
        string currentDay, savedDay;
        double currentAmount, savedAmount;
        try
        {
            current = JsonNode.Parse(File.ReadAllText(destination)) as JsonObject;
            saved = JsonNode.Parse(File.ReadAllText(source)) as JsonObject;
            if (current is null || saved is null) return;
            var dayField = name == "ai-budget.json" ? "Day" : "UtcDay";
            var amountField = name == "ai-budget.json" ? "EstimatedUsd" : "ReservedCalls";
            currentDay = current[dayField]?.GetValue<string>() ?? "";
            savedDay = saved[dayField]?.GetValue<string>() ?? "";
            currentAmount = name == "javinfo-usage.json"
                ? current[amountField]?.GetValue<int>() ?? 0 : current[amountField]?.GetValue<double>() ?? 0;
            savedAmount = name == "javinfo-usage.json"
                ? saved[amountField]?.GetValue<int>() ?? 0 : saved[amountField]?.GetValue<double>() ?? 0;
            if (!ValidLedgerDay(currentDay) || !ValidLedgerDay(savedDay)
                || !double.IsFinite(currentAmount) || !double.IsFinite(savedAmount)
                || currentAmount < 0 || savedAmount < 0) return;
        }
        catch (Exception) when (File.Exists(destination))
        {
            // Retain the current ledger if either side cannot be compared.
            return;
        }
        var newer = string.CompareOrdinal(savedDay, currentDay);
        if (newer < 0) return;
        if (newer > 0) current = saved;
        else
        {
            if (name == "javinfo-usage.json")
            {
                current["ReservedCalls"] = checked((int)Math.Max(currentAmount, savedAmount));
                var currentNext = ReadNextAllowed(current);
                var savedNext = ReadNextAllowed(saved);
                current["NextAllowedUtc"] = (currentNext >= savedNext ? currentNext : savedNext).ToString("O");
            }
            else
            {
                // Preserve the ledger whose protected total is larger so its
                // reported/estimated breakdown is not silently rewritten by
                // an older backup. The next-allowed clock also stays monotonic.
                var currentNext = ReadNextAllowed(current);
                var savedNext = ReadNextAllowed(saved);
                var currentLocalNext = ReadNextAllowed(current, "NextLocalAllowedUtc");
                var savedLocalNext = ReadNextAllowed(saved, "NextLocalAllowedUtc");
                var currentCloudNext = ReadNextAllowed(current, "NextCloudAllowedUtc");
                var savedCloudNext = ReadNextAllowed(saved, "NextCloudAllowedUtc");
                if (savedAmount > currentAmount) current = saved;
                current["EstimatedUsd"] = Math.Max(currentAmount, savedAmount);
                current["NextAllowedUtc"] = (currentNext >= savedNext ? currentNext : savedNext).ToString("O");
                current["NextLocalAllowedUtc"] = (currentLocalNext >= savedLocalNext
                    ? currentLocalNext : savedLocalNext).ToString("O");
                current["NextCloudAllowedUtc"] = (currentCloudNext >= savedCloudNext
                    ? currentCloudNext : savedCloudNext).ToString("O");
            }
        }
        var temporary = destination + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, current.ToJsonString(JsonOptions));
        File.Move(temporary, destination, true);
    }

    static bool ValidLedgerDay(string day) => day.Length == 0 ||
        DateOnly.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);

    static bool ValidQuotaSnapshot(string name, JsonElement element)
    {
        var dayField = name == "ai-budget.json" ? "Day" : "UtcDay";
        var amountField = name == "ai-budget.json" ? "EstimatedUsd" : "ReservedCalls";
        if (!element.TryGetProperty(dayField, out var day) || day.ValueKind != JsonValueKind.String
            || !ValidLedgerDay(day.GetString() ?? "")
            || !element.TryGetProperty(amountField, out var amount) || amount.ValueKind != JsonValueKind.Number)
            return false;
        if (name == "ai-budget.json")
        {
            if (!amount.TryGetDouble(out var cost) || !double.IsFinite(cost) || cost < 0) return false;
            foreach (var field in new[] { "ReportedUsageUsd", "UnverifiedUsd" })
                if (element.TryGetProperty(field, out var value) &&
                    (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var optionalCost)
                        || !double.IsFinite(optionalCost) || optionalCost < 0)) return false;
            foreach (var field in new[] { "AttemptCount", "ReportedUsageCount", "InputTokens", "OutputTokens",
                "UnverifiedInputEstimateTokens", "UnverifiedOutputLimitTokens" })
                if (element.TryGetProperty(field, out var value) &&
                    (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count)
                        || count < 0)) return false;
            foreach (var field in new[] { "NextAllowedUtc", "NextLocalAllowedUtc", "NextCloudAllowedUtc" })
                if (element.TryGetProperty(field, out var next) &&
                    (next.ValueKind != JsonValueKind.String ||
                     !DateTimeOffset.TryParse(next.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.RoundtripKind, out _))) return false;
            return true;
        }
        return amount.TryGetInt32(out var calls) && calls >= 0;
    }

    static DateTimeOffset ReadNextAllowed(JsonObject ledger, string field = "NextAllowedUtc")
    {
        var value = ledger[field]?.GetValue<string>();
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : DateTimeOffset.MinValue;
    }
}

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
internal static class LibraryBackupService
{
    const int FormatVersion = 1;
    const string ManifestName = "backup-manifest.json";
    const string PendingName = "restore-pending.json";
    const string ActiveName = "restore-active.json";
    const string SessionLeaseName = ".library-session.lock";
    static readonly string[] DataFiles =
    [
        "config.json", "metadata.json", "library-index.json", "personal-ratings.json",
        "review-history.jsonl", "ai-budget.json", "javinfo-usage.json"
    ];
    static readonly HashSet<string> QuotaFiles = new(StringComparer.Ordinal)
        { "ai-budget.json", "javinfo-usage.json" };
    static readonly Regex SafeImageName = new(@"^[a-zA-Z0-9_-]+\.(?:jpg|jpeg|png|webp|bmp)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex SafeCustomName = new(@"^[a-zA-Z0-9_-]+-custom\.(?:jpg|jpeg|png|webp|bmp)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal sealed class Manifest
    {
        public int Version { get; set; }
        public string Purpose { get; set; } = "user";
        public DateTimeOffset CreatedUtc { get; set; }
        public List<Entry> Files { get; set; } = [];
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
    }

    internal readonly record struct Verification(bool IsValid, string Message, int FileCount);

    static string BackupRoot(string dataDir) => Path.Combine(dataDir, "backups");
    static string PendingPath(string dataDir) => Path.Combine(dataDir, PendingName);
    static string ActivePath(string dataDir) => Path.Combine(dataDir, ActiveName);

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
        var root = BackupRoot(dataDir);
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root)
            .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    internal static string Create(string dataDir, bool rollback = false)
    {
        Directory.CreateDirectory(dataDir);
        var root = BackupRoot(dataDir);
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, ".building-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var manifest = new Manifest { Version = FormatVersion, Purpose = rollback ? "rollback" : "user",
                CreatedUtc = DateTimeOffset.UtcNow };
            foreach (var name in DataFiles)
            {
                var source = Path.Combine(dataDir, name);
                if (!File.Exists(source)) continue;
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
            File.WriteAllText(Path.Combine(staging, ManifestName), JsonSerializer.Serialize(manifest, JsonOptions));
            var verified = Verify(staging, allowRawRollback: rollback);
            if (!verified.IsValid) throw new InvalidDataException("备份创建后校验失败：" + verified.Message);
            var completed = Path.Combine(root, (rollback ? ".rollback-" : "")
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
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(source);
            if (!allowed.IsMatch(name)) continue;
            var relative = relativeDirectory + "/" + name;
            var destination = Path.Combine(staging, relativeDirectory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            AddEntry(staging, relative, manifest);
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
            _ => false
        };
    }

    internal static Verification Verify(string backupDirectory, bool allowRawRollback = false)
    {
        try
        {
            var manifestPath = Path.Combine(backupDirectory, ManifestName);
            if (!File.Exists(manifestPath)) return new(false, "缺少备份清单；旧式无清单备份不能自动恢复。", 0);
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
            if (manifest?.Version != FormatVersion || manifest.Files is null)
                return new(false, "不支持的备份版本或清单格式。", 0);
            if (allowRawRollback ? manifest.Purpose != "rollback" : manifest.Purpose != "user")
                return new(false, "备份类型不符，无法恢复。", 0);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in manifest.Files)
            {
                if (!IsAllowed(item.Path) || !names.Add(item.Path) || item.Size < 0
                    || item.Sha256.Length != 64 || !item.Sha256.All(Uri.IsHexDigit))
                    return new(false, "清单含有非法或重复文件。", 0);
                var path = Path.Combine(backupDirectory, item.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) return new(false, "缺少文件：" + item.Path, 0);
                using var stream = File.OpenRead(path);
                if (stream.Length != item.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new(false, "文件校验失败：" + item.Path, 0);
                if (item.Path.EndsWith(".json", StringComparison.Ordinal) && (!allowRawRollback || item.Path == "scrape-queue.json"))
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(path));
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
                    if (item.Path != "scrape-queue.json" && json.RootElement.ValueKind != JsonValueKind.Object)
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
            var actual = Directory.EnumerateFiles(backupDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(backupDirectory, path).Replace('\\', '/'))
                .Where(path => path != ManifestName);
            if (actual.Any(path => !names.Contains(path))) return new(false, "备份目录包含清单外文件。", 0);
            return new(true, "校验通过", manifest.Files.Count);
        }
        catch (Exception ex) { return new(false, ex.Message, 0); }
    }

    internal static void StageRestore(string dataDir, string backupDirectory)
    {
        var full = Path.GetFullPath(backupDirectory);
        var root = Path.GetFullPath(BackupRoot(dataDir));
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("只能恢复本机备份目录中的完整备份。");
        if (File.Exists(ActivePath(dataDir)))
            throw new InvalidOperationException("上次恢复尚未处理完，不能排定新的恢复。");
        var verification = Verify(full);
        if (!verification.IsValid) throw new InvalidDataException(verification.Message);
        var pending = PendingPath(dataDir);
        var temporary = pending + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new RestoreRequest { BackupPath = full }));
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
            var check = Verify(pending.BackupPath);
            if (!check.IsValid)
            {
                File.Delete(PendingPath(dataDir));
                throw new InvalidDataException("待恢复备份校验失败，当前数据未修改：" + check.Message);
            }
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
        var included = manifest.Files.Select(item => item.Path).ToHashSet(StringComparer.Ordinal);
        // A restore is an exact replacement of the managed data set. In particular,
        // rolling back a crash must remove files introduced by the interrupted run.
        foreach (var name in DataFiles)
            if (!QuotaFiles.Contains(name) && !included.Contains(name)) File.Delete(Path.Combine(dataDir, name));
        foreach (var (subdir, pattern) in new[] { ("posters", SafeImageName), ("thumbs", SafeCustomName) })
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
            else current["EstimatedUsd"] = Math.Max(currentAmount, savedAmount);
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
            return amount.TryGetDouble(out var cost) && double.IsFinite(cost) && cost >= 0;
        return amount.TryGetInt32(out var calls) && calls >= 0;
    }

    static DateTimeOffset ReadNextAllowed(JsonObject ledger)
    {
        var value = ledger["NextAllowedUtc"]?.GetValue<string>();
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : DateTimeOffset.MinValue;
    }
}

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Video IDs are hashes of absolute paths. Relocation must therefore remap
// every ID-bound store together, never just replace the configured folder.
// This service does no live-library writes until the user confirms a preview.
internal static class LibraryRelocationService
{
    const string ManifestName = "library-relocation-source.json";
    const string PendingGuardName = "library-relocation-pending.json";
    static readonly string[] VideoExtensions =
    [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".flv", ".ts", ".mts", ".m2ts", ".webm", ".mpg", ".mpeg", ".vob"];
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    internal sealed class SourceEntry
    {
        public string OldPath { get; set; } = "";
        public string OldVideoId { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public long Length { get; set; }
        public long ModifiedTicks { get; set; }
        public string Sha256 { get; set; } = "";
    }

    internal sealed class SourceManifest
    {
        public int Version { get; set; } = 1;
        public string SourceRoot { get; set; } = "";
        public string IndexSha256 { get; set; } = "";
        public DateTimeOffset CapturedUtc { get; set; } = DateTimeOffset.UtcNow;
        public List<SourceEntry> Entries { get; set; } = [];
    }

    internal enum MatchKind { Exact, Ambiguous, DifferentContent, Missing }

    internal sealed class PreviewRow
    {
        public required SourceEntry Source { get; init; }
        public required MatchKind Kind { get; init; }
        public string? ProposedTargetPath { get; init; }
        public IReadOnlyList<string> ExactContentPaths { get; init; } = [];
        public string? SameRelativePath { get; init; }
    }

    internal sealed class Preview
    {
        public required string DataDir { get; init; }
        public required string SourceRoot { get; init; }
        public required string TargetRoot { get; init; }
        public required string LiveStateSha256 { get; init; }
        public required string IndexSha256 { get; init; }
        public required string SourceManifestSha256 { get; init; }
        public required IReadOnlyList<PreviewRow> Rows { get; init; }
        public int AutomaticCount => Rows.Count(row => row.Kind == MatchKind.Exact);
        public int ManualCount => Rows.Count - AutomaticCount;
    }

    internal sealed record Decision(string OldVideoId, string TargetPath, bool AcceptDifferentContent = false);
    internal sealed record Confirmation(string PackagePath, int VideoCount, int ManualDecisions,
        string Message);
    sealed class TargetEvidence
    {
        public string Path { get; set; } = "";
        public long Length { get; set; }
        public string Sha256 { get; set; } = "";
    }
    sealed class PendingGuard
    {
        public int Version { get; set; } = 1;
        public string PackagePath { get; set; } = "";
        public List<TargetEvidence> Targets { get; set; } = [];
    }

    internal static string IdForPath(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..24].ToLowerInvariant();

    internal static SourceManifest PrepareSourceManifest(string dataDir, string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        var manifest = CaptureSourceManifest(dataDir, sourceRoot, cancellationToken);
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, ManifestName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(temporary, path, true);
            return manifest;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static Preview CreatePreview(string dataDir, string sourceRoot, string targetRoot,
        CancellationToken cancellationToken = default)
    {
        var data = Path.GetFullPath(dataDir);
        var source = NormalizeRoot(sourceRoot);
        var target = NormalizeRoot(targetRoot);
        if (IsWithin(source, target) || IsWithin(target, source))
            throw new InvalidOperationException("原目录和新目录不能相同或相互包含。");
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException("新视频目录不存在：" + target);
        RejectLinksOnPath(target);
        var indexPath = Path.Combine(data, "library-index.json");
        var indexHash = HashFile(indexPath);
        var manifest = Directory.Exists(source)
            ? CaptureSourceManifest(data, source, cancellationToken)
            : ReadPreparedSourceManifest(data, source, indexHash);
        if (!string.Equals(manifest.IndexSha256, indexHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("视频索引在来源指纹建立后发生变化，请重新准备来源指纹。");

        var sizes = manifest.Entries.Select(entry => entry.Length).ToHashSet();
        var targetFiles = EnumerateVideoFiles(target, cancellationToken)
            .Select(path => (Path: path, Length: new FileInfo(path).Length))
            .Where(file => sizes.Contains(file.Length))
            .ToList();
        var targetHashes = new Dictionary<string, string>(Paths);
        foreach (var (path, _) in targetFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            targetHashes.Add(path, HashFile(path));
        }
        var byFingerprint = targetFiles.GroupBy(file => (file.Length, Sha256: targetHashes[file.Path]),
                new FingerprintComparer())
            .ToDictionary(group => group.Key,
                group => group.Select(file => file.Path).OrderBy(path => path, Paths).ToArray(),
                new FingerprintComparer());
        var rows = new List<PreviewRow>(manifest.Entries.Count);
        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byFingerprint.TryGetValue((entry.Length, entry.Sha256), out var matches);
            matches ??= [];
            var sameRelative = Path.GetFullPath(Path.Combine(target, entry.RelativePath));
            if (!IsWithin(target, sameRelative)) throw new InvalidDataException("来源清单包含越界相对路径。");
            if (!File.Exists(sameRelative)) sameRelative = null;
            var kind = matches.Length switch
            {
                1 => MatchKind.Exact,
                > 1 => MatchKind.Ambiguous,
                _ when sameRelative is not null => MatchKind.DifferentContent,
                _ => MatchKind.Missing
            };
            rows.Add(new PreviewRow
            {
                Source = entry, Kind = kind,
                ProposedTargetPath = kind == MatchKind.Exact ? matches[0] : null,
                ExactContentPaths = matches,
                SameRelativePath = sameRelative
            });
        }
        return new Preview
        {
            DataDir = data, SourceRoot = source, TargetRoot = target,
            LiveStateSha256 = LibraryBackupService.RelocationLiveDigest(data),
            IndexSha256 = indexHash,
            SourceManifestSha256 = ManifestDigest(manifest),
            Rows = rows
        };
    }

    // Confirmation creates a verified replacement package and schedules the
    // existing crash-recoverable, single-instance restore on next startup.
    // It never mutates the active config/index/queue in the current process.
    internal static Confirmation StageConfirmedRelocation(Preview preview,
        IReadOnlyCollection<Decision> decisions, CancellationToken cancellationToken = default)
    {
        if (preview.Rows.Count == 0) throw new InvalidOperationException("没有可迁移的视频。");
        if (LibraryBackupService.RelocationLiveDigest(preview.DataDir) != preview.LiveStateSha256
            || !HashFile(Path.Combine(preview.DataDir, "library-index.json"))
                .Equals(preview.IndexSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("预览后视频库资料发生变化，请重新预览。");
        var source = Directory.Exists(preview.SourceRoot)
            ? CaptureSourceManifest(preview.DataDir, preview.SourceRoot, cancellationToken)
            : ReadPreparedSourceManifest(preview.DataDir, preview.SourceRoot, preview.IndexSha256);
        if (ManifestDigest(source) != preview.SourceManifestSha256)
            throw new InvalidOperationException("来源视频在预览后发生变化，请重新预览。");
        var selected = decisions.ToDictionary(decision => decision.OldVideoId,
            StringComparer.OrdinalIgnoreCase);
        if (selected.Count != decisions.Count || selected.Keys.Except(
                preview.Rows.Select(row => row.Source.OldVideoId), StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("人工决定包含重复或不属于本次预览的视频。");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathMap = new Dictionary<string, string>(Paths);
        var targetPaths = new HashSet<string>(Paths);
        var evidence = new List<TargetEvidence>(preview.Rows.Count);
        foreach (var row in preview.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            selected.TryGetValue(row.Source.OldVideoId, out var decision);
            if (row.Kind != MatchKind.Exact && decision is null)
                throw new InvalidOperationException("有模糊、缺失或内容不符的视频尚未人工决定。" + row.Source.RelativePath);
            var path = Path.GetFullPath(decision?.TargetPath ?? row.ProposedTargetPath!);
            if (!IsWithin(preview.TargetRoot, path) || !File.Exists(path)
                || !VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("目标视频不在新目录中：" + path);
            RejectLinksOnPath(path);
            var hash = HashFile(path);
            var exact = new FileInfo(path).Length == row.Source.Length
                && hash.Equals(row.Source.Sha256, StringComparison.OrdinalIgnoreCase);
            if (!exact && decision?.AcceptDifferentContent != true)
                throw new InvalidOperationException("目标视频内容与原文件不同，需要明确人工接受：" + path);
            if (!targetPaths.Add(path)) throw new InvalidOperationException("多个原视频指向同一个目标文件：" + path);
            evidence.Add(new TargetEvidence { Path = path, Length = new FileInfo(path).Length, Sha256 = hash });
            var newId = IdForPath(path);
            if (!map.TryAdd(row.Source.OldVideoId, newId))
                throw new InvalidDataException("来源视频编号重复。");
            pathMap.Add(row.Source.OldPath, path);
        }
        var package = LibraryBackupService.CreateRelocationPackage(preview.DataDir,
            preview.SourceRoot, preview.TargetRoot, map, pathMap);
        var guardPath = Path.Combine(preview.DataDir, PendingGuardName);
        var temporary = guardPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new PendingGuard
                { PackagePath = package, Targets = evidence }, JsonOptions));
            File.Move(temporary, guardPath, true);
            // Staging rechecks the live snapshot and lets the existing restore
            // engine create an independent rollback package before any replacement.
            LibraryBackupService.StageRestore(preview.DataDir, package);
            return new Confirmation(package, map.Count, selected.Count,
                "迁移已排定。关闭所有窗口并重新启动映格后，资料与队列会一同切换到新目录。");
        }
        catch
        {
            try { File.Delete(guardPath); } catch { }
            throw;
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    // Call before the ordinary restore at startup. A verified replacement
    // package alone cannot prove the external video files stayed unchanged
    // after confirmation; this guard closes that gap. Normal restores pass
    // straight through. An interrupted restore is allowed to roll back first.
    internal static bool ApplyPendingAtStartup(string dataDir, out string error,
        bool requireSingleInstance = true)
    {
        var guardPath = Path.Combine(dataDir, PendingGuardName);
        if (!File.Exists(guardPath))
            return LibraryBackupService.ApplyPendingAtStartup(dataDir, out error, requireSingleInstance);
        var pendingPath = Path.Combine(dataDir, "restore-pending.json");
        var activePath = Path.Combine(dataDir, "restore-active.json");
        var leases = new List<FileStream>();
        try
        {
            if (File.Exists(pendingPath) && !File.Exists(activePath))
            {
                if ((File.GetAttributes(guardPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("迁移校验文件不能是链接。");
                var guard = JsonSerializer.Deserialize<PendingGuard>(File.ReadAllText(guardPath))
                    ?? throw new InvalidDataException("迁移校验文件损坏。");
                if (guard.Version != 1 || guard.Targets.Count == 0 || guard.Targets.Count > 200_000)
                    throw new InvalidDataException("迁移校验文件格式错误。");
                using var pending = JsonDocument.Parse(File.ReadAllText(pendingPath));
                var stagedPackage = pending.RootElement.GetProperty("BackupPath").GetString() ?? "";
                if (!Path.GetFullPath(stagedPackage).Equals(Path.GetFullPath(guard.PackagePath),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("迁移包与待恢复请求不一致。");
                foreach (var target in guard.Targets)
                {
                    if (!File.Exists(target.Path))
                        throw new InvalidOperationException("新目录中的视频已移动或删除：" + target.Path);
                    RejectLinksOnPath(target.Path);
                    // Keep each read-only handle until the offline restore
                    // completes. FileShare.Read prevents a concurrent writer
                    // or rename from replacing bytes after verification.
                    var lease = new FileStream(target.Path, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                    leases.Add(lease);
                    if (lease.Length != target.Length
                        || !Convert.ToHexString(SHA256.HashData(lease))
                            .Equals(target.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("新目录中的视频在确认后发生变化：" + target.Path);
                }
            }
            var applied = LibraryBackupService.ApplyPendingAtStartup(dataDir, out error, requireSingleInstance);
            // Cleanup is secondary to a completed offline restore. A locked
            // guard file must not turn an already switched library into a
            // misleading "migration failed; original data unchanged" report.
            if (applied)
            {
                try { File.Delete(guardPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return applied;
        }
        catch (Exception ex)
        {
            error = "视频库迁移已暂停，原资料未切换：" + ex.Message;
            return false;
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    static SourceManifest CaptureSourceManifest(string dataDir, string sourceRoot,
        CancellationToken cancellationToken)
    {
        var source = NormalizeRoot(sourceRoot);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("原视频目录不可访问。请先准备来源指纹，再转移视频。");
        RejectLinksOnPath(source);
        var indexPath = Path.Combine(dataDir, "library-index.json");
        var index = ReadIndex(indexPath);
        if (!index.Folders.Any(folder => Paths.Equals(NormalizeRoot(folder), source)))
            throw new InvalidOperationException("原目录不在当前视频索引的根目录中。");
        var entries = index.Entries.Where(entry => IsWithin(source, entry.Path))
            .OrderBy(entry => entry.Path, Paths).ToArray();
        if (entries.Length == 0 || entries.Length > 200_000)
            throw new InvalidOperationException("原目录中没有已索引视频，或视频数量超出安全上限。");
        var output = new SourceManifest { SourceRoot = source, IndexSha256 = HashFile(indexPath) };
        var known = new HashSet<string>(Paths);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(entry.Path);
            if (!known.Add(path) || !File.Exists(path))
                throw new InvalidOperationException("原视频不可访问或索引重复：" + path);
            RejectLinksOnPath(path);
            var info = new FileInfo(path);
            if (info.Length != entry.Size || info.LastWriteTime.Ticks != entry.ModifiedTicks)
                throw new InvalidOperationException("原视频与索引不一致，请先刷新索引：" + path);
            output.Entries.Add(new SourceEntry
            {
                OldPath = path, OldVideoId = IdForPath(path),
                RelativePath = Path.GetRelativePath(source, path),
                Length = info.Length, ModifiedTicks = info.LastWriteTime.Ticks,
                Sha256 = HashFile(path)
            });
        }
        return output;
    }

    static SourceManifest ReadPreparedSourceManifest(string dataDir, string sourceRoot, string indexHash)
    {
        var path = Path.Combine(dataDir, ManifestName);
        if (!File.Exists(path)) throw new InvalidOperationException(
            "原目录已不可访问，也没有预先准备的来源指纹。请在旧盘仍可访问时准备迁移。");
        var manifest = JsonSerializer.Deserialize<SourceManifest>(File.ReadAllText(path))
            ?? throw new InvalidDataException("来源指纹文件损坏。");
        if (manifest.Version != 1 || manifest.Entries.Count is < 1 or > 200_000
            || !Paths.Equals(manifest.SourceRoot, sourceRoot)
            || !manifest.IndexSha256.Equals(indexHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("来源指纹与当前索引或目录不符，请重新准备迁移。");
        var indexed = ReadIndex(Path.Combine(dataDir, "library-index.json")).Entries
            .Where(entry => IsWithin(sourceRoot, entry.Path))
            .ToDictionary(entry => Path.GetFullPath(entry.Path), Paths);
        if (indexed.Count != manifest.Entries.Count) throw new InvalidDataException("来源指纹覆盖的视频数量不一致。");
        foreach (var entry in manifest.Entries)
        {
            if (!indexed.TryGetValue(entry.OldPath, out var original)
                || original.Size != entry.Length || original.ModifiedTicks != entry.ModifiedTicks
                || !Paths.Equals(IdForPath(entry.OldPath), entry.OldVideoId)
                || !IsWithin(sourceRoot, entry.OldPath)
                || !Path.GetRelativePath(sourceRoot, entry.OldPath).Equals(entry.RelativePath, StringComparison.OrdinalIgnoreCase)
                || entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("来源指纹与视频索引不匹配。");
        }
        return manifest;
    }

    static LibraryIndex ReadIndex(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到视频索引。", path);
        var index = JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(path))
            ?? throw new InvalidDataException("视频索引损坏。");
        if (index.Folders is null || index.Entries is null || index.Entries.Any(entry =>
                entry is null || string.IsNullOrWhiteSpace(entry.Path) || !Path.IsPathFullyQualified(entry.Path)))
            throw new InvalidDataException("视频索引包含无效路径。");
        return index;
    }

    static IEnumerable<string> EnumerateVideoFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            RejectLinksOnPath(folder);
            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                pending.Push(child);
            }
            foreach (var path in Directory.EnumerateFiles(folder))
                if (VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                    && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    yield return Path.GetFullPath(path);
        }
    }

    static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("视频目录不能为空。", nameof(path));
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full)!;
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? root : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    static bool IsWithin(string root, string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    static void RejectLinksOnPath(string path)
    {
        for (var current = Path.GetFullPath(path); Directory.Exists(current) || File.Exists(current);
             current = Path.GetDirectoryName(current) ?? "")
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("迁移目录或文件包含链接，已停止以避免读取错误位置：" + current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    static string ManifestDigest(SourceManifest manifest) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            manifest.Version, manifest.SourceRoot, manifest.IndexSha256, manifest.Entries
        }))));

    sealed class FingerprintComparer : IEqualityComparer<(long Length, string Sha256)>
    {
        public bool Equals((long Length, string Sha256) x, (long Length, string Sha256) y) =>
            x.Length == y.Length && x.Sha256.Equals(y.Sha256, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((long Length, string Sha256) value) =>
            HashCode.Combine(value.Length, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Sha256));
    }
}

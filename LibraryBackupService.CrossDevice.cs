using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace YinggeVideoLibrary;

internal static partial class LibraryBackupService
{
    static string CurrentMachineId()
    {
        // A data-directory path alone can be identical on two PCs. Bind the
        // ordinary same-library fast path to this Windows installation.
        string? installation;
        try { installation = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
            "MachineGuid", null) as string; }
        catch { installation = null; }
        var identity = string.IsNullOrWhiteSpace(installation)
            ? Environment.MachineName + "|" + Environment.UserDomainName
            : installation;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.Trim().ToUpperInvariant())));
    }

    internal sealed record CrossDeviceMatch(string OldVideoId, string RelativePath, string Status,
        string? TargetPath, IReadOnlyList<string> ExactCandidates);

    sealed class CrossDeviceMap
    {
        public Dictionary<string, string> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<CrossDeviceMatch> Rows { get; } = [];
        public bool Complete => Rows.All(row => row.Status is "唯一内容匹配" or "人工选择的相同内容");
    }

    // Full-file hashes are deliberately opt-in. Ordinary and scheduled backups
    // never read all videos; the explicit transfer action does so once.
    internal static string CreateCrossDevicePackage(string dataDir, CancellationToken cancellationToken = default,
        Action<int, int>? progress = null)
    {
        var package = Create(dataDir);
        try
        {
            var manifestPath = Path.Combine(package, ManifestName);
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))
                ?? throw new InvalidDataException("跨设备包清单无法读取。");
            var index = ReadCrossDeviceIndex(Path.Combine(package, "library-index.json"));
            var roots = index.Folders.Select(Path.GetFullPath)
                .OrderByDescending(path => path.Length).ToArray();
            var fingerprints = new List<VideoFingerprint>(index.Entries.Count);
            progress?.Invoke(0, index.Entries.Count);
            foreach (var entry in index.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(entry.Path);
                var root = roots.FirstOrDefault(folder => PathWithin(folder, path))
                    ?? throw new InvalidDataException("视频索引路径不属于已加入的文件夹：" + path);
                RejectReparseAncestors(path);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != entry.Size || info.LastWriteTime.Ticks != entry.ModifiedTicks)
                    throw new InvalidOperationException("视频在索引后已变化，请先刷新资料库：" + path);
                var hash = HashFileWithCancellation(path, cancellationToken);
                info.Refresh();
                if (info.Length != entry.Size || info.LastWriteTime.Ticks != entry.ModifiedTicks)
                    throw new InvalidOperationException("计算指纹期间视频发生变化，请重试：" + path);
                fingerprints.Add(new VideoFingerprint
                {
                    OldVideoId = LibraryRelocationService.IdForPath(path), OldPath = path,
                    RelativePath = Path.GetRelativePath(root, path), Length = entry.Size, Sha256 = hash
                });
                if (fingerprints.Count % 20 == 0 || fingerprints.Count == index.Entries.Count)
                    progress?.Invoke(fingerprints.Count, index.Entries.Count);
            }
            // The sealed package is the source snapshot. Background polling may
            // update live queue/metadata while hours of video hashing run; that
            // does not invalidate the already copied package. Each source video
            // is checked against the sealed index before and after hashing.
            manifest.VideoFingerprints = fingerprints;
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
            var check = Verify(package);
            if (!check.IsValid) throw new InvalidDataException("跨设备包校验失败：" + check.Message);
            return package;
        }
        catch
        {
            try { Directory.Delete(package, true); } catch { }
            throw;
        }
    }

    static string HashFileWithCancellation(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            digest.AppendData(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    static bool VerifyCrossDeviceFingerprintManifest(string package, Manifest manifest)
    {
        if (manifest.VideoFingerprints is null) return true;
        if (manifest.Purpose != "user" || manifest.SourceLibraryId.Length != 16
            || manifest.SourceMachineId.Length != 64 || !manifest.SourceMachineId.All(Uri.IsHexDigit)
            || !manifest.SourceLibraryId.All(Uri.IsHexDigit)) return false;
        var index = ReadCrossDeviceIndex(Path.Combine(package, "library-index.json"));
        if (manifest.VideoFingerprints.Count != index.Entries.Count) return false;
        var indexed = index.Entries.ToDictionary(entry => Path.GetFullPath(entry.Path),
            StringComparer.OrdinalIgnoreCase);
        if (indexed.Count != index.Entries.Count) return false;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.VideoFingerprints)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.OldPath)
                || !Path.IsPathFullyQualified(item.OldPath)
                || !indexed.TryGetValue(Path.GetFullPath(item.OldPath), out var entry)
                || item.Length != entry.Size || item.Length < 0
                || item.Sha256.Length != 64 || !item.Sha256.All(Uri.IsHexDigit)
                || !item.OldVideoId.Equals(LibraryRelocationService.IdForPath(item.OldPath),
                    StringComparison.OrdinalIgnoreCase) || !ids.Add(item.OldVideoId)
                || string.IsNullOrWhiteSpace(item.RelativePath)
                || Path.IsPathFullyQualified(item.RelativePath)
                || item.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => part is "." or "..")) return false;
        }
        return true;
    }

    static LibraryIndex ReadCrossDeviceIndex(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException("跨设备包缺少视频索引。");
        var index = JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(path))
            ?? throw new InvalidDataException("视频索引无法读取。");
        if (index.Folders is null || index.Entries is null || index.Entries.Count > 200_000
            || index.Entries.Any(entry => entry is null || !Path.IsPathFullyQualified(entry.Path)
                || entry.Size < 0)) throw new InvalidDataException("视频索引包含无效路径或尺寸。");
        return index;
    }

    static bool PathWithin(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    static bool SourceIndexPathsMatchDestination(string source, string destination, Manifest manifest)
    {
        var sourcePath = Path.Combine(source, "library-index.json");
        var destinationPath = Path.Combine(destination, "library-index.json");
        if (!File.Exists(sourcePath)) return true; // Older empty-library packages remain usable.
        var sourceIndex = ReadCrossDeviceIndex(sourcePath);
        if (sourceIndex.Entries.Count == 0) return true;
        if (!File.Exists(destinationPath)) return false;
        var targetPaths = ReadCrossDeviceIndex(destinationPath).Entries
            .ToDictionary(entry => Path.GetFullPath(entry.Path), StringComparer.OrdinalIgnoreCase);
        var fingerprints = manifest.VideoFingerprints?.ToDictionary(item =>
            Path.GetFullPath(item.OldPath), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in sourceIndex.Entries)
        {
            var path = Path.GetFullPath(entry.Path);
            if (!targetPaths.TryGetValue(path, out var target)
                || target.Size != entry.Size || target.ModifiedTicks != entry.ModifiedTicks)
                return false;
            RejectReparseAncestors(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != entry.Size || info.LastWriteTime.Ticks != entry.ModifiedTicks)
                return false;
            if (fingerprints is not null)
            {
                if (!fingerprints.TryGetValue(path, out var expected)
                    || !HashFile(path).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    return false;
                info.Refresh();
                if (info.Length != entry.Size || info.LastWriteTime.Ticks != entry.ModifiedTicks)
                    return false;
            }
        }
        return true;
    }

    static bool RequiresCrossDeviceMapping(string destination, string source, Manifest manifest)
    {
        if (manifest.SourceLibraryId.Equals(LibraryId(destination), StringComparison.OrdinalIgnoreCase)
            && manifest.SourceMachineId.Equals(CurrentMachineId(), StringComparison.OrdinalIgnoreCase)
            && SourceIndexPathsMatchDestination(source, destination, manifest)) return false;
        if (manifest.VideoFingerprints is null)
            throw new InvalidOperationException("来源包的视频路径与当前库不同，且缺少完整内容指纹。请在来源设备主动生成跨设备迁移包；当前资料未修改。");
        return true;
    }

    static string MappingDigest(IReadOnlyList<CrossDeviceMatch> rows) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rows.OrderBy(row => row.OldVideoId,
            StringComparer.OrdinalIgnoreCase)))));

    static CrossDeviceMap ResolveCrossDeviceMap(string destination, Manifest manifest,
        IReadOnlyDictionary<string, string>? manualChoices)
    {
        var fingerprints = manifest.VideoFingerprints
            ?? throw new InvalidOperationException("这是另一资料库的备份，缺少完整视频指纹。请在来源设备主动生成跨设备迁移包后重试；当前资料未修改。");
        var target = ReadCrossDeviceIndex(Path.Combine(destination, "library-index.json"));
        var sizes = fingerprints.Select(item => item.Length).ToHashSet();
        var sameNames = target.Entries.GroupBy(entry => Path.GetFileName(entry.Path),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Path).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<(long Length, string Sha256), List<string>>();
        foreach (var entry in target.Entries.Where(entry => sizes.Contains(entry.Size)))
        {
            var path = Path.GetFullPath(entry.Path);
            RejectReparseAncestors(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != entry.Size || info.LastWriteTime.Ticks != entry.ModifiedTicks)
                throw new InvalidOperationException("目标视频索引已过期，请先刷新资料库：" + path);
            var key = (entry.Size, HashFile(path).ToUpperInvariant());
            if (!candidates.TryGetValue(key, out var list)) candidates[key] = list = [];
            list.Add(path);
            info.Refresh();
            if (info.Length != entry.Size || info.LastWriteTime.Ticks != entry.ModifiedTicks)
                throw new InvalidOperationException("匹配期间目标视频发生变化，请重新预览：" + path);
        }
        var mapped = new CrossDeviceMap();
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in fingerprints)
        {
            candidates.TryGetValue((item.Length, item.Sha256.ToUpperInvariant()), out var paths);
            paths ??= [];
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            string? chosen = null;
            string status;
            if (manualChoices?.TryGetValue(item.OldVideoId, out var proposed) == true)
            {
                chosen = paths.FirstOrDefault(path => path.Equals(Path.GetFullPath(proposed),
                    StringComparison.OrdinalIgnoreCase));
                if (chosen is null) throw new InvalidDataException("人工选择不是该来源视频的相同内容候选：" + item.RelativePath);
                status = "人工选择的相同内容";
            }
            else if (paths.Count == 1)
            {
                chosen = paths[0];
                status = "唯一内容匹配";
            }
            else if (paths.Count > 1) status = "多个相同内容候选，需人工选择";
            else status = sameNames.ContainsKey(Path.GetFileName(item.OldPath))
                ? "同名但内容不同，需人工核对；当前禁止导入"
                : "未找到相同内容，禁止导入";
            if (chosen is not null)
            {
                if (!usedTargets.Add(chosen))
                    throw new InvalidDataException("多个来源视频指向同一目标视频，无法安全导入：" + chosen);
                mapped.Ids[item.OldVideoId] = LibraryRelocationService.IdForPath(chosen);
                mapped.Paths[item.OldPath] = chosen;
            }
            mapped.Rows.Add(new CrossDeviceMatch(item.OldVideoId, item.RelativePath, status,
                chosen, paths.ToArray()));
        }
        if (manualChoices is not null && manualChoices.Keys.Except(
                fingerprints.Select(item => item.OldVideoId), StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("人工映射包含不属于来源包的视频。");
        return mapped;
    }

    static IReadOnlyList<string> SourceUnindexedCategoryIds(string source, LibraryImportScope scopes,
        Manifest manifest)
    {
        if (!scopes.HasFlag(LibraryImportScope.Categories)) return [];
        var indexed = (manifest.VideoFingerprints ?? []).Select(item => item.OldVideoId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "config.json"))) as JsonObject
            ?? throw new InvalidDataException("导入配置格式无效。");
        var orphanIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in new[] { "categories", "multiCategories" })
        {
            if (config[property] is null) continue;
            if (config[property] is not JsonObject ids)
                throw new InvalidDataException("导入配置字段格式无效：" + property);
            foreach (var id in ids.Select(pair => pair.Key))
                if (!indexed.Contains(id)) orphanIds.Add(id);
        }
        return orphanIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static IReadOnlyList<string> ValidateCrossDeviceCoveredIds(string source, LibraryImportScope scopes,
        CrossDeviceMap mapping, Manifest manifest)
    {
        var covered = mapping.Ids.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orphanCategoryIds = SourceUnindexedCategoryIds(source, scopes, manifest);
        var orphanCategories = orphanCategoryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        void AddObjectKeys(string path)
        {
            if (!File.Exists(path)) return;
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("导入资料必须是对象：" + Path.GetFileName(path));
            foreach (var key in root.Select(pair => pair.Key)) used.Add(key);
        }
        if (scopes.HasFlag(LibraryImportScope.Metadata)) AddObjectKeys(Path.Combine(source, "metadata.json"));
        if (scopes.HasFlag(LibraryImportScope.Ratings)) AddObjectKeys(Path.Combine(source, "personal-ratings.json"));
        if ((scopes & (LibraryImportScope.Categories | LibraryImportScope.Tags | LibraryImportScope.Reviews)) != 0)
        {
            var config = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "config.json"))) as JsonObject
                ?? throw new InvalidDataException("导入配置格式无效。");
            var properties = new List<string>();
            if (scopes.HasFlag(LibraryImportScope.Categories))
                properties.AddRange(["categories", "multiCategories"]);
            if (scopes.HasFlag(LibraryImportScope.Tags)) properties.Add("tags");
            if (scopes.HasFlag(LibraryImportScope.Reviews)) properties.Add("aiReviews");
            foreach (var property in properties)
            {
                if (config[property] is null) continue;
                if (config[property] is not JsonObject ids)
                    throw new InvalidDataException("导入配置字段格式无效：" + property);
                foreach (var key in ids.Select(pair => pair.Key))
                    if (property is not ("categories" or "multiCategories")
                        || !orphanCategories.Contains(key)) used.Add(key);
            }
        }
        if (scopes.HasFlag(LibraryImportScope.Queue))
        {
            var queue = ReadImportQueue(Path.Combine(source, "scrape-queue.json"));
            foreach (var job in queue) used.Add(job.VideoId);
        }
        if (scopes.HasFlag(LibraryImportScope.Reviews))
        {
            var path = Path.Combine(source, "review-history.jsonl");
            if (File.Exists(path)) foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                foreach (var property in new[] { "Before", "After", "MetadataBefore", "TagsBefore", "AutoAmateurBefore" })
                    if (document.RootElement.TryGetProperty(property, out var value)
                        && value.ValueKind == JsonValueKind.Object)
                        foreach (var item in value.EnumerateObject()) used.Add(item.Name);
            }
        }
        if (scopes.HasFlag(LibraryImportScope.SiteLists))
        {
            var state = SiteListImportStore.Load(source);
            foreach (var entry in state.Entries.Where(entry => entry.Status == "已匹配"))
                used.Add(entry.VideoId);
        }
        if (scopes.HasFlag(LibraryImportScope.RoleProfiles))
            foreach (var profile in RoleProfileStore.Load(source).Profiles)
                foreach (var work in profile.Works)
                    if (work.LocalVideoId.Length > 0) used.Add(work.LocalVideoId);
        var missing = used.Where(id => !covered.Contains(id)).Take(5).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException("来源资料包含未获得视频内容指纹的旧编号，已阻止跨设备导入："
                + string.Join("、", missing));
        return orphanCategoryIds;
    }

    sealed class MappedImportSource : IDisposable
    {
        public string Path { get; }
        public MappedImportSource(string source, string dataDir, CrossDeviceMap map,
            LibraryImportScope scopes, IReadOnlyList<string> skippedCategoryIds)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "yingge-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            try
            {
                var files = new List<string>();
                if ((scopes & (LibraryImportScope.Categories | LibraryImportScope.Tags |
                    LibraryImportScope.Reviews)) != 0) files.Add("config.json");
                if (scopes.HasFlag(LibraryImportScope.Metadata)) files.Add("metadata.json");
                if (scopes.HasFlag(LibraryImportScope.Ratings)) files.Add("personal-ratings.json");
                if (scopes.HasFlag(LibraryImportScope.Reviews))
                    files.AddRange(["review-history.jsonl", "review-views.json"]);
                if (scopes.HasFlag(LibraryImportScope.Queue)) files.Add("scrape-queue.json");
                if (scopes.HasFlag(LibraryImportScope.SiteLists)) files.Add("site-list-import.json");
                if (scopes.HasFlag(LibraryImportScope.RoleProfiles)) files.Add(RoleProfileStore.FileName);
                foreach (var name in files)
                    if (File.Exists(System.IO.Path.Combine(source, name)))
                        File.Copy(System.IO.Path.Combine(source, name), System.IO.Path.Combine(Path, name));
                if (skippedCategoryIds.Count > 0)
                    PruneUnindexedCategories(System.IO.Path.Combine(Path, "config.json"), skippedCategoryIds);
                // Local-action history is not part of selective field merges.
                // Strip it before generic ID rewriting of this temp source.
                StripUnimportedSiteListLocalActions(System.IO.Path.Combine(Path, "config.json"));
                var mapper = new RelocationValueMapper(dataDir, "__yingge_no_source_root__",
                    "__yingge_no_target_root__", map.Ids, map.Paths);
                foreach (var name in files.Where(name => name.EndsWith(".json", StringComparison.Ordinal)
                             && name != "site-list-import.json"))
                    RewriteJson(System.IO.Path.Combine(Path, name), mapper);
                if (scopes.HasFlag(LibraryImportScope.SiteLists))
                    RewriteSiteListVideoIds(System.IO.Path.Combine(Path, "site-list-import.json"),
                        map.Ids, dataDir, source);
                if (scopes.HasFlag(LibraryImportScope.Reviews))
                    ReviewTransactionStore.RelocateHistoryFile(
                        System.IO.Path.Combine(Path, "review-history.jsonl"), mapper.Map);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { }
        }
    }

    static void PruneUnindexedCategories(string path, IReadOnlyList<string> orphanIds)
    {
        var config = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("迁移配置格式无效。");
        foreach (var property in new[] { "categories", "multiCategories" })
            if (config[property] is JsonObject values)
                foreach (var id in orphanIds) values.Remove(id);
        File.WriteAllText(path, config.ToJsonString(JsonOptions));
    }

    static void RewriteSiteListVideoIds(string path, IReadOnlyDictionary<string, string> idMap,
        string targetSnapshotDirectory, string? sourceSnapshotDirectory = null)
    {
        if (!File.Exists(path)) return;
        var folder = System.IO.Path.GetDirectoryName(path)!;
        var state = SiteListImportStore.Load(folder);
        var targetContent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in state.Entries.Where(entry => entry.Status == "已匹配"))
        {
            if (!idMap.TryGetValue(entry.VideoId, out var mapped))
                throw new InvalidDataException("站点片单仍引用无法匹配的视频，已停止迁移：" + entry.VideoId);
            var sourceSnapshot = sourceSnapshotDirectory is null ? null
                : TryTargetSiteListSnapshot(sourceSnapshotDirectory, entry.VideoId, requireLiveFile: false);
            var sourceMatch = sourceSnapshotDirectory is null
                || sourceSnapshot is not null && entry.LocalFingerprint.Length == 64
                    && entry.LocalFingerprint.Equals(
                        SiteListImportStore.ComputeLocalFingerprint(sourceSnapshot), StringComparison.Ordinal);
            var snapshot = sourceMatch ? TryTargetSiteListSnapshot(targetSnapshotDirectory, mapped) : null;
            if (snapshot is not null && entry.ContentSha256.Length == 64)
            {
                try
                {
                    if (!targetContent.TryGetValue(mapped, out var digest))
                    {
                        digest = SiteListLocalActionStore.ComputeContentSha256(snapshot);
                        targetContent[mapped] = digest;
                    }
                    if (!digest.Equals(entry.ContentSha256, StringComparison.Ordinal)) snapshot = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    snapshot = null;
                }
            }
            if (snapshot is null)
            {
                // A path-derived ID alone is insufficient for safe playback.
                // Keep the imported list and audit intact, but require the user
                // to match this entry again when the destination snapshot is
                // unavailable (for example a video moved after preview).
                entry.Status = "待核对";
                entry.VideoId = "";
                entry.LocalTitle = "";
                entry.LocalFingerprint = "";
                entry.ContentSha256 = "";
            }
            else
            {
                entry.VideoId = mapped;
                entry.LocalTitle = snapshot.DisplayTitle;
                entry.LocalFingerprint = SiteListImportStore.ComputeLocalFingerprint(snapshot);
            }
        }
        File.WriteAllText(path, JsonSerializer.Serialize(state));
        _ = SiteListImportStore.Load(folder);
    }

    static SiteListImportStore.LocalVideo? TryTargetSiteListSnapshot(string dataDir, string videoId,
        bool requireLiveFile = true)
    {
        try
        {
            var index = ReadCrossDeviceIndex(System.IO.Path.Combine(dataDir, "library-index.json"));
            var entry = index.Entries.SingleOrDefault(item => LibraryRelocationService.IdForPath(item.Path)
                .Equals(videoId, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            if (requireLiveFile)
            {
                RejectReparseAncestors(entry.Path);
                var file = new FileInfo(entry.Path);
                if (!file.Exists || file.Length != entry.Size || file.LastWriteTime.Ticks != entry.ModifiedTicks)
                    return null;
            }
            var configPath = System.IO.Path.Combine(dataDir, "config.json");
            var config = File.Exists(configPath)
                ? JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(configPath)) : null;
            var metadata = MetadataStore.Load(dataDir);
            metadata.TryGetValue(videoId, out var details);
            var title = System.IO.Path.GetFileNameWithoutExtension(entry.Path);
            var displayTitle = config?.DisplayTitleOverrides?.GetValueOrDefault(videoId)
                ?? details?.Title ?? title;
            return new SiteListImportStore.LocalVideo(videoId, title, displayTitle,
                details?.Code ?? "", details?.Title ?? "", details?.OriginalTitle ?? "",
                details?.DetailIdentityVerified == true ? details.SourceUrl ?? "" : "",
                entry.Path, entry.Size, entry.ModifiedTicks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YinggeVideoLibrary;

internal static partial class LibraryBackupService
{
    internal static string RelocationLiveDigest(string dataDir) => SnapshotLiveState(dataDir);

    internal static string CreateRelocationPackage(string dataDir, string sourceRoot, string targetRoot,
        IReadOnlyDictionary<string, string> idMap, IReadOnlyDictionary<string, string> pathMap)
    {
        if (File.Exists(PendingPath(dataDir)) || File.Exists(ActivePath(dataDir))
            || File.Exists(PendingImportPath(dataDir)) || File.Exists(ActiveImportPath(dataDir))
            || File.Exists(PendingOfflineBackupPath(dataDir)) || ReviewTransactionStore.HasPending(dataDir)
            || DuplicateMetadataTransactionStore.HasPending(dataDir))
            throw new InvalidOperationException("已有恢复、导入、备份或审核事务待处理，请先完成它再迁移视频库。");
        var package = Create(dataDir);
        try
        {
            var path = Path.Combine(package, ManifestName);
            var original = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))
                ?? throw new InvalidDataException("迁移快照清单无效。");
            if (original.Purpose != "user") throw new InvalidDataException("迁移快照类型无效。");

            var mapper = new RelocationValueMapper(dataDir, sourceRoot, targetRoot, idMap, pathMap);
            foreach (var name in DataFiles.Where(name => name.EndsWith(".json", StringComparison.Ordinal)
                                                  && name is not ("ai-budget.json" or "javinfo-usage.json"
                                                      or "duplicate-metadata-history.json"
                                                      or "site-list-import.json")))
                RewriteJson(Path.Combine(package, name), mapper);
            RewriteJson(Path.Combine(package, "scrape-queue.json"), mapper);
            RewriteSiteListVideoIds(Path.Combine(package, "site-list-import.json"), idMap, package, dataDir);
            RewriteSiteListLocalActionFingerprints(Path.Combine(package, "config.json"), dataDir, package, idMap);
            ReviewTransactionStore.RelocateHistoryFile(Path.Combine(package, "review-history.jsonl"), mapper.Map);
            DuplicateMetadataTransactionStore.RelocateHistoryFile(
                Path.Combine(package, "duplicate-metadata-history.json"), mapper.Map);
            foreach (var file in Directory.Exists(Path.Combine(package, "title-candidates"))
                         ? Directory.EnumerateFiles(Path.Combine(package, "title-candidates"), "*.json").ToArray()
                         : [])
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var oldId = idMap.Keys.FirstOrDefault(id => Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(id.Trim().ToUpperInvariant())))
                    .Equals(name, StringComparison.OrdinalIgnoreCase));
                if (oldId is null) continue;
                RewriteJson(file, mapper);
                var newName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    idMap[oldId].Trim().ToUpperInvariant()))) + ".json";
                var destination = Path.Combine(Path.GetDirectoryName(file)!, newName);
                if (File.Exists(destination)) throw new InvalidDataException("标题候选资料发生目标编号冲突。");
                File.Move(file, destination);
            }
            RenameManagedImages(package, idMap);
            AiJobStore.QuarantineRelocatedRequests(package);

            // Manifest entry hashes cover the rewritten files. The package is
            // hidden from ordinary LatestBackup; it is only used by the
            // confirmation's existing offline restore/rollback engine.
            var refreshed = new Manifest
            {
                Version = FormatVersion, Purpose = "user", CreatedUtc = DateTimeOffset.UtcNow,
                SourceLibraryId = LibraryId(dataDir), SourceMachineId = CurrentMachineId()
            };
            foreach (var relative in EnumerateOrdinaryBackupFiles(package)
                         .Where(name => name != ManifestName)
                         .OrderBy(name => name, StringComparer.Ordinal))
                AddEntry(package, relative, refreshed);
            File.WriteAllText(path, JsonSerializer.Serialize(refreshed, JsonOptions));
            var checkedPackage = Verify(package);
            if (!checkedPackage.IsValid)
                throw new InvalidDataException("迁移快照校验失败：" + checkedPackage.Message);
            var hidden = Path.Combine(Path.GetDirectoryName(package)!,
                ".relocation-" + Guid.NewGuid().ToString("N"));
            Directory.Move(package, hidden);
            return hidden;
        }
        catch
        {
            try { if (Directory.Exists(package)) Directory.Delete(package, true); } catch { }
            throw;
        }
    }

    static void RewriteJson(string path, RelocationValueMapper mapper)
    {
        if (!File.Exists(path)) return;
        var node = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidDataException("迁移资料文件没有有效内容：" + Path.GetFileName(path));
        var rewritten = mapper.Map(node);
        File.WriteAllText(path, rewritten.ToJsonString(JsonOptions));
    }

    static void RenameManagedImages(string package, IReadOnlyDictionary<string, string> idMap)
    {
        foreach (var (folder, suffix) in new[] { ("posters", ""), ("thumbs", "-custom") })
        {
            var root = Path.Combine(package, folder);
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root).ToArray())
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                var oldId = suffix.Length == 0 ? stem
                    : stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? stem[..^suffix.Length] : "";
                if (!idMap.TryGetValue(oldId, out var newId)) continue;
                var destination = Path.Combine(root, newId + suffix + Path.GetExtension(file));
                if (File.Exists(destination)) throw new InvalidDataException("迁移图片文件发生目标编号冲突。");
                File.Move(file, destination);
            }
        }
    }

    internal sealed class RelocationValueMapper
    {
        readonly string dataDir, sourceRoot, targetRoot;
        readonly IReadOnlyDictionary<string, string> ids, paths;
        readonly Dictionary<string, string> mediaPaths = new(StringComparer.OrdinalIgnoreCase);

        internal RelocationValueMapper(string dataDir, string sourceRoot, string targetRoot,
            IReadOnlyDictionary<string, string> idMap, IReadOnlyDictionary<string, string> pathMap)
        {
            this.dataDir = Path.GetFullPath(dataDir);
            this.sourceRoot = sourceRoot;
            this.targetRoot = targetRoot;
            ids = idMap;
            paths = pathMap;
            foreach (var (oldId, newId) in ids)
                foreach (var (folder, suffix) in new[] { ("posters", ""), ("thumbs", "-custom") })
                    foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" })
                        mediaPaths[Path.Combine(this.dataDir, folder, oldId + suffix + extension)] =
                            Path.Combine(this.dataDir, folder, newId + suffix + extension);
        }

        internal string Map(string value)
        {
            if (ids.TryGetValue(value, out var id)) return id;
            if (paths.TryGetValue(value, out var path)) return path;
            if (mediaPaths.TryGetValue(value, out var media)) return media;
            if (value.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase)) return targetRoot;
            var prefix = sourceRoot.EndsWith(Path.DirectorySeparatorChar)
                ? sourceRoot : sourceRoot + Path.DirectorySeparatorChar;
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(targetRoot, value[prefix.Length..]);
            return value;
        }

        internal JsonNode Map(JsonNode node)
        {
            if (node is JsonObject original)
            {
                var mapped = new JsonObject();
                foreach (var (key, value) in original)
                {
                    var newKey = Map(key);
                    if (mapped.ContainsKey(newKey))
                        throw new InvalidDataException("迁移后资料编号与现有资料冲突：" + newKey);
                    mapped[newKey] = value is null ? null : Map(value);
                }
                return mapped;
            }
            if (node is JsonArray array)
            {
                var mapped = new JsonArray();
                foreach (var value in array) mapped.Add(value is null ? null : Map(value));
                return mapped;
            }
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                return JsonValue.Create(Map(text))!;
            return node.DeepClone();
        }
    }
}

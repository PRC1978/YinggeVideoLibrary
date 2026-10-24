using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    sealed record LegacyCacheCandidate(string Directory, LibraryConfig Config, LibraryIndex? Index,
        string? IndexFile, DateTime UpdatedAtUtc);
    sealed record CacheMigrationMarker(long ConfigLength, long IndexLength, string ConfigSha256, string IndexSha256);

    // Use a location outside AppData for the shared cache. Packaged desktop
    // processes virtualize LocalAppData, even when the path string looks
    // identical to an unpackaged process. Run before LoadConfig.
    static string TryMigrateLegacyCaches(string destinationDataDir)
    {
        var ownsConfig = false;
        var ownsIndex = false;
        try
        {
            var targetConfig = Path.Combine(destinationDataDir, "config.json");
            var targetIndex = Path.Combine(destinationDataDir, "library-index.json");
            var marker = Path.Combine(destinationDataDir, "cache-migration-v1.done");
            if (TryReadJson(marker, out CacheMigrationMarker? snapshot) && snapshot is not null
                && FileMatchesMigrationMarker(targetConfig, snapshot.ConfigLength, snapshot.ConfigSha256)
                && (snapshot.IndexLength == 0 ? !File.Exists(targetIndex) || new FileInfo(targetIndex).Length < 128
                    : FileMatchesMigrationMarker(targetIndex, snapshot.IndexLength, snapshot.IndexSha256)))
                return "";
            if (File.Exists(marker))
            {
                var alreadySaved = ReadMigrationDestinationConfig(targetConfig, out _);
                if (alreadySaved.FoldersClearedByUser) return "";
                if (NormalizeRoots(alreadySaved.Folders).Length > 0
                    && ReadMigrationDestinationIndex(targetIndex, NormalizeRoots(alreadySaved.Folders))
                        is { Entries.Count: > 0 })
                { WriteMigrationMarker(marker, targetConfig, targetIndex, true); return ""; }
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var packages = Path.Combine(localAppData, "Packages");
            var sourceDirectories = new List<string> { Path.Combine(localAppData, "LocalVideoLibrary") };
            if (Directory.Exists(packages))
                sourceDirectories.AddRange(Directory.EnumerateDirectories(packages, "OpenAI.Codex_*", SearchOption.TopDirectoryOnly)
                    .Select(package => Path.Combine(package, "LocalCache", "Local", "LocalVideoLibrary")));
            var destinationFullPath = Path.GetFullPath(destinationDataDir).TrimEnd(Path.DirectorySeparatorChar);
            var sources = sourceDirectories
                .Where(path => !Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(destinationFullPath, StringComparison.OrdinalIgnoreCase))
                .Where(Directory.Exists)
                .Select(ReadLegacyCacheCandidate)
                .Where(candidate => candidate is not null)
                .OrderByDescending(candidate => candidate!.Index is { Entries.Count: > 0 })
                .ThenByDescending(candidate => candidate!.UpdatedAtUtc)
                .ThenByDescending(candidate => candidate!.Index?.Entries.Count ?? 0)
                .ToArray();
            if (sources.Length == 0) return "";

            try { ownsConfig = ConfigWriteMutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { ownsConfig = true; }
            if (!ownsConfig) return "缓存迁移稍后重试：配置正在使用";
            try { ownsIndex = IndexWriteMutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { ownsIndex = true; }
            if (!ownsIndex) return "缓存迁移稍后重试：索引正在使用";

            // Re-read under the same mutexes as normal saves. A user who
            // deliberately removed every folder must never be resurrected by
            // a private cache left over from an earlier development session.
            var current = ReadMigrationDestinationConfig(targetConfig, out var hasValidDestinationConfig);
            if (current.FoldersClearedByUser) return "";
            var currentRoots = NormalizeRoots(current.Folders);
            var currentIndex = ReadMigrationDestinationIndex(targetIndex, currentRoots);
            var alreadyMigrated = currentRoots.Length > 0 && currentIndex is { Entries.Count: > 0 };
            if (alreadyMigrated && File.Exists(marker)) return "";

            foreach (var candidate in sources)
            {
                if (candidate is null) continue;
                var sourceRoots = NormalizeRoots(candidate.Config.Folders);
                if (sourceRoots.Length == 0) continue;
                if (currentRoots.Length > 0 && !currentRoots.SequenceEqual(sourceRoots, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (currentIndex is { Entries.Count: > 0 }
                    && !NormalizeRoots(currentIndex.Folders).SequenceEqual(sourceRoots, StringComparer.OrdinalIgnoreCase))
                    continue;

                var migratedConfig = false;
                var migratedIndex = false;
                if (currentRoots.Length == 0)
                {
                    // A valid destination config can contain intentional empty
                    // favorites/tags/categories. Never resurrect them from a
                    // legacy copy. Only a truly absent/invalid config imports
                    // the old preferences wholesale.
                    var merged = hasValidDestinationConfig
                        ? CloneLibraryConfig(current) : CloneLibraryConfig(candidate.Config);
                    merged.Folders = sourceRoots.ToList();
                    merged.FoldersClearedByUser = false;
                    RestoreCustomPosters(candidate.Directory, destinationDataDir, merged);
                    foreach (var review in merged.AiReviews.Values)
                        review.CandidatePoster = TranslateLegacyMediaPath(candidate.Directory,
                            destinationDataDir, review.CandidatePoster) ?? review.CandidatePoster;
                    WriteMigrationJson(targetConfig, merged,
                        path => TryReadJson(path, out LibraryConfig? copy) && copy?.Folders is { Count: > 0 });
                    currentRoots = sourceRoots;
                    migratedConfig = true;
                }

                if (currentIndex is not { Entries.Count: > 0 } && candidate.Index is { Entries.Count: > 0 }
                    && candidate.IndexFile is not null
                    && NormalizeRoots(candidate.Index.Folders).SequenceEqual(currentRoots, StringComparer.OrdinalIgnoreCase))
                {
                    CopyMigrationJson(candidate.IndexFile, targetIndex,
                        path => TryReadLibraryIndex(path, out LibraryIndex? copy)
                            && copy is { Entries.Count: > 0 }
                            && NormalizeRoots(copy.Folders).SequenceEqual(currentRoots, StringComparer.OrdinalIgnoreCase));
                    migratedIndex = true;
                }

                var auxiliary = MigrateAuxiliaryCache(candidate.Directory, destinationDataDir);
                Directory.CreateDirectory(destinationDataDir);
                if (currentRoots.Length > 0)
                    WriteMigrationMarker(marker, targetConfig, targetIndex,
                        migratedIndex || currentIndex is { Entries.Count: > 0 });
                if (migratedConfig || migratedIndex || auxiliary > 0)
                {
                    var otherRoots = sources.Count(source => source is not null
                        && !NormalizeRoots(source.Config.Folders).SequenceEqual(currentRoots, StringComparer.OrdinalIgnoreCase));
                    var conflictNote = otherRoots > 0 ? $"；另有 {otherRoots} 份不同文件夹的旧缓存未覆盖" : "";
                    return (migratedIndex
                        ? $"已恢复 {currentRoots.Length} 个视频文件夹和 {candidate.Index!.Entries.Count} 部视频的索引"
                        : migratedConfig ? $"已恢复 {currentRoots.Length} 个视频文件夹，正在重新扫描"
                        : $"已恢复 {auxiliary} 项本地媒体缓存") + conflictNote;
                }
                return "";
            }
            return "";
        }
        catch (Exception ex)
        {
            // Normal startup can still recover from .bak/.last-good, or scan
            // configured folders; migration is never allowed to block it.
            return "缓存迁移未完成：" + ex.Message;
        }
        finally
        {
            if (ownsIndex) try { IndexWriteMutex.ReleaseMutex(); } catch { }
            if (ownsConfig) try { ConfigWriteMutex.ReleaseMutex(); } catch { }
        }
    }

    static bool FileMatchesMigrationMarker(string path, long expectedLength, string expectedHash)
    {
        try
        {
            if (expectedLength <= 0 || new FileInfo(path).Length != expectedLength) return false;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static void WriteMigrationMarker(string path, string configPath, string indexPath, bool hasUsableIndex)
    {
        try
        {
            using var configStream = File.OpenRead(configPath);
            var configHash = Convert.ToHexString(SHA256.HashData(configStream));
            long indexLength = 0;
            var indexHash = "";
            if (hasUsableIndex)
            {
                using var indexStream = File.OpenRead(indexPath);
                indexLength = indexStream.Length;
                indexHash = Convert.ToHexString(SHA256.HashData(indexStream));
            }
            var snapshot = new CacheMigrationMarker(configStream.Length, indexLength, configHash, indexHash);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot)); File.Move(temporary, path, true); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
        catch { /* The marker only avoids repeated migration checks. */ }
    }

    static LibraryConfig ReadMigrationDestinationConfig(string path, out bool hasValidConfig)
    {
        hasValidConfig = false;
        LibraryConfig? empty = null;
        foreach (var candidate in new[] { path, path + ".last-good", path + ".bak" })
        {
            if (!TryReadJson(candidate, out LibraryConfig? loaded) || loaded is null) continue;
            hasValidConfig = true;
            NormalizeMigrationConfig(loaded);
            if (loaded.FoldersClearedByUser || loaded.Folders.Count > 0) return loaded;
            empty ??= loaded;
        }
        return empty ?? new LibraryConfig();
    }

    static LibraryIndex? ReadMigrationDestinationIndex(string path, string[] configuredRoots)
    {
        LibraryIndex? empty = null;
        foreach (var candidate in new[] { path, path + ".last-good", path + ".bak" })
        {
            if (!TryReadLibraryIndex(candidate, out LibraryIndex? loaded) || loaded is null) continue;
            if (loaded.Entries.Count > 0 && (configuredRoots.Length == 0 ||
                NormalizeRoots(loaded.Folders).SequenceEqual(configuredRoots, StringComparer.OrdinalIgnoreCase)))
                return loaded;
            empty ??= loaded;
        }
        return empty;
    }

    static LegacyCacheCandidate? ReadLegacyCacheCandidate(string directory)
    {
        try
        {
            // A marked empty config is an intentional deletion, even if an
            // old index still contains videos.
            var primaryConfig = Path.Combine(directory, "config.json");
            if (TryReadJson(primaryConfig, out LibraryConfig? explicitConfig)
                && explicitConfig?.FoldersClearedByUser == true) return null;

            LibraryConfig? config = null;
            string? configFile = null;
            foreach (var path in new[] { primaryConfig, primaryConfig + ".last-good", primaryConfig + ".bak" })
                if (TryReadJson(path, out LibraryConfig? loaded) && loaded?.Folders is { Count: > 0 })
                { config = loaded; configFile = path; break; }

            LibraryIndex? index = null;
            string? indexFile = null;
            var primaryIndex = Path.Combine(directory, "library-index.json");
            foreach (var path in new[] { primaryIndex, primaryIndex + ".last-good", primaryIndex + ".bak" })
                if (TryReadLibraryIndex(path, out LibraryIndex? loaded)
                    && loaded is { Entries.Count: > 0 }
                    && ItemsFromIndex(loaded, out _).Count > 0)
                { index = loaded; indexFile = path; break; }

            if (config is null && index is { Folders.Length: > 0 })
                config = new LibraryConfig { Folders = index.Folders.ToList() };
            if (config?.Folders is not { Count: > 0 }) return null;
            NormalizeMigrationConfig(config);

            // Prefer a coherent pair. A source index from an older folder set
            // must not be written alongside the current source config.
            if (index is not null && !NormalizeRoots(index.Folders).SequenceEqual(
                    NormalizeRoots(config.Folders), StringComparer.OrdinalIgnoreCase))
            { index = null; indexFile = null; }
            var updated = new[] { configFile, indexFile }
                .Where(path => path is not null)
                .Select(path => path!)
                .Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            return new LegacyCacheCandidate(directory, config, index, indexFile, updated);
        }
        catch { return null; }
    }

    static void NormalizeMigrationConfig(LibraryConfig config)
    {
        config.Folders ??= [];
        config.Favorites ??= [];
        config.Player ??= "";
        config.Categories ??= [];
        config.MultiCategories ??= [];
        config.Tags ??= [];
        config.CategoryNames ??= [];
        config.WatchStates ??= [];
        config.CategoryRenames ??= [];
        config.CustomPosters ??= [];
        config.TmdbToken ??= "";
        config.AiEndpoint ??= "";
        config.AiModel ??= "";
        config.AiKey ??= "";
        config.AiReviews ??= [];
        config.SearchHistory ??= [];
    }

    static void RestoreCustomPosters(string oldDataDir, string newDataDir, LibraryConfig config)
    {
        foreach (var (videoId, oldPath) in config.CustomPosters.ToArray())
        {
            var translated = TranslateLegacyMediaPath(oldDataDir, newDataDir, oldPath);
            if (translated is not null) config.CustomPosters[videoId] = translated;
        }
    }

    static string? TranslateLegacyMediaPath(string oldDataDir, string newDataDir, string? oldPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || !Path.IsPathFullyQualified(oldPath)) return null;
        var logicalLegacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalVideoLibrary");
        foreach (var folder in new[] { "thumbs", "posters" })
            foreach (var legacyRoot in new[] { Path.Combine(oldDataDir, folder), Path.Combine(logicalLegacy, folder) })
            {
                string relative;
                try { relative = Path.GetRelativePath(legacyRoot, oldPath); }
                catch { continue; }
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                    || relative.Contains(Path.DirectorySeparatorChar) || relative.Contains(Path.AltDirectorySeparatorChar)) continue;
                var oldPhysical = Path.Combine(oldDataDir, folder, relative);
                if (!File.Exists(oldPhysical)) continue;
                var target = Path.Combine(newDataDir, folder, relative);
                CopyMissingFile(oldPhysical, target);
                if (File.Exists(target)) return target;
            }
        return null;
    }

    static int MigrateAuxiliaryCache(string oldDataDir, string newDataDir)
    {
        var count = CopyMissingMediaDirectory(Path.Combine(oldDataDir, "thumbs"), Path.Combine(newDataDir, "thumbs"));
        count += CopyMissingMediaDirectory(Path.Combine(oldDataDir, "posters"), Path.Combine(newDataDir, "posters"));

        using var metadataMutex = new Mutex(false, "Local\\YinggeVideoLibrary.MetadataWrite");
        using var queueMutex = new Mutex(false, "Local\\YinggeVideoLibrary.ScrapeQueueWrite");
        var ownsMetadata = false;
        var ownsQueue = false;
        try
        {
            try { ownsMetadata = metadataMutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { ownsMetadata = true; }
            if (!ownsMetadata) throw new IOException("元数据正在写入");
            try { ownsQueue = queueMutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { ownsQueue = true; }
            if (!ownsQueue) throw new IOException("搜刮队列正在写入");

            var oldMetadata = ReadMigrationMetadata(Path.Combine(oldDataDir, "metadata.json"));
            if (oldMetadata.Count > 0)
            {
                var target = Path.Combine(newDataDir, "metadata.json");
                var merged = ReadMigrationMetadata(target);
                var additions = 0;
                foreach (var (key, value) in oldMetadata)
                {
                    if (merged.ContainsKey(key)) continue;
                    value.Poster = TranslateLegacyMediaPath(oldDataDir, newDataDir, value.Poster) ?? value.Poster;
                    merged[key] = value;
                    additions++;
                }
                if (additions > 0)
                {
                    WriteMigrationJson(target, merged, path => ReadMigrationMetadata(path).Count > 0);
                    count += additions;
                }
            }

            var oldQueue = ReadMigrationQueue(Path.Combine(oldDataDir, "scrape-queue.json"));
            if (oldQueue.Count > 0)
            {
                var target = Path.Combine(newDataDir, "scrape-queue.json");
                var merged = ReadMigrationQueue(target);
                var known = merged.Select(item => item.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var additions = 0;
                foreach (var item in oldQueue)
                    if (known.Add(item.VideoId)) { merged.Add(item); additions++; }
                if (additions > 0)
                {
                    WriteMigrationJson(target, merged, path => ReadMigrationQueue(path).Count > 0);
                    count += additions;
                }
            }

            var oldCursor = Path.Combine(oldDataDir, "poster-warm-cursor.txt");
            var newCursor = Path.Combine(newDataDir, "poster-warm-cursor.txt");
            if (!File.Exists(newCursor) && File.Exists(oldCursor)
                && int.TryParse(File.ReadAllText(oldCursor), out var cursor) && cursor >= 0)
            { CopyMissingFile(oldCursor, newCursor); count++; }
        }
        finally
        {
            if (ownsQueue) try { queueMutex.ReleaseMutex(); } catch { }
            if (ownsMetadata) try { metadataMutex.ReleaseMutex(); } catch { }
        }
        return count;
    }

    static Dictionary<string, MovieMetadata> ReadMigrationMetadata(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
            if (TryReadJson(candidate, out Dictionary<string, MovieMetadata>? loaded) && loaded is { Count: > 0 })
                return loaded.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                    .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, MovieMetadata>(StringComparer.OrdinalIgnoreCase);
    }

    static List<ScrapeQueueItem> ReadMigrationQueue(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
            if (TryReadJson(candidate, out List<ScrapeQueueItem>? loaded) && loaded is { Count: > 0 })
                return loaded.Where(item => item is not null && !string.IsNullOrWhiteSpace(item.VideoId)
                    && !string.IsNullOrWhiteSpace(item.Code))
                    .GroupBy(item => item.VideoId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).ToList();
        return [];
    }

    static int CopyMissingMediaDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) return 0;
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(file).Contains(".tmp.", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetExtension(file).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp")) continue;
            try
            {
                if (new FileInfo(file).Length == 0) continue;
                var target = Path.Combine(destination, Path.GetFileName(file));
                if (File.Exists(target)) continue;
                CopyMissingFile(file, target);
                if (File.Exists(target)) copied++;
            }
            catch { /* A bad regenerated thumbnail is safe to skip. */ }
        }
        return copied;
    }

    static void CopyMissingFile(string source, string destination)
    {
        if (File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".migration-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temporary);
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { /* Another copy finished first. */ }
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    static void WriteMigrationJson<T>(string destination, T data, Func<string, bool> isValid)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".migration-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(data));
            if (!isValid(temporary)) throw new InvalidDataException("迁移后的配置验证失败");
            ReplaceMigrationFile(temporary, destination);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    static void CopyMigrationJson(string source, string destination, Func<string, bool> isValid)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".migration-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temporary);
            if (!isValid(temporary)) throw new InvalidDataException("迁移后的索引验证失败");
            ReplaceMigrationFile(temporary, destination);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    static void ReplaceMigrationFile(string temporary, string destination)
    {
        if (File.Exists(destination))
        {
            var backup = destination + $".pre-migration-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.bak";
            File.Replace(temporary, destination, backup);
        }
        else File.Move(temporary, destination);
    }
}

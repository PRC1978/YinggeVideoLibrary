using System.Collections.Concurrent;
using System.IO;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    readonly ConcurrentDictionary<string, byte> changedPaths = new(StringComparer.OrdinalIgnoreCase);
    volatile bool watcherOverflow;
    readonly SemaphoreSlim incrementalGate = new(1, 1);
    List<VideoItem>? indexedDirectoryLibrary;
    HashSet<string> indexedVideoDirectories = new(StringComparer.OrdinalIgnoreCase);

    internal static bool ShouldQueueDirectoryChange(WatcherChangeTypes change, bool directoryExists) =>
        change != WatcherChangeTypes.Changed || !directoryExists;

    internal static HashSet<string> BuildKnownVideoDirectorySet(IEnumerable<string> videoFolders)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in videoFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var current = folder;
            while (!string.IsNullOrEmpty(current) && directories.Add(current))
            {
                var parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }
        return directories;
    }

    internal static bool IsRelevantIncrementalPath(string path, bool directoryExists, HashSet<string> knownVideoDirectories) =>
        Extensions.Contains(Path.GetExtension(path)) || directoryExists || knownVideoDirectories.Contains(path);

    HashSet<string> KnownVideoDirectories()
    {
        if (ReferenceEquals(indexedDirectoryLibrary, videos)) return indexedVideoDirectories;
        indexedVideoDirectories = BuildKnownVideoDirectorySet(videos.Select(item => item.Folder));
        indexedDirectoryLibrary = videos;
        return indexedVideoDirectories;
    }

    async Task RefreshChangedPathsAsync()
    {
        if (isClosing) return;
        if (!await incrementalGate.WaitAsync(0)) return;
        try
        {
            var observed = changedPaths.Keys.ToArray();
            foreach (var path in observed) changedPaths.TryRemove(path, out _);
            if (watcherOverflow || videos.Count == 0)
            {
                watcherOverflow = false;
                await RefreshAsync();
                return;
            }
            // Covers and subtitles may change frequently, especially while
            // generating posters. They do not change the video index and must
            // never trigger full-library path comparisons on the UI thread.
            var knownDirectories = KnownVideoDirectories();
            var changed = observed.Where(path => IsRelevantIncrementalPath(path, Directory.Exists(path), knownDirectories)).ToArray();
            if (changed.Length > 500)
            {
                await RefreshAsync();
                return;
            }
            if (changed.Length == 0) return;
            var roots = NormalizeRoots(config.Folders);
            var favorites = new HashSet<string>(config.Favorites, StringComparer.OrdinalIgnoreCase);
            var delta = await Task.Run(() =>
            {
                var replacements = new Dictionary<string, VideoItem>(StringComparer.OrdinalIgnoreCase);
                // A copied folder can emit an event for every nested directory.
                // Scan its highest changed ancestor once, not once per child.
                var directories = new List<string>();
                foreach (var directory in changed.Where(Directory.Exists).OrderBy(path => path.Length))
                    if (!directories.Any(parent => IsDescendantPath(parent, directory))) directories.Add(directory);
                var incompleteDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var directory in directories)
                {
                    var scan = Scan([directory], favorites);
                    if (scan.IncompleteRoots.Count > 0) incompleteDirectories.Add(directory);
                    foreach (var item in scan.Items) replacements[item.Path] = item;
                }
                foreach (var path in changed)
                {
                    if (directories.Any(directory => IsDescendantPath(directory, path))) continue;
                    if (!Extensions.Contains(Path.GetExtension(path)) || !File.Exists(path)) continue;
                    try
                    {
                        var info = new FileInfo(path);
                        var id = IdFor(path);
                        replacements[path] = new VideoItem { Id = id, Path = path, Title = Path.GetFileNameWithoutExtension(path), Folder = info.DirectoryName ?? "", Size = info.Length, Modified = info.LastWriteTime, Favorite = favorites.Contains(id) };
                    }
                    catch (IOException) { }
                }
                return (replacements, directories, incompleteDirectories);
            });
            if (isClosing) return;
            var previous = videos.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
            var realChange = changed.Any(path => !File.Exists(path) && !Directory.Exists(path)
                    && (previous.ContainsKey(path) || knownDirectories.Contains(path)))
                || delta.replacements.Any(pair => !previous.TryGetValue(pair.Key, out var old) || old.Size != pair.Value.Size || old.Modified != pair.Value.Modified)
                || delta.directories.Where(directory => !delta.incompleteDirectories.Contains(directory))
                    .Any(directory => previous.Keys.Any(path => IsDescendantPath(directory, path) && !delta.replacements.ContainsKey(path)));
            if (!realChange) return;
            // Keep existing objects when a directory event merely re-enumerated
            // them. This preserves card state and avoids reapplying all metadata.
            var additions = new List<VideoItem>();
            foreach (var pair in delta.replacements.ToArray())
            {
                if (previous.TryGetValue(pair.Key, out var old) && old.Size == pair.Value.Size && old.Modified == pair.Value.Modified)
                    delta.replacements[pair.Key] = old;
                else additions.Add(pair.Value);
            }
            foreach (var path in changed)
            {
                if (Directory.Exists(path))
                {
                    if (!delta.directories.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                    if (delta.incompleteDirectories.Contains(path)) continue;
                    foreach (var old in previous.Keys.Where(key => IsDescendantPath(path, key)).ToArray()) previous.Remove(old);
                }
                else if (!File.Exists(path))
                {
                    foreach (var old in previous.Keys.Where(key => key.Equals(path, StringComparison.OrdinalIgnoreCase) || IsDescendantPath(path, key)).ToArray()) previous.Remove(old);
                }
                else previous.Remove(path);
            }
            foreach (var pair in delta.replacements) previous[pair.Key] = pair.Value;
            ApplySavedFields(additions);
            videos = previous.Values.OrderByDescending(item => item.Modified).ToList();
            categoryDataVersion++;
            ShowView(view);
            TaskStatusText.Text = $"增量更新完成 · {videos.Count} 部视频";
            try { await SaveIndexAsync(videos, roots); }
            catch (Exception ex) { Log("增量索引保存失败：" + ex.Message); }
        }
        catch (Exception ex) { Log("增量更新失败，改为完整扫描：" + ex.Message); await RefreshAsync(); }
        finally { incrementalGate.Release(); if (changedPaths.Count > 0 && !isClosing) QueueFolderRefresh(); }
    }
}

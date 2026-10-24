using System.Collections.Concurrent;
using System.IO;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    readonly ConcurrentDictionary<string, byte> changedPaths = new(StringComparer.OrdinalIgnoreCase);
    volatile bool watcherOverflow;
    readonly SemaphoreSlim incrementalGate = new(1, 1);

    async Task RefreshChangedPathsAsync()
    {
        if (isClosing) return;
        if (!await incrementalGate.WaitAsync(0)) return;
        try
        {
            var changed = changedPaths.Keys.ToArray();
            foreach (var path in changed) changedPaths.TryRemove(path, out _);
            if (watcherOverflow || changed.Length > 500 || videos.Count == 0)
            {
                watcherOverflow = false;
                await RefreshAsync();
                return;
            }
            if (changed.Length == 0) return;
            var roots = NormalizeRoots(config.Folders);
            var favorites = new HashSet<string>(config.Favorites, StringComparer.OrdinalIgnoreCase);
            var delta = await Task.Run(() =>
            {
                var replacements = new Dictionary<string, VideoItem>(StringComparer.OrdinalIgnoreCase);
                var directories = changed.Where(Directory.Exists).ToArray();
                foreach (var directory in directories)
                    foreach (var item in Scan([directory], favorites).Items) replacements[item.Path] = item;
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
                return (replacements, directories);
            });
            if (isClosing) return;
            if (delta.replacements.Count == 0 && !changed.Any(path => videos.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase) || IsDescendantPath(path, item.Path)))) return;
            var previous = videos.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
            var realChange = changed.Any(path => !File.Exists(path) && !Directory.Exists(path) && previous.Keys.Any(old => old.Equals(path, StringComparison.OrdinalIgnoreCase) || IsDescendantPath(path, old)))
                || delta.replacements.Any(pair => !previous.TryGetValue(pair.Key, out var old) || old.Size != pair.Value.Size || old.Modified != pair.Value.Modified);
            if (!realChange) return;
            foreach (var path in changed)
            {
                if (Directory.Exists(path))
                {
                    foreach (var old in previous.Keys.Where(key => IsDescendantPath(path, key)).ToArray()) previous.Remove(old);
                }
                else if (!File.Exists(path))
                {
                    foreach (var old in previous.Keys.Where(key => key.Equals(path, StringComparison.OrdinalIgnoreCase) || IsDescendantPath(path, key)).ToArray()) previous.Remove(old);
                }
                else previous.Remove(path);
            }
            foreach (var pair in delta.replacements) previous[pair.Key] = pair.Value;
            var additions = delta.replacements.Values.ToList();
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

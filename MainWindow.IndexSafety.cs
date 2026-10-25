using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    internal readonly record struct IndexSaveResult(bool PreservedMismatchedIndex, int PreviousEntryCount);

    // An empty scan of a different folder set is not proof that the old
    // library was deleted. Keep the existing on-disk index as a recovery
    // snapshot, while the current UI can truthfully show the new empty set.
    internal static bool ShouldPreserveMismatchedIndex(LibraryIndex? previous, LibraryIndex incoming,
        bool explicitlyClearedByUser = false) =>
        !explicitlyClearedByUser && incoming.Entries.Count == 0 && previous is { Entries.Count: > 0 } &&
        !NormalizeRoots(previous.Folders).SequenceEqual(NormalizeRoots(incoming.Folders), StringComparer.OrdinalIgnoreCase);

    // Caller serializes concurrent writes with IndexWriteGate and the named
    // index mutex. The path parameter also lets diagnostics exercise the
    // actual save logic in a temporary directory, never in the user library.
    internal static IndexSaveResult WriteIndexSnapshotAtPath(string indexPath, LibraryIndex incoming,
        bool explicitlyClearedByUser = false)
    {
        var hasValidPrevious = TryReadLibraryIndex(indexPath, out var previous);
        if (hasValidPrevious && ShouldPreserveMismatchedIndex(previous, incoming, explicitlyClearedByUser))
        {
            PreserveGoodIndexSnapshot(indexPath);
            return new IndexSaveResult(true, previous!.Entries.Count);
        }
        if (!hasValidPrevious && !explicitlyClearedByUser && incoming.Entries.Count == 0)
        {
            // A damaged primary must not make a valid recovery copy invisible.
            // Keep the .bak/.last-good file in place instead of replacing it
            // with a new empty index for an unrelated folder set.
            foreach (var recovery in new[] { indexPath + ".bak", indexPath + ".last-good" })
                if (TryReadLibraryIndex(recovery, out var recovered) &&
                    ShouldPreserveMismatchedIndex(recovered, incoming))
                    return new IndexSaveResult(true, recovered!.Entries.Count);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(indexPath))!);
        var temporary = $"{indexPath}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(incoming));
            if (hasValidPrevious) File.Replace(temporary, indexPath, indexPath + ".bak");
            else File.Move(temporary, indexPath, true);
            if (incoming.Entries.Count > 0) PreserveGoodIndexSnapshot(indexPath);
            return default;
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    void ShowPreservedMismatchedIndexNotice()
    {
        const string summary = "当前文件夹扫描为 0，且与旧索引的位置不同；旧索引已保留。请检查视频文件夹设置。";
        TaskStatusText.Text = summary;
        if (view is "all" or "recent" or "favorites" or "categories") PageSubtitle.Text = summary;
        if (videos.Count == 0 && string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            EmptyTitle.Text = "当前文件夹没有视频";
            EmptyDescription.Text = "文件夹与旧索引不同，旧索引已保留。请检查“视频文件夹”中的位置。";
        }
        Log($"本次扫描为 0；配置根与旧索引不同，已保留旧索引中的 {preservedMismatchedIndexCount} 部视频，不会混入当前列表");
    }
}

namespace YinggeVideoLibrary;

internal enum CleanupWriteOutcome
{
    Done,
    // The index change succeeded but the journal could not record it; the next
    // start may treat the action as unfinished, so the caller must say so.
    DoneButJournalUnmarked,
    NothingToDo,
    JournalFailed,
    IndexFailed
}

// Order matters more than brevity here: the journal is written before the index
// is touched, and it is only marked done after the index was actually saved.
// Every step reports failure instead of swallowing it.
internal static class MissingCleanupCoordinator
{
    internal static async Task<CleanupWriteOutcome> CleanupAsync(string dataDir,
        IReadOnlyList<MissingCleanupEntry> entries, Func<Task<bool>> applyAndSaveIndex)
    {
        var record = MissingCleanupStore.Begin(dataDir, entries);
        if (record is null) return CleanupWriteOutcome.JournalFailed;

        bool saved;
        try { saved = await applyAndSaveIndex(); }
        catch { saved = false; }
        if (!saved)
        {
            // Nothing was persisted, so the journal must not claim otherwise.
            MissingCleanupStore.MarkRolledBack(dataDir, record.Id);
            return CleanupWriteOutcome.IndexFailed;
        }
        return MissingCleanupStore.Complete(dataDir, record)
            ? CleanupWriteOutcome.Done
            : CleanupWriteOutcome.DoneButJournalUnmarked;
    }

    internal static async Task<CleanupWriteOutcome> UndoAsync(string dataDir,
        Func<IReadOnlyList<MissingCleanupEntry>, Task<bool>> restoreAndSaveIndex)
    {
        var begun = MissingCleanupStore.BeginUndo(dataDir, out var record);
        if (begun == UndoBeginResult.NothingToUndo) return CleanupWriteOutcome.NothingToDo;
        if (begun == UndoBeginResult.JournalFailed || record is null) return CleanupWriteOutcome.JournalFailed;

        bool saved;
        try { saved = await restoreAndSaveIndex(record.Entries); }
        catch { saved = false; }
        if (!saved)
        {
            // The index was not written, so the cleanup stays undoable.
            MissingCleanupStore.AbortUndo(dataDir, record.Id);
            return CleanupWriteOutcome.IndexFailed;
        }
        return MissingCleanupStore.CompleteUndo(dataDir, record.Id)
            ? CleanupWriteOutcome.Done
            : CleanupWriteOutcome.DoneButJournalUnmarked;
    }

    internal static async Task<CleanupWriteOutcome> RecoverAsync(string dataDir,
        Func<MissingCleanupRecord, Task<bool>> restoreAndSaveIndex)
    {
        MissingCleanupStore.ResolveInterruptedUndo(dataDir);
        var pending = MissingCleanupStore.Pending(dataDir);
        if (pending is null) return CleanupWriteOutcome.NothingToDo;

        bool saved;
        try { saved = await restoreAndSaveIndex(pending); }
        catch { saved = false; }
        // A failed write must keep the pending marker: it is the only evidence
        // that these rows still have to be restored.
        if (!saved) return CleanupWriteOutcome.IndexFailed;

        return MissingCleanupStore.MarkRolledBack(dataDir, pending.Id)
            ? CleanupWriteOutcome.Done
            : CleanupWriteOutcome.DoneButJournalUnmarked;
    }
}

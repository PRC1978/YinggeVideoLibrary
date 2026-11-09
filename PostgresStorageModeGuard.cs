using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// The file-backed application must never write a library that has been cut over
// to PostgreSQL. A future cutover writes this marker only after database and
// rollback verification. Presence alone blocks the legacy writer, including
// when the marker is damaged or only partly written.
//
// A restore flips the library into a blocked state first: while the sentinel
// exists neither the file writer nor a database session may open the library.
// That is deliberately stricter than "marker absent", because a half-restored
// database must never look like a clean file-mode library.
internal static class PostgresStorageModeGuard
{
    internal sealed record RestoreAttempt(string StagingSchema, string PreviousSchema,
        DateTimeOffset StartedUtc, int ProcessId);
    internal const string MarkerName = "storage-postgresql.active";
    internal const string RestoreSentinelName = ".postgres-restore-in-progress";

    internal enum StorageMode
    {
        LegacyFiles,
        PostgreSql,
        Blocked
    }

    internal static StorageMode Current(string dataDirectory)
    {
        if (File.Exists(Path.Combine(dataDirectory, RestoreSentinelName)) ||
            Directory.Exists(Path.Combine(dataDirectory, RestoreSentinelName)))
            return StorageMode.Blocked;
        var marker = Path.Combine(dataDirectory, MarkerName);
        if (!File.Exists(marker) && !Directory.Exists(marker))
        {
            // A missing marker beside an initialized cluster must not reopen
            // the stale JSON snapshot and fork the library's history.
            if (File.Exists(Path.Combine(dataDirectory, PostgresClusterLocation.MarkerName)) ||
                File.Exists(Path.Combine(dataDirectory, PostgresClusterLocation.PendingName)))
                return StorageMode.Blocked;
            var clusterDirectory = PostgresClusterLocation.DefaultPath(dataDirectory);
            if (Directory.Exists(clusterDirectory) &&
                Directory.EnumerateFileSystemEntries(clusterDirectory).Any())
                return StorageMode.Blocked;
            return StorageMode.LegacyFiles;
        }
        if (PostgresStorageModeActivation.TryRead(dataDirectory) is not null)
            return StorageMode.PostgreSql;
        // An isolated restore publishes a directory that was never a file-mode
        // library. Its marker carries no backup pointer yet, but it still
        // authorizes the database and blocks the JSON writer.
        if (PostgresStorageModeActivation.IsAuthorizationOnly(marker))
            return StorageMode.PostgreSql;
        // Any other marker cannot be read as the current format; it blocks the
        // JSON writers and does not authorize PostgreSQL startup.
        return StorageMode.Blocked;
    }

    internal static bool LegacyFilesMayOpen(string dataDirectory) =>
        Current(dataDirectory) == StorageMode.LegacyFiles &&
        !File.Exists(Path.Combine(dataDirectory, PostgresLibraryCutover.NewLibraryPendingName));

    internal static bool PostgresMayOpen(string dataDirectory) =>
        Current(dataDirectory) == StorageMode.PostgreSql;

    internal static void BeginRestore(string dataDirectory, string? stagingSchema = null,
        string? previousSchema = null)
    {
        Directory.CreateDirectory(dataDirectory);
        var sentinel = Path.Combine(dataDirectory, RestoreSentinelName);
        var temp = sentinel + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(new RestoreAttempt(
            stagingSchema ?? "", previousSchema ?? "", DateTimeOffset.UtcNow,
            Environment.ProcessId)));
        File.Move(temp, sentinel, overwrite: true);
    }

    internal static RestoreAttempt? ReadRestoreAttempt(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, RestoreSentinelName);
            var attempt = JsonSerializer.Deserialize<RestoreAttempt>(File.ReadAllBytes(path));
            if (attempt is null || !attempt.StagingSchema.StartsWith("yingge_restore_",
                    StringComparison.Ordinal) ||
                !attempt.PreviousSchema.StartsWith("yingge_prev_", StringComparison.Ordinal))
                return null;
            PostgresLibraryMigration.ValidateSchema(attempt.StagingSchema);
            PostgresLibraryMigration.ValidateSchema(attempt.PreviousSchema);
            return attempt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException)
        {
            return null;
        }
    }

    internal static void EndRestore(string dataDirectory)
    {
        PostgresStorageModeActivation.TryDelete(Path.Combine(dataDirectory, RestoreSentinelName));
    }
}

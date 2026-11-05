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
        if (!File.Exists(marker) && !Directory.Exists(marker)) return StorageMode.LegacyFiles;
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
        Current(dataDirectory) == StorageMode.LegacyFiles;

    internal static bool PostgresMayOpen(string dataDirectory) =>
        Current(dataDirectory) == StorageMode.PostgreSql;

    internal static void BeginRestore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var sentinel = Path.Combine(dataDirectory, RestoreSentinelName);
        var temp = sentinel + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(new
        {
            startedUtc = DateTimeOffset.UtcNow,
            processId = Environment.ProcessId
        }));
        File.Move(temp, sentinel, overwrite: true);
    }

    internal static void EndRestore(string dataDirectory)
    {
        PostgresStorageModeActivation.TryDelete(Path.Combine(dataDirectory, RestoreSentinelName));
    }
}

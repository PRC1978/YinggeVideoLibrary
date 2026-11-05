using System.IO;

namespace YinggeVideoLibrary;

// The file-backed application must never write a library that has been cut over
// to PostgreSQL. A future cutover writes this marker only after database and
// rollback verification. Presence alone blocks the legacy writer, including
// when the marker is damaged or only partly written.
internal static class PostgresStorageModeGuard
{
    internal const string MarkerName = "storage-postgresql.active";

    internal static bool LegacyFilesMayOpen(string dataDirectory) =>
        !File.Exists(Path.Combine(dataDirectory, MarkerName)) &&
        !Directory.Exists(Path.Combine(dataDirectory, MarkerName));
}

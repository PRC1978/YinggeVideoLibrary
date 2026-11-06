using System.Collections.Concurrent;
using System.IO;

namespace YinggeVideoLibrary;

// Registered only by a fully opened database-mode desktop window. Diagnostics
// and legacy libraries keep their existing file-backed behavior.
internal static class AiStorageRoutes
{
    private static readonly ConcurrentDictionary<string, string> Routes =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void Register(string dataDir, string connectionString) =>
        Routes[Path.GetFullPath(dataDir)] = connectionString;

    internal static string? ConnectionStringFor(string dataDir) =>
        Routes.TryGetValue(Path.GetFullPath(dataDir), out var value) ? value : null;
}

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Deliberately avoids MainWindow, LoadConfig and the cache migration path. It
// verifies that a published executable sees the same account's library without
// changing even a migration marker in the live data directory.
internal static class SharedCacheReadOnlyProbe
{
    /// <summary>
    /// Explicit opt-in for reading the live data directory. The probe only
    /// reads, but "just running a self-test" must not be enough to open a real
    /// user library, so an unredirected run is refused unless the operator says
    /// so. A redirected run (<c>--data-dir</c>) needs no opt-in.
    /// </summary>
    internal const string AllowRealLibraryReadArgument = "--allow-real-library-read";

    /// <summary>Exit code used when the probe refuses to read the live library.</summary>
    internal const int RefusedExitCode = 3;

    static bool HasArgument(string name) => Environment.GetCommandLineArgs().Skip(1)
        .Any(argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static int Run(string destination)
    {
        try
        {
            var dataDirectory = Path.GetFullPath(AppPaths.DataDirectory);
            var reportPath = Path.GetFullPath(destination);
            var dataPrefix = dataDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (reportPath.Equals(dataDirectory, StringComparison.OrdinalIgnoreCase)
                || reportPath.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("只读探针的报告必须写入视频库数据目录之外。");

            if (!AppPaths.IsRedirected && !HasArgument(AllowRealLibraryReadArgument))
            {
                const string reason = "拒绝读取真实视频库：请加 --data-dir <隔离目录>，"
                    + "或显式加 --allow-real-library-read 表示同意读取当前用户库。";
                WriteReport(reportPath, new
                {
                    Stage = "read-only shared cache probe",
                    Refused = true,
                    Reason = reason,
                    Executable = Environment.ProcessPath ?? "",
                    WindowsUser = $"{Environment.UserDomainName}\\{Environment.UserName}",
                    DataDirectory = dataDirectory,
                    DataDirectoryRedirected = false,
                    RealLibraryRead = false,
                    ReadOnly = true
                });
                // A GUI-subsystem process has no reliable console, so the report
                // file is the authoritative channel; the message is best effort
                // and is written as UTF-8 so a redirected stream stays readable.
                WriteConsole(reason);
                return RefusedExitCode;
            }

            var config = ReadFirstValid(dataDirectory, "config.json", "folders");
            var index = ReadFirstValid(dataDirectory, "library-index.json", "Entries");
            var roots = config.Folders;
            var indexRoots = index.Folders;
            WriteReport(reportPath, new
            {
                Stage = "read-only shared cache probe",
                Refused = false,
                Executable = Environment.ProcessPath ?? "",
                WorkingDirectory = Environment.CurrentDirectory,
                WindowsUser = $"{Environment.UserDomainName}\\{Environment.UserName}",
                DataDirectory = dataDirectory,
                DataDirectoryRedirected = AppPaths.IsRedirected,
                RealLibraryRead = !AppPaths.IsRedirected,
                ConfigSource = config.Path,
                IndexSource = index.Path,
                ConfigSha256 = config.Sha256,
                IndexSha256 = index.Sha256,
                ConfiguredFolderCount = roots.Length,
                AvailableFolderCount = roots.Count(Directory.Exists),
                IndexFolderCount = indexRoots.Length,
                FoldersMatch = roots.Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(indexRoots.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
                IndexedVideoCount = index.EntryCount,
                ReadOnly = true
            });
            return 0;
        }
        catch { return 1; }
    }

    static void WriteConsole(string message)
    {
        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException
            or PlatformNotSupportedException) { }
        try { Console.Error.WriteLine("映格视频库 " + message); }
        catch (IOException) { }
    }

    static void WriteReport(string reportPath, object report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static (string Path, string Sha256, string[] Folders, int EntryCount) ReadFirstValid(
        string directory, string name, string requiredProperty)
    {
        foreach (var suffix in new[] { "", ".bak", ".last-good" })
        {
            var path = Path.Combine(directory, name + suffix);
            if (!File.Exists(path)) continue;
            try
            {
                using var stream = File.OpenRead(path);
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;
                if (!TryProperty(root, requiredProperty, out var required)
                    || required.ValueKind != JsonValueKind.Array) continue;
                var folders = TryProperty(root, "Folders", out var folderArray)
                    && folderArray.ValueKind == JsonValueKind.Array
                        ? folderArray.EnumerateArray()
                            .Where(value => value.ValueKind == JsonValueKind.String)
                            .Select(value => value.GetString() ?? "")
                            .Where(value => value.Length > 0).ToArray()
                        : [];
                stream.Position = 0;
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                return (path, hash, folders, required.GetArrayLength());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        throw new FileNotFoundException($"找不到有效的 {name} 或备份。");
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }
}

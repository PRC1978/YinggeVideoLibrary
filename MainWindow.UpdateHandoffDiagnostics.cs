using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    internal static void PrepareIsolatedUpdateHandoff(string newExecutable, string outputPath)
    {
        var data = AppPaths.DataDirectory;
        var host = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("No host"));
        var hostDirectory = Path.GetDirectoryName(host)!;
        var required = Path.DirectorySeparatorChar + Path.Combine("artifacts", "1733-p0") + Path.DirectorySeparatorChar;
        if (!host.Contains(required, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Handoff must run from artifacts/1733-p0, never an installed copy");
        for (var directory = new DirectoryInfo(hostDirectory); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Handoff host cannot use reparse points");
        if (!PublishedExecutable.TryGetBinaryVersion(host, out var oldVersion) || oldVersion != new Version(1, 7, 3, 2))
            throw new InvalidOperationException("Expected fixed isolated old host 1.7.3.2");
        if (!PublishedExecutable.TryGetBinaryVersion(Path.GetFullPath(newExecutable), out var newVersion) || newVersion != new Version(1, 7, 3, 3))
            throw new InvalidOperationException("Expected new published executable 1.7.3.3");
        if (!PortableUpdateWorker.HasBundledPostgres(hostDirectory))
            throw new InvalidOperationException("Isolated host lacks PostgreSQL runtime");
        if (!AppPaths.IsRedirected || AppPaths.RedirectError is not null ||
            data.Equals(AppPaths.DefaultDataDirectory, StringComparison.OrdinalIgnoreCase) ||
            data.StartsWith(hostDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any()))
            throw new InvalidOperationException("Handoff requires a fresh isolated data directory");
        if (!Path.IsPathFullyQualified(outputPath) || File.Exists(outputPath) || Directory.Exists(outputPath))
            throw new InvalidOperationException("Handoff output must be an unused absolute filename");
        var fullOutput = Path.GetFullPath(outputPath);
        if (fullOutput.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Keep handoff evidence outside the restarted library");
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "config.json"),
            "{\"folders\":[],\"updateAutoCheck\":false,\"closeToTray\":false}");
    }

    internal async Task<bool> RunIsolatedUpdateHandoffAsync(string newExecutable, string outputPath)
    {
        var stages = new List<object>();
        var host = Environment.ProcessPath!;
        var oldVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString();
        string? failure = null;
        void Record(string phase)
        {
            stages.Add(new { phase, timeUtc = DateTimeOffset.UtcNow,
                shutdownRequested = updateExitRequested, closing = isClosing });
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                oldPid = Environment.ProcessId, host, oldVersion, dataDirectory = DataDir,
                scope = "Production DownloadAndApplyUpdateAsync handoff from isolated old host; parent must verify replacement and restart",
                realLibraryRead = false, windowShown = false, closeToTrayInMemory = config.CloseToTray,
                shutdownRequested = updateExitRequested, failure, stages
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        config.CloseToTray = true;
        config.UpdateAutoCheck = false;
        System.Windows.Application.Current.Exit += (_, _) => Record("application-exit");
        Closed += (_, _) => Record("window-closed");
        try
        {
            if (!PublishedExecutable.TryGetBinaryVersion(Path.GetFullPath(newExecutable), out var version))
                throw new InvalidDataException("Invalid new executable");
            var updates = Path.Combine(DataDir, "updates");
            Directory.CreateDirectory(updates);
            var cached = Path.Combine(updates, $"YinggeVideoLibrary-win-x64-{version}.exe");
            File.Copy(newExecutable, cached, false);
            await using var input = File.OpenRead(cached);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input));
            var release = new ReleaseUpdate("v" + version, version,
                new Uri("file:///isolated-handoff-cache-must-already-be-verified.exe"),
                new FileInfo(cached).Length, hash, true, "Isolated production handoff");
            Record("calling-production-handoff");
            await DownloadAndApplyUpdateAsync(release);
            if (!updateExitRequested) failure = updateStatus?.Text ?? "Production handoff did not request shutdown";
            Record("production-handoff-returned");
            return updateExitRequested;
        }
        catch (Exception error)
        {
            failure = error.ToString();
            Record("diagnostic-failed");
            return false;
        }
    }
}

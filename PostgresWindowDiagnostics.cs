using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal static class PostgresWindowDiagnostics
{
    internal static async Task<int> RunAsync(string runtimeDirectory, string dataDirectory,
        string outputPath)
    {
        var result = new Dictionary<string, object?> { ["passed"] = false,
            ["realLibraryRead"] = false };
        MainWindow? window = null;
        try
        {
            dataDirectory = Path.GetFullPath(dataDirectory);
            if (!dataDirectory.StartsWith(Path.GetFullPath(Path.GetTempPath()),
                StringComparison.OrdinalIgnoreCase) ||
                PostgresStorageModeGuard.Current(dataDirectory) !=
                    PostgresStorageModeGuard.StorageMode.PostgreSql)
                throw new InvalidDataException("窗口诊断仅允许使用临时目录下已启用的数据库视频库。");
            Environment.SetEnvironmentVariable("YINGGE_DATA_DIR", dataDirectory);
            using var lease = LibraryBackupService.AcquireSessionLease(dataDirectory);
            await using var session = await PostgresLibraryRuntimeSession.OpenAsync(
                runtimeDirectory, dataDirectory);
            window = new MainWindow(session) { ShowInTaskbar = false,
                WindowState = System.Windows.WindowState.Minimized };
            System.Windows.Application.Current.MainWindow = window;
            window.Show();
            var uiReport = Path.Combine(Path.GetDirectoryName(outputPath)!,
                "postgres-window-ui-" + Guid.NewGuid().ToString("N") + ".json");
            var uiCode = await window.WriteHiddenStartupDiagnosticAsync(uiReport,
                verifyRoles: true);
            result["uiExitCode"] = uiCode;
            result["uiReport"] = uiReport;
            result["videos"] = session.Index.Index.Entries.Count;
            result["passed"] = uiCode == 0;
            window.Close();
            window = null;
        }
        catch (Exception error) { result["error"] = error.ToString(); }
        finally
        {
            if (window is not null) try { window.Close(); } catch { }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllBytesAsync(outputPath, JsonSerializer.SerializeToUtf8Bytes(result));
        }
        return Equals(result["passed"], true) ? 0 : 1;
    }
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace YinggeVideoLibrary;

// A real WPF dispatcher experiment, not a fake Application or a library launch.
// Both children exit through App's early diagnostic routes before single-instance
// or database setup; the parent only terminates processes it started itself.
internal static class UpdateShutdownDiagnostics
{
    internal static void StartWindowProbe(App app, string reportPath)
    {
        bool closing = false, closed = false;
        bool? closingAtReturn = null, closedAtReturn = null;
        var window = new Window
        {
            Title = "Update shutdown isolation probe", Width = 240, Height = 100,
            Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false,
            Opacity = 0
        };
        app.MainWindow = window;
        window.Closing += (_, _) => closing = true;
        window.Closed += (_, _) => closed = true;
        app.Exit += (_, _) => File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            closingAtReturn, closedAtReturn, closingAfterDispatcher = closing,
            closedAfterDispatcher = closed,
            immediateCheckWouldKillHelper = closingAtReturn == false
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Show();
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            app.Shutdown();
            closingAtReturn = closing;
            closedAtReturn = closed;
            // Deliberately never kill a helper here: Shutdown queues closure;
            // Closed/Closing cannot be used as an immediate return-value check.
        }));
    }

    static Process Start(string argument, string path)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!
            ?? throw new InvalidOperationException("No executable path"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        info.ArgumentList.Add(argument);
        info.ArgumentList.Add(path);
        return Process.Start(info) ?? throw new InvalidOperationException("Probe process did not start");
    }

    internal static async Task<int> RunAsync(string outputPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "YinggeUpdateShutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var childReport = Path.Combine(root, "wpf.json");
        var helperReady = Path.Combine(root, "helper-ready.txt");
        var checks = new Dictionary<string, bool>();
        string? error = null;
        Process? helper = null, child = null;
        try
        {
            helper = Start("--diagnose-update-shutdown-helper", helperReady);
            var readyLimit = Stopwatch.StartNew();
            while (!File.Exists(helperReady) && !helper.HasExited && readyLimit.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(50);
            checks["helperStarted"] = File.Exists(helperReady) && !helper.HasExited;
            child = Start("--diagnose-update-shutdown-child", childReport);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await child.WaitForExitAsync(timeout.Token);
            checks["wpfProcessExited"] = child.ExitCode == 0;
            checks["helperSurvivedShutdown"] = !helper.HasExited;
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(childReport));
            var value = report.RootElement;
            checks["closingNotYetRaisedAtShutdownReturn"] = !value.GetProperty("closingAtReturn").GetBoolean();
            checks["closedNotYetRaisedAtShutdownReturn"] = !value.GetProperty("closedAtReturn").GetBoolean();
            checks["closedEventuallyRaised"] = value.GetProperty("closedAfterDispatcher").GetBoolean();
            checks["oldImmediateCheckWouldKillHelper"] = value.GetProperty("immediateCheckWouldKillHelper").GetBoolean();
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            foreach (var owned in new[] { child, helper })
            {
                if (owned is null) continue;
                try { if (!owned.HasExited) { owned.Kill(); await owned.WaitForExitAsync(); } }
                catch (Exception exception) { error ??= exception.ToString(); }
                finally { owned.Dispose(); }
            }
        }
        var passed = error is null && checks.Count == 7 && checks.Values.All(value => value);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
        {
            passed, realLibraryRead = false, productionInstallRead = false,
            scope = "Real WPF Shutdown lifecycle only; no product update download or PostgreSQL",
            evidenceDirectory = root, checks, error
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}

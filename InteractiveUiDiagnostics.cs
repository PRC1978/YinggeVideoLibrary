using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace YinggeVideoLibrary;

// Controlled, visible production UI for manual keyboard and layout acceptance.
// No alternate controls or decision behavior are installed by this harness.
internal static class InteractiveUiDiagnostics
{
    internal static void Prepare(string outputPath, int width, int height, string theme)
    {
        var data = AppPaths.DataDirectory;
        if (!AppPaths.IsRedirected || AppPaths.RedirectError is not null ||
            data.Equals(AppPaths.DefaultDataDirectory, StringComparison.OrdinalIgnoreCase) ||
            (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any()))
            throw new InvalidOperationException("Interactive UI acceptance requires a new isolated empty data directory");
        if ((width, height) is not ((800, 550) or (1240, 780) or (1600, 1000)))
            throw new ArgumentException("Use one of the three declared acceptance window sizes");
        if (!Path.IsPathFullyQualified(outputPath) || File.Exists(outputPath))
            throw new ArgumentException("Use a new absolute report filename");
        var output = Path.GetFullPath(outputPath);
        if (output.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Keep the evidence report outside the fixture library");
        WindowsCredentialStore.EnableIsolatedDiagnostics();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        UiFixtureGenerator.Seed(data, 12, 20, 6);
        var configPath = Path.Combine(data, "config.json");
        var config = JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(configPath))
            ?? throw new InvalidDataException("Fixture config was not generated");
        config.UiTheme = UiTheme.Normalize(theme);
        config.StartPage = "scrape";
        config.CloseToTray = false;
        config.MinimizeToTray = false;
        config.AutoStart = false;
        config.UpdateAutoCheck = false;
        var index = 0;
        foreach (var id in config.Categories.Keys.Take(4))
        {
            index++;
            config.AiReviews[id] = new AiReviewRecord
            {
                VideoId = id, CandidateCode = $"UI-{index:000}", CandidateTitle = $"隔离界面候选 {index}",
                CandidateSource = "fixture", Confidence = 35 + index * 10,
                Status = "pending", DecisionSource = "rules",
                Reason = "由产品夹具生成的排版测试记录，不代表真实搜刮结果。"
            };
        }
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));
    }

    internal static void Observe(MainWindow window, App app, string outputPath, int width, int height)
    {
        var events = new List<object>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var completed = false;
        string SnapshotKey() => $"{window.ActualWidth}/{window.ActualHeight}/{UiTheme.Current}/{window.PageTitleForInteractiveDiagnostic()}";
        var lastState = "";
        void Record(string phase, string? key = null)
        {
            events.Add(new { phase, key, timeUtc = DateTimeOffset.UtcNow,
                actualWidth = window.ActualWidth, actualHeight = window.ActualHeight,
                theme = UiTheme.Current, page = window.PageTitleForInteractiveDiagnostic(),
                focusedControl = (Keyboard.FocusedElement as FrameworkElement)?.Name,
                focusedType = Keyboard.FocusedElement?.GetType().Name });
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, executable = Environment.ProcessPath,
                dataDirectory = AppPaths.DataDirectory, realLibraryRead = false,
                requestedWidth = width, requestedHeight = height,
                completed, scope = "Visible isolated production window; event trace alone does not prove keyboard task success",
                events
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        timer.Tick += (_, _) =>
        {
            var state = SnapshotKey();
            if (state != lastState) { lastState = state; Record("page-theme-or-size-changed"); }
        };
        window.PreviewKeyDown += (_, args) => Record("key-down", args.Key.ToString());
        window.ContentRendered += (_, _) => { Record("content-rendered"); timer.Start(); };
        window.Closed += (_, _) =>
        {
            timer.Stop(); completed = true; Record("window-closed");
            app.Shutdown(0);
        };
        app.Exit += (_, _) => { timer.Stop(); Record("application-exit"); };
    }
}

public partial class MainWindow
{
    internal string PageTitleForInteractiveDiagnostic() => PageTitle.Text;
}

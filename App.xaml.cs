using System.Configuration;
using System.Data;
using System.Windows;

namespace YinggeVideoLibrary;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] is "--apply-update" or "--test-apply-update")
        {
            Shutdown(await Task.Run(() => PortableUpdateWorker.Apply(e.Args)));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-updater")
        {
            Shutdown(PortableUpdateWorker.SelfTest(e.Args[1]));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-features")
        {
            Shutdown(await FeatureDiagnostics.RunAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--diagnose-cache")
        {
            var destination = e.Args.Length > 1 ? e.Args[1] : null;
            Shutdown(YinggeVideoLibrary.MainWindow.WriteHeadlessCacheDiagnostic(destination));
            return;
        }

        if (e.Args.Length > 1 && e.Args[0] == "--diagnose-ui-cache")
        {
            var probeWindow = new MainWindow();
            MainWindow = probeWindow;
            var result = await probeWindow.WriteHeadlessUiDiagnosticAsync(e.Args[1]);
            probeWindow.Close();
            Shutdown(result);
            return;
        }

        if (e.Args.Length > 1 && e.Args[0] == "--diagnose-ui-startup")
        {
            var probeWindow = new MainWindow
            {
                Left = -30000, Top = -30000, Width = 1224, Height = 768,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0
            };
            MainWindow = probeWindow;
            probeWindow.Show();
            var result = await probeWindow.WriteHiddenStartupDiagnosticAsync(e.Args[1], e.Args.Length > 2 && e.Args[2] == "--categories", e.Args.Length > 2 && e.Args[2] == "--scrape", e.Args.Length > 2 && e.Args[2] == "--settings");
            probeWindow.Close();
            Shutdown(result);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}


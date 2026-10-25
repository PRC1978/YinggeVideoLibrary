using System.Configuration;
using System.Data;
using System.Windows;

namespace YinggeVideoLibrary;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    System.IO.FileStream? librarySessionLease;

    protected override void OnExit(ExitEventArgs e)
    {
        librarySessionLease?.Dispose();
        base.OnExit(e);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Headless diagnostics and the update worker can await before creating
        // any window. Keep WPF alive until each path reports its real result.
        if (e.Args.Length > 0 && (e.Args[0].StartsWith("--self-test-", StringComparison.Ordinal)
            || e.Args[0].StartsWith("--diagnose-", StringComparison.Ordinal)
            || e.Args[0] is "--apply-update" or "--test-apply-update"))
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
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
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-update-transition")
        {
            Shutdown(PortableUpdateWorker.TransitionSelfTest(e.Args[1], e.Args[2]));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-restarted")
        {
            Shutdown(PortableUpdateWorker.WriteRestartProbe(e.Args[1]));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-startup-update")
        {
            Shutdown(StartupUpdateGateDiagnostics.RunToFile(e.Args[1]));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-features")
        {
            Shutdown(await FeatureDiagnostics.RunAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-shared-cache")
        {
            Shutdown(await Task.Run(() => SharedCacheReadOnlyProbe.Run(e.Args[1])));
            return;
        }
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-cross-device")
        {
            Shutdown(await Task.Run(() => BackupCrossDeviceDiagnostics.RunToFile(e.Args[1], e.Args[2])));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-ai-processes")
        {
            Shutdown(await AiProcessDiagnostics.RunAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-ai-child")
        {
            Shutdown(await AiProcessDiagnostics.RunChildAsync(e.Args));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-review-child")
        {
            Shutdown(ReviewAdversarialDiagnostics.RunChild(e.Args));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--diagnose-batch")
        {
            Shutdown(await YinggeVideoLibrary.MainWindow.WriteHeadlessBatchDiagnosticAsync(e.Args[1]));
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
            var result = await probeWindow.WriteHiddenStartupDiagnosticAsync(e.Args[1], e.Args.Length > 2 && e.Args[2] == "--categories", e.Args.Length > 2 && e.Args[2] == "--scrape", e.Args.Length > 2 && e.Args[2] == "--settings", e.Args.Length > 2 && e.Args[2] == "--selection", e.Args.Length > 2 && e.Args[2] == "--folders", e.Args.Length > 2 && e.Args[2] == "--recommendations", e.Args.Length > 2 && e.Args[2] == "--roles",
                e.Args.Length > 2 && e.Args[2] == "--scroll" ? 300 : e.Args.Length > 2 && e.Args[2] == "--scroll-30" ? 30 : 0);
            probeWindow.Close();
            Shutdown(result);
            return;
        }

        var dataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".YinggeVideoLibrary");
        if (!LibraryRelocationService.ApplyPendingAtStartup(dataDir, out var restoreError))
        {
            System.Windows.MessageBox.Show("恢复已停止，视频库尚未加载。\n" + restoreError,
                "映格视频库 · 备份恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        if (!ReviewTransactionStore.RecoverPendingAtStartup(dataDir, out var reviewRecoveryError))
        {
            System.Windows.MessageBox.Show("审核操作恢复已停止，视频库尚未加载。\n" + reviewRecoveryError,
                "映格视频库 · 审核恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        if (!DuplicateMetadataTransactionStore.RecoverPendingAtStartup(dataDir, out var duplicateRecoveryError))
        {
            System.Windows.MessageBox.Show("重复视频资料合并恢复已停止，视频库尚未加载。\n" + duplicateRecoveryError,
                "映格视频库 · 资料恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        if (!LibraryBackupService.ApplyPendingImportAtStartup(dataDir, out var importError))
        {
            System.Windows.MessageBox.Show("选择性导入已停止，视频库尚未加载。\n" + importError,
                "映格视频库 · 数据导入", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        if (!LibraryBackupService.ApplyPendingOfflineBackupAtStartup(dataDir, out var backupError))
        {
            System.Windows.MessageBox.Show(backupError, "映格视频库 · 一致性备份",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        try { librarySessionLease = LibraryBackupService.AcquireSessionLease(dataDir); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("无法锁定共享视频库数据，请稍后重试。\n" + ex.Message,
                "映格视频库 · 数据保护", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.ReconcileStartupRegistration();
        window.ApplyStartupWindowPreferences(e.Args.Contains("--auto-start", StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(backupError))
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() =>
                System.Windows.MessageBox.Show(window,
                    backupError + "\n备份安排已保留，下次启动会重试；也可以在设置中取消。",
                    "映格视频库 · 一致性备份", MessageBoxButton.OK, MessageBoxImage.Warning)));
        window.Show();
    }
}


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
    PostgresLibraryRuntimeSession? postgresRuntimeSession;

    protected override void OnExit(ExitEventArgs e)
    {
        if (postgresRuntimeSession is not null)
        {
            postgresRuntimeSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
            postgresRuntimeSession = null;
        }
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
        if (e.Args.Length > 2 && e.Args[0] == "--diagnose-review-recovery")
        {
            Shutdown(ReviewTransactionRecoveryDiagnostics.RecoverCopyToFile(e.Args[1], e.Args[2]));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-updater")
        {
            Shutdown(PortableUpdateWorker.SelfTest(e.Args[1]));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-ai-cost-preview")
        {
            Shutdown(AiCostPreviewDiagnostics.RunToFile(e.Args[1]));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--diagnose-live-sources")
        {
            Shutdown(await LiveSourceSmokeDiagnostics.RunToFileAsync(e.Args[1]));
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
        // Explicit, isolated PostgreSQL import probe. This never reads AppPaths.DataDirectory.
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-postgres")
        {
            Shutdown(await PostgresMigrationDiagnostics.RunToFileAsync(e.Args[1], e.Args[2]));
            return;
        }
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-bundled-postgres")
        {
            Shutdown(await BundledPostgresRuntime.RunProbeToFileAsync(e.Args[1], e.Args[2],
                e.Args.Length > 3 && e.Args[3] == "--scale"));
            return;
        }
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-postgres-backup")
        {
            Shutdown(await PostgresBackupAcceptance.RunToFileAsync(e.Args[1], e.Args[2]));
            return;
        }
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-postgres-backup-scale")
        {
            Shutdown(await PostgresBackupAcceptance.RunToFileAsync(e.Args[1], e.Args[2],
                includeLarge: true));
            return;
        }
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-postgres-selective-import")
        {
            Shutdown(await PostgresSelectiveImportDiagnostics.RunToFileAsync(e.Args[1], e.Args[2]));
            return;
        }
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-postgres-selective-import-scale")
        {
            Shutdown(await PostgresSelectiveImportDiagnostics.RunToFileAsync(e.Args[1], e.Args[2],
                30000));
            return;
        }
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-postgres-selective-import-cross-device")
        {
            Shutdown(await PostgresSelectiveImportDiagnostics.RunCrossDeviceToFileAsync(
                e.Args[1], e.Args[2]));
            return;
        }
        if (e.Args.Length > 3 && e.Args[0] == "--diagnose-postgres-window")
        {
            Shutdown(await PostgresWindowDiagnostics.RunAsync(e.Args[1], e.Args[2], e.Args[3]));
            return;
        }
        if (e.Args.Length > 3 && e.Args[0] == "--migrate-library-postgres")
        {
            Shutdown(await PostgresLibraryCutover.RunToFileAsync(e.Args[1], e.Args[2], e.Args[3]));
            return;
        }
        if (e.Args.Length > 3 && e.Args[0] == "--self-test-postgres-pending")
        {
            Shutdown(await PostgresLibraryCutover.TestPendingToFileAsync(e.Args[1], e.Args[2], e.Args[3]));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-cloud-account")
        {
            Shutdown(await CloudAccountDiagnostics.RunToFileAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-ai-scrape-eval")
        {
            Shutdown(await Task.Run(() => AiScrapeEvaluation.RunToFile(e.Args[1], e.Args[2])));
            return;
        }
        if (e.Args.Length > 2 && e.Args[0] == "--diagnose-large-backup")
        {
            Shutdown(await Task.Run(() => LargeBackupDiagnostics.RunToFile(e.Args[1], e.Args[2])));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-anime")
        {
            Shutdown(AnimeFeatureDiagnostics.RunToFile(e.Args[1]));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-airborne")
        {
            Shutdown(AirborneExtensionDiagnostics.RunToFile(e.Args[1]));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-shared-cache")
        {
            Shutdown(await Task.Run(() => SharedCacheReadOnlyProbe.Run(e.Args[1])));
            return;
        }
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-plugin-queue")
        {
            Shutdown(await Task.Run(() => PluginQueueDiagnostics.RunToFile(e.Args[1], e.Args[2])));
            return;
        }
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-missing-files")
        {
            Shutdown(await Task.Run(() => MissingFileDiagnostics.RunToFile(e.Args[1], e.Args[2])));
            return;
        }
        // --self-test-plugins <隔离工作目录> <结果 JSON> [示例插件构建输出目录]
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-plugins")
        {
            var sample = e.Args.Length > 3 ? e.Args[3] : null;
            Shutdown(await Task.Run(() => PluginDiagnostics.RunToFile(e.Args[1], e.Args[2], sample)));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--ui-fixture")
        {
            Shutdown(UiFixtureGenerator.Run(e.Args));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--diagnose-ui-search")
        {
            var searchProbe = new MainWindow
            {
                Left = -30000, Top = -30000, Width = 1224, Height = 768,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0
            };
            MainWindow = searchProbe;
            searchProbe.Show();
            var searchResult = await searchProbe.WriteHiddenSearchDiagnosticAsync(e.Args[1]);
            searchProbe.Close();
            Shutdown(searchResult);
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--diagnose-ui-motion")
        {
            var motionProbe = new MainWindow
            {
                Left = -30000, Top = -30000, Width = 1240, Height = 780,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0
            };
            MainWindow = motionProbe;
            motionProbe.Show();
            var motionResult = await motionProbe.WriteHiddenMotionDiagnosticAsync(e.Args[1]);
            motionProbe.Close();
            Shutdown(motionResult);
            return;
        }
        // --diagnose-ui-theme <输出目录>：所有界面模式 × 三档窗口 × 关键页面截图。
        if (e.Args.Length > 1 && e.Args[0] == "--diagnose-ui-theme")
        {
            var themeProbe = new MainWindow
            {
                Left = -30000, Top = -30000, Width = 1240, Height = 780,
                ShowActivated = false, ShowInTaskbar = false
            };
            MainWindow = themeProbe;
            themeProbe.Show();
            var themeResult = await themeProbe.WriteHiddenThemeDiagnosticAsync(e.Args[1]);
            themeProbe.Close();
            Shutdown(themeResult);
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--diagnose-ui-scrape")
        {
            var scrapeProbe = new MainWindow
            {
                Left = -30000, Top = -30000, Width = 1224, Height = 780,
                ShowActivated = false, ShowInTaskbar = false
            };
            MainWindow = scrapeProbe;
            scrapeProbe.Show();
            var scrapeResult = await scrapeProbe.WriteHiddenScrapeDiagnosticAsync(e.Args[1]);
            scrapeProbe.Close();
            Shutdown(scrapeResult);
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--diagnose-ui-cover")
        {
            var coverProbe = new MainWindow
            {
                Left = -30000, Top = -30000, Width = 1224, Height = 780,
                ShowActivated = false, ShowInTaskbar = false
            };
            MainWindow = coverProbe;
            coverProbe.Show();
            var result = await coverProbe.WriteHiddenCoverDiagnosticAsync(e.Args[1]);
            coverProbe.Close();
            Shutdown(result);
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--diagnose-ui-cover-reload")
        {
            var coverProbe = new MainWindow { Left = -30000, Top = -30000,
                ShowActivated = false, ShowInTaskbar = false };
            MainWindow = coverProbe;
            coverProbe.Show();
            var result = coverProbe.WriteCoverReloadDiagnostic(e.Args[1]);
            coverProbe.Close();
            Shutdown(result);
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

        if (e.Args.Length > 3 && e.Args[0] == "--diagnose-review-decision")
        {
            var decideWindow = new MainWindow
            {
                Left = -30000, Top = -30000, Width = 1224, Height = 768,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0
            };
            MainWindow = decideWindow;
            decideWindow.Show();
            var result = await decideWindow.RunReviewDecisionProbeAsync(e.Args[1], e.Args[2],
                e.Args[3] == "approve");
            decideWindow.Close();
            Shutdown(result);
            return;
        }
        if (e.Args.Length > 3 && e.Args[0] == "--diagnose-review-select")
        {
            var selectWindow = new MainWindow
            {
                Left = -30000, Top = -30000, Width = 1224, Height = 768,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0
            };
            MainWindow = selectWindow;
            selectWindow.Show();
            var result = await selectWindow.RunSelectCandidateProbeAsync(e.Args[1], e.Args[2],
                int.Parse(e.Args[3]));
            selectWindow.Close();
            Shutdown(result);
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--diagnose-plugin-queue")
        {
            var queueWindow = new MainWindow
            {
                Left = -30000, Top = -30000, Width = 1224, Height = 768,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0
            };
            MainWindow = queueWindow;
            queueWindow.Show();
            var result = await queueWindow.RunPluginQueueProbeAsync(e.Args[1]);
            queueWindow.Close();
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

        ProjectLicenseMigration.RefreshInstalledSidecar();
        // Stale update rollback copies are useful only right after an update. Prune
        // them in the background so a failed prune can never delay or block startup.
        _ = Task.Run(() => UpdateBackupJanitor.TryRun(System.IO.Path.GetDirectoryName(Environment.ProcessPath)));
        var dataDir = AppPaths.DataDirectory;
        if (System.IO.File.Exists(System.IO.Path.Combine(dataDir, PostgresLibraryCutover.PendingName)))
        {
            try
            {
                await PostgresLibraryCutover.ApplyPendingAsync(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "postgres"), dataDir);
            }
            catch (Exception error)
            {
                var noDatabaseWasCreated =
                    PostgresStorageModeGuard.Current(dataDir) ==
                        PostgresStorageModeGuard.StorageMode.LegacyFiles &&
                    !System.IO.Directory.Exists(System.IO.Path.Combine(dataDir, "postgres-data"));
                if (noDatabaseWasCreated)
                {
                    // Preflight failed before any database was created. Resume
                    // the ordinary file-mode startup so its pending operations
                    // can finish; the user can stage conversion again later.
                    try { System.IO.File.Delete(System.IO.Path.Combine(dataDir,
                        PostgresLibraryCutover.PendingName)); }
                    catch
                    {
                        Shutdown(1);
                        return;
                    }
                    System.Windows.MessageBox.Show("数据库转换未开始，已取消待办并继续载入原文件库。\n" + error.Message,
                        "映格视频库 · 数据库转换", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    System.Windows.MessageBox.Show("数据库转换已停止；旧资料文件未删除。为防止资料分叉，本次不载入视频库。\n" + error.Message,
                        "映格视频库 · 数据库转换", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Shutdown(1);
                    return;
                }
            }
        }
        var storageMode = PostgresStorageModeGuard.Current(dataDir);
        if (storageMode == PostgresStorageModeGuard.StorageMode.PostgreSql)
        {
            try
            {
                string importWarning = "";
                librarySessionLease = LibraryBackupService.AcquireSessionLease(dataDir);
                var runtimeDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "postgres");
                postgresRuntimeSession = await PostgresLibraryRuntimeSession.OpenAsync(
                    runtimeDirectory, dataDir);
                if (await PostgresSelectiveImport.HasPendingAsync(
                        postgresRuntimeSession.ConnectionString))
                {
                    await postgresRuntimeSession.DisposeAsync();
                    postgresRuntimeSession = null;
                    librarySessionLease.Dispose();
                    librarySessionLease = null;
                    try
                    {
                        await PostgresSelectiveImport.ApplyPendingAtStartupAsync(
                            runtimeDirectory, dataDir);
                    }
                    catch (Exception error)
                    {
                        if (PostgresStorageModeGuard.Current(dataDir) !=
                            PostgresStorageModeGuard.StorageMode.PostgreSql) throw;
                        importWarning = "数据库选择性导入未完成，待办仍可取消或重新预览：" + error.Message;
                    }
                    librarySessionLease = LibraryBackupService.AcquireSessionLease(dataDir);
                    postgresRuntimeSession = await PostgresLibraryRuntimeSession.OpenAsync(
                        runtimeDirectory, dataDir);
                }
                string backupWarning = importWarning;
                try
                {
                    await PostgresBackupPlanStore.RunAtStartupAsync(
                        postgresRuntimeSession.ConnectionString, dataDir,
                        postgresRuntimeSession.Config.Folders);
                }
                catch (Exception error) { backupWarning = "数据库备份未完成，待办已保留：" + error.Message; }
                var databaseWindow = new MainWindow(postgresRuntimeSession);
                MainWindow = databaseWindow;
                databaseWindow.ReconcileStartupRegistration();
                databaseWindow.ApplyStartupWindowPreferences(
                    e.Args.Contains("--auto-start", StringComparer.OrdinalIgnoreCase));
                if (backupWarning.Length > 0)
                    databaseWindow.Loaded += (_, _) => databaseWindow.Dispatcher.BeginInvoke(new Action(() =>
                        System.Windows.MessageBox.Show(databaseWindow, backupWarning,
                            "映格视频库 · 数据库备份", MessageBoxButton.OK, MessageBoxImage.Warning)));
                databaseWindow.Show();
                return;
            }
            catch (Exception error)
            {
                if (postgresRuntimeSession is not null)
                {
                    await postgresRuntimeSession.DisposeAsync();
                    postgresRuntimeSession = null;
                }
                librarySessionLease?.Dispose();
                librarySessionLease = null;
                System.Windows.MessageBox.Show("PostgreSQL 视频库未能安全载入；旧文件库未被打开。\n" + error.Message,
                    "映格视频库 · 存储模式", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
                return;
            }
        }
        if (storageMode != PostgresStorageModeGuard.StorageMode.LegacyFiles)
        {
            System.Windows.MessageBox.Show("无法确认视频库的存储标记或恢复状态，已停止启动以避免资料分叉。请核对数据库状态与备份后再重试。",
                "映格视频库 · 存储模式", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
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
        if (!PostgresStorageModeGuard.LegacyFilesMayOpen(dataDir))
        {
            librarySessionLease?.Dispose();
            librarySessionLease = null;
            System.Windows.MessageBox.Show("视频库存储模式已切换，当前文件版已停止启动。",
                "映格视频库 · 存储模式", MessageBoxButton.OK, MessageBoxImage.Warning);
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


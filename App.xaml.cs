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
    internal bool SystemSessionEnding { get; private set; }
    internal bool RecoveryShutdownRequested { get; private set; }

    internal void ShutdownForRecovery(int exitCode)
    {
        RecoveryShutdownRequested = true;
        Shutdown(exitCode);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        SystemSessionEnding = true;
        base.OnSessionEnding(e);
    }

    static bool IsLibraryLeaseConflict(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is System.IO.IOException io && (io.HResult & 0xFFFF) is 32 or 33)
                return true;
        return false;
    }

    async Task<PostgresLibraryRuntimeSession?> OpenDatabaseOrActivateAsync(
        string runtimeDirectory, string dataDirectory)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return await PostgresLibraryRuntimeSession.OpenAsync(runtimeDirectory,
                    dataDirectory);
            }
            catch (Exception error) when (IsLibraryLeaseConflict(error) &&
                                          elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                // If the old window still exists, wake it. If it is already
                // closing, wait for its PostgreSQL owner lease to be released
                // and continue opening this process instead of showing an
                // incorrect "database unavailable" dialog.
                if (SingleInstanceActivation.TryActivateExisting(dataDirectory, 250))
                    return null;
                await Task.Delay(200);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (postgresRuntimeSession is not null)
            {
                // WPF calls OnExit on its dispatcher thread. PostgreSQL shutdown is
                // asynchronous; blocking that dispatcher while its continuation is
                // queued there leaves a windowless process holding the library lock.
                var session = postgresRuntimeSession;
                postgresRuntimeSession = null;
                try
                {
                    Task.Run(async () => await session.DisposeAsync().ConfigureAwait(false))
                        .GetAwaiter().GetResult();
                }
                catch (Exception error)
                {
                    System.Diagnostics.Trace.WriteLine("PostgreSQL stop on exit failed: " + error);
                }
            }
        }
        finally
        {
            librarySessionLease?.Dispose();
            base.OnExit(e);
        }
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
        if (e.Args.Length > 4 && e.Args[0] == "--diagnose-r18-offline")
        {
            Shutdown(R18OfflineDiagnostics.Run(e.Args[1], e.Args[2], e.Args[3], e.Args[4]));
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
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-postgres-orphan")
        {
            Shutdown(await BundledPostgresRuntime.RunOrphanRecoveryToFileAsync(
                e.Args[1], e.Args[2]));
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--self-test-postgres-exit")
        {
            var isolatedDirectory = System.IO.Path.GetFullPath(e.Args[1]);
            if (isolatedDirectory.Equals(System.IO.Path.GetFullPath(AppPaths.DefaultDataDirectory),
                StringComparison.OrdinalIgnoreCase) ||
                !System.IO.File.Exists(System.IO.Path.Combine(isolatedDirectory,
                    "storage-postgresql.active")))
            {
                Shutdown(2);
                return;
            }
            postgresRuntimeSession = await PostgresLibraryRuntimeSession.OpenAsync(
                System.IO.Path.Combine(AppContext.BaseDirectory, "postgres"), isolatedDirectory);
            Shutdown();
            return;
        }
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-postgres-backup")
        {
            Shutdown(await PostgresBackupAcceptance.RunToFileAsync(e.Args[1], e.Args[2]));
            return;
        }
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-postgres-backup-one")
        {
            Shutdown(await PostgresBackupAcceptance.RunSingleCaseToFileAsync(e.Args[1], e.Args[2]));
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
        if (e.Args.Length > 2 && e.Args[0] == "--self-test-postgres-new-library")
        {
            Shutdown(await PostgresLibraryCutover.TestNewLibraryToFileAsync(e.Args[1], e.Args[2]));
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
        if (e.Args.Length > 1 && e.Args[0] == "--diagnose-ui-dialog")
        {
            Shutdown(ThemeMessageBoxDiagnostics.Run(e.Args[1]));
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
        // A previous window may be hidden in the notification area. Wake it
        // before touching migration, backup or database files.
        if (SingleInstanceActivation.TryActivateExisting(dataDir, 100))
        {
            Shutdown();
            return;
        }
        var postgresRuntimeDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "postgres");
        if (!AppPaths.IsRedirected &&
            (!System.IO.Directory.Exists(dataDir) ||
             !System.IO.Directory.EnumerateFileSystemEntries(dataDir).Any()))
        {
            try
            {
                var legacyCache = global::YinggeVideoLibrary.MainWindow.TryMigrateLegacyCaches(dataDir);
                if (legacyCache.Contains("稍后重试", StringComparison.Ordinal) ||
                    legacyCache.StartsWith("缓存迁移未完成", StringComparison.Ordinal))
                    throw new InvalidOperationException(legacyCache);
            }
            catch (Exception error)
            {
                YinggeVideoLibrary.ThemeMessageBox.Show("旧版缓存迁移未完成，尚未创建空数据库。\n" + error.Message,
                    "映格视频库 · 旧库迁移", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
                return;
            }
        }
        if (System.IO.File.Exists(System.IO.Path.Combine(dataDir,
                PostgresLibraryCutover.NewLibraryPendingName)) ||
            !System.IO.Directory.Exists(dataDir) ||
            !System.IO.Directory.EnumerateFileSystemEntries(dataDir).Any())
        {
            try
            {
                await PostgresLibraryCutover.InitializeNewAsync(postgresRuntimeDirectory, dataDir);
            }
            catch (Exception error)
            {
                if (IsLibraryLeaseConflict(error) &&
                    await SingleInstanceActivation.WaitForExistingAsync(dataDir,
                        TimeSpan.FromSeconds(10)))
                {
                    Shutdown();
                    return;
                }
                YinggeVideoLibrary.ThemeMessageBox.Show("新视频库的 PostgreSQL 初始化未完成。为避免写入两种存储，本次不载入视频库。\n" +
                    error.Message, "映格视频库 · 数据库初始化", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
                return;
            }
        }
        if (System.IO.File.Exists(System.IO.Path.Combine(dataDir, PostgresLibraryCutover.PendingName)))
        {
            try
            {
                await PostgresLibraryCutover.ApplyPendingAsync(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "postgres"), dataDir);
            }
            catch (Exception error)
            {
                if (IsLibraryLeaseConflict(error) &&
                    await SingleInstanceActivation.WaitForExistingAsync(dataDir,
                        TimeSpan.FromSeconds(10)))
                {
                    Shutdown();
                    return;
                }
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
                    YinggeVideoLibrary.ThemeMessageBox.Show("数据库转换未开始，已取消待办并继续载入原文件库。\n" + error.Message,
                        "映格视频库 · 数据库转换", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    YinggeVideoLibrary.ThemeMessageBox.Show("数据库转换已停止；旧资料文件未删除。为防止资料分叉，本次不载入视频库。\n" + error.Message,
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
                postgresRuntimeSession = await OpenDatabaseOrActivateAsync(runtimeDirectory,
                    dataDir);
                if (postgresRuntimeSession is null)
                {
                    librarySessionLease.Dispose();
                    librarySessionLease = null;
                    Shutdown();
                    return;
                }
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
                    postgresRuntimeSession = await OpenDatabaseOrActivateAsync(runtimeDirectory,
                        dataDir);
                    if (postgresRuntimeSession is null)
                    {
                        librarySessionLease.Dispose();
                        librarySessionLease = null;
                        Shutdown();
                        return;
                    }
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
                        YinggeVideoLibrary.ThemeMessageBox.Show(databaseWindow, backupWarning,
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
                if (IsLibraryLeaseConflict(error) &&
                    await SingleInstanceActivation.WaitForExistingAsync(dataDir,
                        TimeSpan.FromSeconds(10)))
                {
                    Shutdown();
                    return;
                }
                YinggeVideoLibrary.ThemeMessageBox.Show("PostgreSQL 视频库未能安全载入；旧文件库未被打开。\n" + error.Message,
                    "映格视频库 · 存储模式", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
                return;
            }
        }
        if (storageMode != PostgresStorageModeGuard.StorageMode.LegacyFiles)
        {
            YinggeVideoLibrary.ThemeMessageBox.Show("无法确认视频库的存储标记或恢复状态，已停止启动以避免资料分叉。请核对数据库状态与备份后再重试。",
                "映格视频库 · 存储模式", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        if (!LibraryRelocationService.ApplyPendingAtStartup(dataDir, out var restoreError))
        {
            YinggeVideoLibrary.ThemeMessageBox.Show("恢复已停止，视频库尚未加载。\n" + restoreError,
                "映格视频库 · 备份恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        if (!ReviewTransactionStore.RecoverPendingAtStartup(dataDir, out var reviewRecoveryError))
        {
            YinggeVideoLibrary.ThemeMessageBox.Show("审核操作恢复已停止，视频库尚未加载。\n" + reviewRecoveryError,
                "映格视频库 · 审核恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        if (!DuplicateMetadataTransactionStore.RecoverPendingAtStartup(dataDir, out var duplicateRecoveryError))
        {
            YinggeVideoLibrary.ThemeMessageBox.Show("重复视频资料合并恢复已停止，视频库尚未加载。\n" + duplicateRecoveryError,
                "映格视频库 · 资料恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        if (!LibraryBackupService.ApplyPendingImportAtStartup(dataDir, out var importError))
        {
            YinggeVideoLibrary.ThemeMessageBox.Show("选择性导入已停止，视频库尚未加载。\n" + importError,
                "映格视频库 · 数据导入", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        if (!LibraryBackupService.ApplyPendingOfflineBackupAtStartup(dataDir, out var backupError))
        {
            YinggeVideoLibrary.ThemeMessageBox.Show(backupError, "映格视频库 · 一致性备份",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        if (System.IO.File.Exists(System.IO.Path.Combine(postgresRuntimeDirectory, "bin", "postgres.exe")) &&
            new[] { "config.json", "library-index.json", "metadata.json" }.All(name =>
                System.IO.File.Exists(System.IO.Path.Combine(dataDir, name))))
        {
            try
            {
                var cacheMigration = global::YinggeVideoLibrary.MainWindow.TryMigrateLegacyCaches(dataDir);
                if (cacheMigration.Contains("稍后重试", StringComparison.Ordinal) ||
                    cacheMigration.StartsWith("缓存迁移未完成", StringComparison.Ordinal))
                    throw new InvalidOperationException(cacheMigration);
                await PostgresLibraryCutover.ConvertAsync(postgresRuntimeDirectory, dataDir);
                var executable = Environment.ProcessPath ??
                    throw new InvalidOperationException("无法定位程序文件以重新载入数据库。");
                var restart = new System.Diagnostics.ProcessStartInfo(executable)
                {
                    UseShellExecute = true
                };
                foreach (var argument in e.Args) restart.ArgumentList.Add(argument);
                System.Diagnostics.Process.Start(restart);
                Shutdown();
                return;
            }
            catch (Exception error)
            {
                if (IsLibraryLeaseConflict(error) &&
                    await SingleInstanceActivation.WaitForExistingAsync(dataDir,
                        TimeSpan.FromSeconds(10)))
                {
                    Shutdown();
                    return;
                }
                YinggeVideoLibrary.ThemeMessageBox.Show("自动转换为 PostgreSQL 未完成。旧资料文件未删除；为避免两种存储分叉，本次不载入视频库。\n" +
                    error.Message, "映格视频库 · 数据库转换", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
                return;
            }
        }
        try { librarySessionLease = LibraryBackupService.AcquireSessionLease(dataDir); }
        catch (Exception ex)
        {
            YinggeVideoLibrary.ThemeMessageBox.Show("无法锁定共享视频库数据，请稍后重试。\n" + ex.Message,
                "映格视频库 · 数据保护", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        if (!PostgresStorageModeGuard.LegacyFilesMayOpen(dataDir))
        {
            librarySessionLease?.Dispose();
            librarySessionLease = null;
            YinggeVideoLibrary.ThemeMessageBox.Show("视频库存储模式已切换，当前文件版已停止启动。",
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
                YinggeVideoLibrary.ThemeMessageBox.Show(window,
                    backupError + "\n备份安排已保留，下次启动会重试；也可以在设置中取消。",
                    "映格视频库 · 一致性备份", MessageBoxButton.OK, MessageBoxImage.Warning)));
        window.Show();
    }
}


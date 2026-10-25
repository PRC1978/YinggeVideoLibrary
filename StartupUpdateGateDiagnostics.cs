using Microsoft.Win32;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Forms = System.Windows.Forms;

namespace YinggeVideoLibrary;

internal static class StartupUpdateGateDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var findings = new Dictionary<string, bool>(StringComparer.Ordinal);
        var testKey = @"Software\YinggeVideoLibrary\StartupGate-" + Guid.NewGuid().ToString("N");
        const string testValue = "YinggeStartupGateOnly";
        var executableDirectory = Path.Combine(directory, "a folder with spaces");
        Directory.CreateDirectory(executableDirectory);
        var newExecutable = Path.Combine(executableDirectory, "映格视频库.exe");
        var anotherExecutable = Path.Combine(executableDirectory, "另一份映格.exe");
        var missingExecutable = Path.Combine(executableDirectory, "已移走的映格.exe");
        File.WriteAllText(newExecutable, "synthetic executable path; never launched");
        File.WriteAllText(anotherExecutable, "synthetic alternate path; never launched");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(testKey, writable: true)
                ?? throw new IOException("无法创建隔离的启动测试键"))
            {
                key.SetValue("Unrelated", "retain", RegistryValueKind.String);
                key.SetValue(testValue, missingExecutable, RegistryValueKind.String);
            }

            findings["movedExecutableRepairsMissingRunTarget"] =
                StartupRegistration.ReconcileInKey(true, newExecutable, testKey, testValue).Length == 0
                && Registry.GetValue(@"HKEY_CURRENT_USER\" + testKey, testValue, null) is string repaired
                && repaired == $"\"{newExecutable}\" --auto-start"
                && StartupRegistration.RegisteredExecutableFromKey(testKey, testValue) == newExecutable;

            using (var key = Registry.CurrentUser.OpenSubKey(testKey, writable: true)
                ?? throw new IOException("隔离的启动测试键意外消失"))
                key.SetValue(testValue, $"\"{anotherExecutable}\" --auto-start", RegistryValueKind.String);
            var alternateMessage = StartupRegistration.ReconcileInKey(true, newExecutable, testKey, testValue);
            findings["anotherUsableCopyIsNotSilentlyReplaced"] = alternateMessage.Contains("另一份", StringComparison.Ordinal)
                && StartupRegistration.RegisteredExecutableFromKey(testKey, testValue) == anotherExecutable;

            using (var key = Registry.CurrentUser.OpenSubKey(testKey, writable: true)
                ?? throw new IOException("隔离的启动测试键意外消失"))
                key.SetValue(testValue, missingExecutable, RegistryValueKind.String);
            findings["invalidExecutableCannotRegister"] =
                StartupRegistration.ReconcileInKey(true, missingExecutable, testKey, testValue).Length > 0
                && StartupRegistration.RegisteredExecutableFromKey(testKey, testValue) == missingExecutable;

            findings["disablingAutostartDeletesOnlyItsValue"] =
                StartupRegistration.ReconcileInKey(false, null, testKey, testValue).Length == 0
                && Registry.GetValue(@"HKEY_CURRENT_USER\" + testKey, testValue, null) is null
                && Equals(Registry.GetValue(@"HKEY_CURRENT_USER\" + testKey, "Unrelated", null), "retain");

            findings["quotedAndLegacyRunCommandsParse"] =
                StartupRegistration.ParseExecutable($"\"{newExecutable}\" --auto-start") == newExecutable
                && StartupRegistration.ParseExecutable(newExecutable) == newExecutable
                && StartupRegistration.ParseExecutable("invalid command") is null;

            var pages = new[] { "all", "recent", "favorites", "recommendations", "categories",
                "folders", "status", "scrape", "online", "settings" };
            findings["allStartupPagesRemainSelectable"] = pages.All(page => MainWindow.NormalizeStartupPage(page) == page)
                && MainWindow.NormalizeStartupPage("unknown") == "all";
            findings["loginMinimizeNeedsAllThreeChoices"] = MainWindow.ShouldStartMinimized(true, true, true)
                && !MainWindow.ShouldStartMinimized(false, true, true)
                && !MainWindow.ShouldStartMinimized(true, false, true)
                && !MainWindow.ShouldStartMinimized(true, true, false);
            findings["trayHidesOnlyRequestedMinimizedWindow"] =
                MainWindow.ShouldHideInTray(true, WindowState.Minimized, true, false)
                && !MainWindow.ShouldHideInTray(true, WindowState.Normal, true, false)
                && !MainWindow.ShouldHideInTray(true, WindowState.Minimized, false, false)
                && !MainWindow.ShouldHideInTray(false, WindowState.Minimized, true, false)
                && !MainWindow.ShouldHideInTray(true, WindowState.Minimized, true, true);

            var restored = 0;
            var exited = 0;
            using (var menu = MainWindow.CreateTrayContextMenu(() => restored++, () => exited++))
            {
                findings["trayRestoreAndExitAreIndependent"] = menu.Items.Count == 2
                    && menu.Items[0] is Forms.ToolStripMenuItem restoreItem
                    && menu.Items[1] is Forms.ToolStripMenuItem exitItem
                    && restoreItem.Text == "打开映格" && exitItem.Text == "退出映格"
                    && CheckTrayActions(restoreItem, exitItem, () => restored, () => exited);
            }

            foreach (var (name, promptPassed) in MainWindow.VerifyUpdatePromptForDiagnostics(
                Path.Combine(directory, "update-prompt")))
                findings["update." + name] = promptPassed;

            var currentExecutable = Environment.ProcessPath ?? "";
            var isolatedTarget = Path.Combine(directory, "same-version-target.exe");
            if (PublishedExecutable.IsThisApplication(currentExecutable))
            {
                File.Copy(currentExecutable, isolatedTarget);
                var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(currentExecutable)));
                var resultPath = Path.Combine(directory, "same-version-result.txt");
                var rejected = PortableUpdateWorker.Apply([
                    "--test-apply-update", isolatedTarget, Environment.ProcessId.ToString(), expectedHash, resultPath]);
                findings["update.sameVersionCannotOverwriteOrRestart"] = rejected == 1
                    && File.ReadAllText(resultPath).Contains("更新失败", StringComparison.Ordinal)
                    && PortableUpdateWorker.MatchesHash(isolatedTarget, expectedHash)
                    && !File.Exists(isolatedTarget + ".update-previous");
            }
            else findings["update.sameVersionCannotOverwriteOrRestart"] = false;

            var workerPath = Path.Combine(directory, "updater-self-test.json");
            var workerExitCode = PortableUpdateWorker.SelfTest(workerPath);
            using var workerReport = JsonDocument.Parse(File.ReadAllText(workerPath));
            findings["update.verifiedReplacementAndRollback"] = workerExitCode == 0
                && workerReport.RootElement.GetProperty("passed").GetBoolean()
                && workerReport.RootElement.GetProperty("rollbackVerified").GetBoolean()
                && workerReport.RootElement.GetProperty("installedVersionUpdated").GetBoolean();
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false); } catch { }
        }
        return findings;
    }

    static bool CheckTrayActions(Forms.ToolStripMenuItem restoreItem, Forms.ToolStripMenuItem exitItem,
        Func<int> restored, Func<int> exited)
    {
        restoreItem.PerformClick();
        var openOnly = restored() == 1 && exited() == 0;
        exitItem.PerformClick();
        return openOnly && restored() == 1 && exited() == 1;
    }

    internal static int RunToFile(string destination)
    {
        var directory = Path.Combine(Path.GetTempPath(), "YinggeStartupGate-" + Guid.NewGuid().ToString("N"));
        var findings = new Dictionary<string, object>(StringComparer.Ordinal);
        try
        {
            foreach (var (name, casePassed) in Verify(directory)) findings[name] = casePassed;
            findings["passed"] = findings.Values.OfType<bool>().All(value => value);
            findings["notExercised"] = new[]
            {
                "Actual Windows login and tray notification area interaction require a visible user session.",
                "The current user's real Run value and desktop shortcut are intentionally untouched.",
                "Successful replacement and hidden restart are covered separately by --self-test-update-transition with an older published binary."
            };
        }
        catch (Exception ex)
        {
            findings["passed"] = false;
            findings["error"] = ex.ToString();
        }
        finally
        {
            try
            {
                // This exact fresh test directory contains only files created by this run.
                var full = Path.GetFullPath(directory);
                var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (full.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(full).StartsWith("YinggeStartupGate-", StringComparison.Ordinal)
                    && Directory.Exists(full))
                    Directory.Delete(full, recursive: true);
            }
            catch { }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        File.WriteAllText(destination, JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true }));
        return findings.TryGetValue("passed", out var passed) && passed is true ? 0 : 1;
    }
}

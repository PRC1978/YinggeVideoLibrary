using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace YinggeVideoLibrary;

/// <summary>Runs from the verified new executable after the old process exits.</summary>
internal static class PortableUpdateWorker
{
    internal static int Apply(string[] args)
    {
        var testOnly = args.Length > 0 && args[0] == "--test-apply-update";
        var resultFile = testOnly && args.Length == 5 ? args[4] : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".YinggeVideoLibrary", "updates", "last-update-result.txt");
        string? target = null;
        var previousExited = false;
        var replacementCompleted = false;
        try
        {
            if (args.Length != (testOnly ? 5 : 4) || !int.TryParse(args[2], out var oldPid) || oldPid <= 0)
                throw new InvalidDataException("更新参数无效");
            target = Path.GetFullPath(args[1]);
            var source = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("无法找到更新程序"));
            var expectedHash = args[3];
            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)
                || source.Equals(target, StringComparison.OrdinalIgnoreCase)
                || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
                throw new InvalidDataException("更新目标或校验值无效");
            if (!MatchesHash(source, expectedHash)) throw new InvalidDataException("更新文件校验失败");

            try
            {
                using var previous = Process.GetProcessById(oldPid);
                if (!previous.WaitForExit(45000)) throw new IOException("旧版程序没有退出，请关闭所有映格视频库窗口后重试");
            }
            catch (ArgumentException) { /* The old process has already exited. */ }
            previousExited = true;

            ReplaceVerifiedFile(source, target, expectedHash);
            replacementCompleted = true;
            Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
            File.WriteAllText(resultFile, $"更新成功：{DateTimeOffset.Now:O}");
            if (!testOnly)
            {
                _ = Process.Start(new ProcessStartInfo(target)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(target)!
                }) ?? throw new IOException("新版程序未能启动");
                UpdateInstalledVersion(source, target);
            }
            return 0;
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            if (replacementCompleted && target is not null)
            {
                try { RestorePreviousFile(target); message += "；已恢复旧版"; }
                catch (Exception rollbackError) { message += "；恢复旧版失败：" + rollbackError.Message; }
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
                File.WriteAllText(resultFile, $"更新失败：{message}");
            }
            catch { }
            if (!testOnly && previousExited && target is not null && File.Exists(target))
                try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
            return 1;
        }
    }

    internal static int SelfTest(string destination)
    {
        var directory = Path.Combine(Path.GetTempPath(), "YinggeUpdateTest-" + Guid.NewGuid().ToString("N"));
        var testUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\YinggeVideoLibrarySelfTest-"
            + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(directory);
            var oldFile = Path.Combine(directory, "映格视频库.exe");
            var newFile = Path.Combine(directory, "downloaded.exe");
            File.WriteAllText(oldFile, "old-version");
            File.WriteAllText(newFile, "new-version");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(newFile)));
            try { ReplaceVerifiedFile(newFile, oldFile, new string('0', 64)); throw new InvalidDataException("错误的校验值未阻止替换"); }
            catch (InvalidDataException ex) when (ex.Message == "复制更新文件后校验失败") { }
            if (File.ReadAllText(oldFile) != "old-version") throw new InvalidDataException("校验失败却修改了旧版");
            ReplaceVerifiedFile(newFile, oldFile, hash);
            if (File.ReadAllText(oldFile) != "new-version"
                || File.ReadAllText(oldFile + ".update-previous") != "old-version")
                throw new InvalidDataException("替换或旧版备份结果不正确");
            RestorePreviousFile(oldFile);
            if (File.ReadAllText(oldFile) != "old-version")
                throw new InvalidDataException("更新失败时不能恢复旧版");
            // `dotnet app.dll` uses dotnet.exe as ProcessPath, whose FileVersion is
            // not guaranteed to be a parseable System.Version. Prefer this app's exe.
            var versionSource = Path.Combine(AppContext.BaseDirectory, "映格视频库.exe");
            if (!File.Exists(versionSource))
                versionSource = Environment.ProcessPath ?? throw new InvalidOperationException("找不到测试程序路径");
            var versionText = FileVersionInfo.GetVersionInfo(versionSource).FileVersion;
            if (!Version.TryParse(versionText, out var sourceVersion))
                throw new InvalidDataException($"测试程序没有有效文件版本号：{versionSource}；{versionText}");
            using (var uninstall = Registry.CurrentUser.CreateSubKey(testUninstallKey, true)
                ?? throw new IOException("无法创建隔离的更新测试记录"))
            {
                uninstall.SetValue("DisplayVersion", "0.0.0");
                uninstall.SetValue("InstallLocation", Path.Combine(directory, "other-install"));
                UpdateInstalledVersion(versionSource, oldFile, testUninstallKey);
                if (!Equals(uninstall.GetValue("DisplayVersion"), "0.0.0"))
                    throw new InvalidDataException("更新修改了其他安装目录的版本记录");
                uninstall.SetValue("InstallLocation", directory);
                UpdateInstalledVersion(versionSource, oldFile, testUninstallKey);
                if (!Equals(uninstall.GetValue("DisplayVersion"), sourceVersion.ToString(3)))
                    throw new InvalidDataException("安装目录更新后未同步版本记录");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            File.WriteAllText(destination, JsonSerializer.Serialize(new { passed = true, badHashRejected = true, replacementVerified = true,
                previousVersionPreserved = true, rollbackVerified = true, otherInstallVersionPreserved = true, installedVersionUpdated = true }));
            return 0;
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            File.WriteAllText(destination, JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
            return 1;
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(testUninstallKey, false); } catch { }
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }

    internal static void ReplaceVerifiedFile(string source, string target, string expectedHash)
    {
        var next = target + ".update-next";
        var backup = target + ".update-previous";
        try
        {
            File.Copy(source, next, true);
            if (!MatchesHash(next, expectedHash)) throw new InvalidDataException("复制更新文件后校验失败");
            if (File.Exists(backup)) File.Delete(backup);
            Exception? lastFailure = null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    File.Replace(next, target, backup, true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lastFailure = ex;
                    Thread.Sleep(750);
                }
            }
            throw new IOException("无法替换正在使用的程序文件，请关闭其他实例，或使用安装包更新", lastFailure);
        }
        finally { try { if (File.Exists(next)) File.Delete(next); } catch { } }
    }

    static void RestorePreviousFile(string target)
    {
        var backup = target + ".update-previous";
        if (!File.Exists(backup)) throw new FileNotFoundException("找不到旧版备份", backup);
        var restore = target + ".rollback-next";
        try
        {
            File.Copy(backup, restore, true);
            File.Replace(restore, target, null, true);
        }
        finally { try { if (File.Exists(restore)) File.Delete(restore); } catch { } }
    }

    internal static bool MatchesHash(string path, string expectedHash)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    static void UpdateInstalledVersion(string source, string target, string? uninstallKeyName = null)
    {
        try
        {
            using var uninstall = Registry.CurrentUser.OpenSubKey(
                uninstallKeyName ?? @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PRC1978.YinggeVideoLibrary_is1", true);
            var installedDirectory = uninstall?.GetValue("InstallLocation") as string;
            if (uninstall is not null && !string.IsNullOrWhiteSpace(installedDirectory)
                && Path.GetFullPath(Path.Combine(installedDirectory, "映格视频库.exe"))
                    .Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                var fileVersion = FileVersionInfo.GetVersionInfo(source).FileVersion;
                if (Version.TryParse(fileVersion, out var version))
                    uninstall.SetValue("DisplayVersion", version.ToString(3));
            }
        }
        catch { /* A portable copy has no uninstall entry. */ }
    }
}

using Microsoft.Win32;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace YinggeVideoLibrary;

internal static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "YinggeVideoLibrary";

    internal static string? RegisteredExecutable()
        => RegisteredExecutableFromKey(RunKey, ValueName);

    internal static string? RegisteredExecutableFromKey(string runKey, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKey);
        return ParseExecutable(key?.GetValue(valueName) as string);
    }

    // The old build stored an unquoted path. Accept it for migration, while
    // writing only a quoted path and a dedicated login-start argument.
    internal static string? ParseExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var value = command.Trim();
        if (value.StartsWith('"'))
        {
            var closing = value.IndexOf('"', 1);
            return closing > 1 ? value[1..closing] : null;
        }
        var match = Regex.Match(value, @"^(.+?\.exe)(?:\s|$)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static bool TrySetEnabled(bool enabled, out string error)
    {
        if (!enabled) return TrySetEnabledInKey(false, null, RunKey, ValueName, out error);
        if (!PublishedExecutable.TryGetRunningPath(out var executable, out error)) return false;
        return TrySetEnabledInKey(true, executable, RunKey, ValueName, out error);
    }

    static bool TrySetEnabledInKey(bool enabled, string? executable, string runKey,
        string valueName, out string error)
    {
        error = "";
        try
        {
            if (!enabled)
            {
                using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
                key?.DeleteValue(valueName, throwOnMissingValue: false);
                return true;
            }
            if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable)
                || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || executable.Contains('"') || !File.Exists(executable))
                throw new IOException("开机自启程序路径无效。");
            using var writableKey = Registry.CurrentUser.CreateSubKey(runKey, writable: true)
                ?? throw new IOException("无法写入当前用户的开机启动项。");
            writableKey.SetValue(valueName, $"\"{Path.GetFullPath(executable)}\" --auto-start", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // Do not silently replace another usable copy of Yingge. Once an old
    // executable is gone (for example after moving or updating the app), the
    // next normal launch repairs the login entry to this executable.
    internal static string Reconcile(bool enabled)
    {
        try
        {
            if (!enabled)
                return TrySetEnabled(false, out var disabledError) ? "" : disabledError;
            if (!PublishedExecutable.TryGetRunningPath(out var current, out var reason))
                return reason;
            return ReconcileInKey(true, current, RunKey, ValueName);
        }
        catch (Exception ex) { return ex.Message; }
    }

    // Diagnostics pass a unique HKCU test subkey and synthetic executable paths.
    // The normal entry point above always validates the running published binary.
    internal static string ReconcileInKey(bool enabled, string? current, string runKey, string valueName)
    {
        try
        {
            if (!enabled)
                return TrySetEnabledInKey(false, null, runKey, valueName, out var disabledError)
                    ? "" : disabledError;
            var registered = RegisteredExecutableFromKey(runKey, valueName);
            if (!string.IsNullOrWhiteSpace(registered) && File.Exists(registered)
                && !string.Equals(Path.GetFullPath(registered), current, StringComparison.OrdinalIgnoreCase))
                return "开机自启仍指向另一份可用的映格；可在启动设置中改为当前程序。";
            return TrySetEnabledInKey(true, current, runKey, valueName, out var enabledError)
                ? "" : enabledError;
        }
        catch (Exception ex) { return ex.Message; }
    }

    internal static string CreateDesktopShortcut()
    {
        if (!PublishedExecutable.TryGetRunningPath(out var executable, out var reason))
            throw new IOException(reason);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
            throw new DirectoryNotFoundException("无法找到当前用户的桌面目录。");
        var destination = Path.Combine(desktop, "映格视频库.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new NotSupportedException("Windows 快捷方式服务不可用。");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new NotSupportedException("Windows 快捷方式服务不可用。");
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                null, shell, [destination])
                ?? throw new IOException("无法创建桌面快捷方式。");
            var shortcutType = shortcut.GetType();
            void Set(string name, string value) => shortcutType.InvokeMember(name,
                BindingFlags.SetProperty, null, shortcut, [value]);
            Set("TargetPath", executable);
            Set("WorkingDirectory", Path.GetDirectoryName(executable) ?? "");
            Set("IconLocation", executable + ",0");
            Set("Description", "打开映格视频库");
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            return destination;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}

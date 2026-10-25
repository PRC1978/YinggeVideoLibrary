using System.Diagnostics;
using System.IO;

namespace YinggeVideoLibrary;

internal static class PublishedExecutable
{
    internal static bool TryGetBinaryVersion(string? path, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
                || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                return false;
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.Equals(info.ProductName, "映格视频库", StringComparison.Ordinal)
                || !string.Equals(info.OriginalFilename, "映格视频库.dll", StringComparison.OrdinalIgnoreCase)
                || !Version.TryParse(info.FileVersion, out var parsed))
                return false;
            version = parsed;
            return true;
        }
        catch { return false; }
    }

    internal static bool IsReleaseBinary(string? path, Version expectedVersion) =>
        TryGetBinaryVersion(path, out var version) && version == expectedVersion;

    internal static bool IsThisApplication(string? path) =>
        IsReleaseBinary(path, typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0, 0));

    internal static bool TryGetRunningPath(out string path, out string error)
    {
        path = Environment.ProcessPath ?? "";
        if (IsThisApplication(path))
        {
            error = "";
            return true;
        }
        path = "";
        error = "此功能需要运行发布版映格视频库.exe；源码方式启动时不会修改 Windows 启动项或更新其他程序。";
        return false;
    }
}

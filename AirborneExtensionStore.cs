using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

/// <summary>
/// Owns the only extension folders that may be handed to
/// <c>CoreWebView2Profile.AddBrowserExtensionAsync</c>.
///
/// That API rewrites the folder it loads: it deletes the browser's
/// <c>_metadata</c> sidecar directory. Passing Edge's own store-installed
/// folder therefore silently modified Edge's profile (a controlled repro
/// dropped 98 files to 96), and passing a user-selected folder damaged the
/// folder the user picked. Every load now goes through a copy that 映格 owns:
/// <c>&lt;data-dir&gt;\extensions\airborne\&lt;path-key&gt;\&lt;version&gt;</c>.
///
/// The source folder is only ever read: files are copied out with
/// <see cref="File.Copy(string, string, bool)"/>, never moved, renamed or
/// deleted, and no timestamps are written back.
/// </summary>
internal static class AirborneExtensionStore
{
    internal const string StoreFolderName = "airborne";
    internal const string MetadataFolderName = "_metadata";
    internal const string ManifestFileName = "manifest.json";

    /// <summary>Number of managed versions kept per source folder.</summary>
    internal const int RetainedGenerations = 2;

    const string StagingMarker = ".staging-";

    /// <summary>Root of every managed copy: <c>&lt;data-dir&gt;\extensions\airborne</c>.</summary>
    internal static string ManagedRoot(string dataDir) =>
        Path.Combine(Path.GetFullPath(dataDir), "extensions", StoreFolderName);

    /// <summary>
    /// Copies <paramref name="sourceFolder"/> into the managed store and returns
    /// the copy path. Returns null with a user-facing <paramref name="error"/>
    /// when the source cannot be read or the copy cannot be verified.
    ///
    /// The copy skips Edge's <c>_metadata</c> directory (the folder the WebView2
    /// API would otherwise delete), is idempotent for an already verified
    /// version, and keeps at most <see cref="RetainedGenerations"/> versions.
    /// </summary>
    internal static string? EnsureManagedCopy(string sourceFolder, string dataDir, out string? error)
    {
        error = null;
        string source;
        try
        {
            if (string.IsNullOrWhiteSpace(sourceFolder))
            {
                error = "没有可用的扩展源目录。";
                return null;
            }
            source = Path.GetFullPath(sourceFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            error = $"扩展源目录路径无效（{ex.Message}）。";
            return null;
        }
        if (!Directory.Exists(source))
        {
            error = "扩展源目录不存在或不可访问，原目录未被改动。";
            return null;
        }

        var version = ReadManifestVersion(source, out var manifestError);
        if (version is null)
        {
            error = manifestError ?? "扩展源目录缺少可用的版本号。";
            return null;
        }

        Dictionary<string, string> payload;
        try { payload = HashPayload(source); }
        catch (Exception ex)
        {
            error = $"无法读取扩展源目录（{ex.Message}），原目录未被改动。";
            return null;
        }
        if (payload.Count == 0)
        {
            error = "扩展源目录中没有可复制的文件，原目录未被改动。";
            return null;
        }

        string generationRoot;
        string target;
        try
        {
            generationRoot = Path.Combine(ManagedRoot(dataDir), FolderKey(source));
            target = Path.Combine(generationRoot, version);
        }
        catch (Exception ex)
        {
            error = $"无法确定扩展副本位置（{ex.Message}）。";
            return null;
        }

        try
        {
            if (Directory.Exists(target))
            {
                // Same version already present: reuse only when every payload
                // file still matches the source byte for byte.
                if (MatchesPayload(target, payload))
                {
                    PruneGenerations(generationRoot, version);
                    return target;
                }
                // A damaged copy is discarded and rebuilt rather than loaded.
                Delete(target);
            }

            Directory.CreateDirectory(generationRoot);
            var staging = Path.Combine(generationRoot, version + StagingMarker + Guid.NewGuid().ToString("N"));
            try
            {
                CopyPayload(source, staging);
                if (!MatchesPayload(staging, payload))
                {
                    Delete(staging);
                    error = "扩展副本完整性校验失败（文件数或 SHA-256 与源目录不一致）；半成品已删除，原目录未被改动。";
                    return null;
                }
                Directory.Move(staging, target);
            }
            catch (Exception ex)
            {
                Delete(staging);
                error = $"复制扩展失败（{ex.Message}）；半成品已删除，原目录未被改动。";
                return null;
            }

            PruneGenerations(generationRoot, version);
            return target;
        }
        catch (Exception ex)
        {
            error = $"准备扩展副本失败（{ex.Message}）；原目录未被改动。";
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="folder"/> is inside
    /// <c>&lt;data-dir&gt;\extensions\airborne\</c>. Comparison uses full paths
    /// and ignores case and separator style, so a sibling directory that only
    /// shares a name prefix is rejected.
    /// </summary>
    internal static bool IsManagedPath(string? folder, string dataDir)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(dataDir)) return false;
        try
        {
            var root = Path.GetFullPath(ManagedRoot(dataDir))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// The load gate. <c>InstallAirborneExtensionAsync</c> rejects anything this
    /// returns false for, so an Edge folder or a user-selected folder can never
    /// reach <c>AddBrowserExtensionAsync</c>. Pure enough to assert in
    /// diagnostics without a WebView2 core.
    /// </summary>
    internal static bool ValidateFolderForLoad(string? folder, string dataDir) =>
        IsManagedPath(folder, dataDir) && Directory.Exists(folder);

    /// <summary>
    /// Relative path to SHA-256 for every file below <paramref name="folder"/>,
    /// <c>_metadata</c> included on purpose: diagnostics use it to prove that the
    /// source folder was not touched at all, not even its Edge sidecar.
    /// </summary>
    internal static Dictionary<string, string> SnapshotFingerprint(string folder)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(folder);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            result[Relative(root, file)] = HashFile(file);
        return result;
    }

    /// <summary>Payload files only (no <c>_metadata</c>), for copy verification.</summary>
    internal static Dictionary<string, string> HashPayload(string folder)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(folder);
        foreach (var file in EnumeratePayloadFiles(root))
            result[Relative(root, file)] = HashFile(file);
        return result;
    }

    /// <summary>Stable short key for a source path, safe for Chinese or spaced paths.</summary>
    internal static string FolderKey(string sourceFolder) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(sourceFolder).ToUpperInvariant())))[..16].ToLowerInvariant();

    /// <summary>Version directory names currently held for a source folder.</summary>
    internal static IReadOnlyList<string> ManagedGenerations(string sourceFolder, string dataDir)
    {
        try
        {
            var root = Path.Combine(ManagedRoot(dataDir), FolderKey(sourceFolder));
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).Contains(StagingMarker, StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return []; }
    }

    static IEnumerable<string> EnumeratePayloadFiles(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (Directory.Exists(entry))
            {
                // Edge's sidecar: the exact folder AddBrowserExtensionAsync deletes.
                if (string.Equals(Path.GetFileName(entry), MetadataFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var nested in EnumeratePayloadFiles(entry)) yield return nested;
            }
            else yield return entry;
        }
    }

    static void CopyPayload(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in EnumeratePayloadFiles(Path.GetFullPath(source)))
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(source), file);
            var target = Path.Combine(destination, relative);
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            // File.Copy is a pure read of the source; LastWriteTime of the source
            // is never written and nothing is moved or deleted there.
            File.Copy(file, target, overwrite: true);
        }
    }

    static bool MatchesPayload(string folder, Dictionary<string, string> expected)
    {
        if (!Directory.Exists(folder)) return false;
        Dictionary<string, string> actual;
        try { actual = HashPayload(folder); }
        catch { return false; }
        if (actual.Count != expected.Count) return false;
        foreach (var pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out var hash)) return false;
            if (!string.Equals(hash, pair.Value, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    static void PruneGenerations(string generationRoot, string keepVersion)
    {
        try
        {
            var stale = Directory.EnumerateDirectories(generationRoot, "*", SearchOption.TopDirectoryOnly)
                .Select(path => (Path: path, Name: Path.GetFileName(path)))
                .Where(item => !string.Equals(item.Name, keepVersion, StringComparison.OrdinalIgnoreCase) &&
                    !item.Name.Contains(StagingMarker, StringComparison.Ordinal))
                .Select(item => (item.Path, item.Name, Version: ParseVersion(item.Name)))
                .OrderByDescending(item => item.Version)
                .ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                // keepVersion is the newest generation; retain the rest up to the cap.
                .Skip(RetainedGenerations - 1)
                .ToList();
            foreach (var item in stale) Delete(item.Path);
        }
        catch { /* Housekeeping only: pruning must never fail a load. */ }
    }

    static Version ParseVersion(string value) =>
        Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0);

    static string? ReadManifestVersion(string folder, out string? error)
    {
        error = null;
        try
        {
            var file = Path.Combine(folder, ManifestFileName);
            if (!File.Exists(file))
            {
                error = "扩展源目录缺少 manifest.json，无法确定版本。";
                return null;
            }
            if (new FileInfo(file).Length > 256_000)
            {
                error = "扩展源目录的 manifest.json 过大，已拒绝处理。";
                return null;
            }
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("version", out var element) ||
                element.ValueKind != JsonValueKind.String)
            {
                error = "扩展源目录的 manifest.json 缺少 version 字段。";
                return null;
            }
            var raw = element.GetString() ?? "";
            var safe = new string(raw.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_').ToArray()).Trim('.');
            if (safe.Length == 0)
            {
                error = "扩展源目录的 manifest.json 版本号不可用。";
                return null;
            }
            return safe;
        }
        catch (Exception ex)
        {
            error = $"读取扩展清单失败（{ex.Message}），原目录未被改动。";
            return null;
        }
    }

    static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    static string HashFile(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* A locked leftover must not break the caller's flow. */ }
    }
}

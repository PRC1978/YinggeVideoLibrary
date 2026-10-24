using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Forms = System.Windows.Forms;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    const string AirborneEdgeId = "khkeolgobhdoloioehjgfpobjnmagfha";
    const string AirborneDefaultOptionsPage = "options/options.html";
    static readonly Regex ExtensionIdPattern = new("^[a-p]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    string? airborneWebViewId;
    string? airborneFolder;
    string? airborneOptionsPage;

    // Call after EnsureCoreWebView2Async and before navigating to bilibili.com.
    // Edge and WebView2 use separate profiles. First reuse the extension already
    // installed in this WebView2 profile; only consult Edge's files if missing.
    async Task<bool> EnsureAirborneExtensionAsync()
    {
        if (!config.EnableExtension)
        {
            ExtensionInfo.Text = "内嵌浏览器扩展已关闭；修改此项后请重新启动软件。";
            return false;
        }
        var core = OnlineWeb.CoreWebView2;
        if (core is null)
        {
            ExtensionInfo.Text = "内嵌浏览器尚未就绪，无法检查扩展。";
            return false;
        }
        try
        {
            var installed = (await core.Profile.GetBrowserExtensionsAsync())
                .FirstOrDefault(extension => IsAirborneExtension(extension));
            if (installed is not null)
            {
                if (!installed.IsEnabled) await installed.EnableAsync(true);
                if (!installed.IsEnabled)
                {
                    ExtensionInfo.Text = "已找到小电视空降助手，但启用失败；请检查 WebView2 运行环境。";
                    return false;
                }
                airborneWebViewId = installed.Id;
                airborneFolder = FindAirborneExtensionQuick();
                airborneOptionsPage = ReadAirborneManifest(airborneFolder)?.OptionsPage ?? AirborneDefaultOptionsPage;
                ExtensionInfo.Text = "小电视空降助手已在内嵌浏览器启用。与 Edge 的账号和扩展设置分开保存；若刚启用，请刷新哔哩哔哩页面。";
                return true;
            }

            var folder = FindAirborneExtensionQuick();
            if (folder is null)
            {
                ExtensionInfo.Text = "未找到小电视空降助手的解压目录。可在 Edge 安装后重启软件，或手动选择含 manifest.json 的扩展文件夹。";
                return false;
            }
            return await InstallAirborneExtensionAsync(core, folder);
        }
        catch (Exception ex)
        {
            ExtensionInfo.Text = "无法检查内嵌扩展：" + ex.Message;
            return false;
        }
    }

    async Task<bool> InstallAirborneExtensionAsync(CoreWebView2 core, string folder)
    {
        var manifest = ReadAirborneManifest(folder);
        if (manifest is null)
        {
            ExtensionInfo.Text = "所选文件夹不是有效的小电视空降助手扩展目录（应直接包含 manifest.json）。";
            return false;
        }
        try
        {
            var extension = await core.Profile.AddBrowserExtensionAsync(folder);
            if (!extension.IsEnabled) await extension.EnableAsync(true);
            if (!extension.IsEnabled)
            {
                ExtensionInfo.Text = "扩展已加入 WebView2，但未能启用。";
                return false;
            }
            airborneWebViewId = extension.Id;
            airborneFolder = folder;
            airborneOptionsPage = manifest.OptionsPage;
            ExtensionInfo.Text = $"小电视空降助手 {manifest.Version} 已加入内嵌浏览器。WebView2 与 Edge 的登录状态、扩展设置相互独立；刷新哔哩哔哩后查看效果。";
            return true;
        }
        catch (Exception ex)
        {
            ExtensionInfo.Text = "扩展加载失败：" + ex.Message + "。可手动选择扩展的解压目录后重试。";
            return false;
        }
    }

    static bool IsAirborneExtension(CoreWebView2BrowserExtension extension) =>
        string.Equals(extension.Id, AirborneEdgeId, StringComparison.OrdinalIgnoreCase) ||
        extension.Name.Contains("小电视空降助手", StringComparison.OrdinalIgnoreCase);

    // No SearchOption.AllDirectories: Edge User Data can contain enormous
    // caches. Only probe the known extension ID beneath profile directories.
    static string? FindAirborneExtensionQuick()
    {
        var preference = Path.Combine(DataDir, "airborne-extension.path");
        try
        {
            if (File.Exists(preference))
            {
                var selected = File.ReadAllText(preference).Trim();
                if (ReadAirborneManifest(selected) is not null) return selected;
            }
        }
        catch { /* A stale preference must not prevent finding Edge's copy. */ }

        var edgeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data");
        if (!Directory.Exists(edgeRoot)) return null;
        var profiles = new List<string> { "Profile 1", "Default" };
        try
        {
            profiles.AddRange(Directory.EnumerateDirectories(edgeRoot, "Profile *", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).Where(name => name is not null).Select(name => name!).Take(32));
        }
        catch { /* Try the two common profile paths below. */ }
        foreach (var profile in profiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var versionsRoot = Path.Combine(edgeRoot, profile, "Extensions", AirborneEdgeId);
            try
            {
                if (!Directory.Exists(versionsRoot)) continue;
                var versions = Directory.EnumerateDirectories(versionsRoot, "*", SearchOption.TopDirectoryOnly)
                    .Select(path => (Path: path, Manifest: ReadAirborneManifest(path)))
                    .Where(item => item.Manifest is not null)
                    .OrderByDescending(item => ParseExtensionVersion(item.Manifest!.Version))
                    .Take(12);
                var latest = versions.FirstOrDefault();
                if (latest.Path is not null) return latest.Path;
            }
            catch { /* Another profile can still contain a usable version. */ }
        }
        return null;
    }

    static Version ParseExtensionVersion(string version) => Version.TryParse(version, out var result) ? result : new Version(0, 0);

    sealed record AirborneManifest(string Version, string OptionsPage);

    // Validate only the files needed for loading and opening settings. A user
    // chosen folder may contain a manifest without an Edge store identifier.
    static AirborneManifest? ReadAirborneManifest(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            var file = Path.Combine(folder, "manifest.json");
            if (!File.Exists(file) || new FileInfo(file).Length > 256_000) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            if (!root.TryGetProperty("manifest_version", out var manifestVersion) || manifestVersion.GetInt32() is not (2 or 3) ||
                !root.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("content_scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Array) return null;
            var hasBilibiliScript = scripts.EnumerateArray().Any(script =>
                script.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array &&
                matches.EnumerateArray().Any(match => match.ValueKind == JsonValueKind.String &&
                    match.GetString()!.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase)));
            if (!hasBilibiliScript) return null;
            var optionsPage = root.TryGetProperty("options_ui", out var optionsUi) &&
                optionsUi.TryGetProperty("page", out var options) && options.ValueKind == JsonValueKind.String
                ? options.GetString() : null;
            optionsPage ??= root.TryGetProperty("options_page", out var legacyOptions) && legacyOptions.ValueKind == JsonValueKind.String
                ? legacyOptions.GetString() : AirborneDefaultOptionsPage;
            if (!IsSafeExtensionPage(optionsPage)) return null;
            return new AirborneManifest(versionElement.GetString() ?? "", optionsPage!);
        }
        catch { return null; }
    }

    static bool IsSafeExtensionPage(string? page) => !string.IsNullOrWhiteSpace(page) &&
        !Path.IsPathRooted(page) && !page.StartsWith('/') && !page.Contains(':') &&
        !page.Split('/').Any(segment => segment is ".." or ".") &&
        Regex.IsMatch(page, @"^[A-Za-z0-9_./-]+$");

    // User-initiated fallback when Edge's copy cannot be found or installed.
    async Task<bool> ChooseAirborneExtensionFolderAsync()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择小电视空降助手解压后的文件夹（该文件夹内应有 manifest.json）",
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return false;
        var folder = dialog.SelectedPath;
        if (ReadAirborneManifest(folder) is null)
        {
            ExtensionInfo.Text = "未找到有效的扩展清单。请选择直接包含 manifest.json 的文件夹。";
            return false;
        }
        if (OnlineWeb.CoreWebView2 is null)
        {
            ExtensionInfo.Text = "已选中扩展文件夹；请先打开“在线”页面以初始化内嵌浏览器。";
        }
        else if (config.EnableExtension && !await InstallAirborneExtensionAsync(OnlineWeb.CoreWebView2, folder)) return false;
        else if (!config.EnableExtension) ExtensionInfo.Text = "扩展目录已保存。请启用内嵌扩展并重启软件。";
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(Path.Combine(DataDir, "airborne-extension.path"), folder);
        }
        catch (Exception ex) { ExtensionInfo.Text += " 目录偏好无法保存：" + ex.Message; }
        return true;
    }

    string? AirborneOptionsUrl()
    {
        if (!config.EnableExtension || airborneWebViewId is null || !ExtensionIdPattern.IsMatch(airborneWebViewId)) return null;
        var page = airborneOptionsPage ?? ReadAirborneManifest(airborneFolder)?.OptionsPage ?? AirborneDefaultOptionsPage;
        if (!IsSafeExtensionPage(page)) return null;
        return $"chrome-extension://{airborneWebViewId}/{page}#behavior";
    }

    void NavigateAirborneOptions()
    {
        var optionsUrl = AirborneOptionsUrl();
        if (optionsUrl is null || OnlineWeb.CoreWebView2 is null)
        {
            ExtensionInfo.Text = "小电视空降助手尚未在内嵌浏览器启用。请先打开“在线”并检查扩展状态。";
            return;
        }
        OnlineWeb.CoreWebView2.Navigate(optionsUrl);
    }
}

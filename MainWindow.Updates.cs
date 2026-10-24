using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    const string UpdateOwner = "PRC1978";
    const string UpdateRepository = "YinggeVideoLibrary";
    const string InstallerAssetName = "YinggeVideoLibrary-Setup.exe";
    static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{UpdateOwner}/{UpdateRepository}/releases/latest");
    static readonly HttpClient UpdateHttp = CreateUpdateHttpClient();
    System.Windows.Controls.Button? updateButton;
    TextBlock? updateStatus;
    ReleaseUpdate? availableUpdate;
    bool updateCheckRunning;
    bool updateInstallRunning;

    sealed record ReleaseUpdate(string Tag, Version Version, Uri DownloadUrl, long Size, string Sha256);

    static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("YinggeVideoLibrary/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    void InitializeUpdateUi()
    {
        if (SettingsPanel.Child is not StackPanel panel) return;
        panel.Children.Add(new Separator { Margin = new Thickness(0, 20, 0, 14) });
        panel.Children.Add(new TextBlock
        {
            Text = "软件更新", FontSize = 17, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 47, 75))
        });
        updateStatus = new TextBlock
        {
            Text = $"当前版本 {CurrentUpdateVersion()} · 通过 GitHub Releases 检查更新",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(105, 123, 150)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 9), FontSize = 12
        };
        panel.Children.Add(updateStatus);
        updateButton = new System.Windows.Controls.Button
        {
            Content = "检查更新", Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        updateButton.Click += async (_, _) =>
        {
            if (availableUpdate is null) await CheckForUpdatesAsync(true);
            else await DownloadAndInstallUpdateAsync(availableUpdate);
        };
        panel.Children.Add(updateButton);
    }

    static Version CurrentUpdateVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    async Task CheckForUpdatesAsync(bool interactive)
    {
        if (updateCheckRunning || updateInstallRunning) return;
        if (!interactive)
        {
            try
            {
                var stamp = Path.Combine(DataDir, "last-update-check.txt");
                if (File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < TimeSpan.FromHours(12)) return;
            }
            catch { /* A failed timestamp read should not block the check. */ }
        }
        updateCheckRunning = true;
        if (updateButton is not null) updateButton.IsEnabled = false;
        if (interactive && updateStatus is not null) updateStatus.Text = "正在检查 GitHub 上的新版本…";
        try
        {
            using var response = await UpdateHttp.GetAsync(LatestReleaseApi);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                if (interactive && updateStatus is not null) updateStatus.Text = "暂未发布安装版，稍后再试。";
                return;
            }
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsedVersion))
                throw new InvalidDataException("发布版本号无效");
            var releaseVersion = new Version(parsedVersion.Major, parsedVersion.Minor,
                Math.Max(0, parsedVersion.Build), Math.Max(0, parsedVersion.Revision));
            var stampPath = Path.Combine(DataDir, "last-update-check.txt");
            File.WriteAllText(stampPath, DateTimeOffset.UtcNow.ToString("O"));
            if (releaseVersion <= CurrentUpdateVersion())
            {
                availableUpdate = null;
                if (updateStatus is not null) updateStatus.Text = $"当前已是最新版 {CurrentUpdateVersion()}";
                if (updateButton is not null) updateButton.Content = "检查更新";
                return;
            }
            if (!root.TryGetProperty("assets", out var assets))
                throw new InvalidDataException("新版本没有安装文件");
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != InstallerAssetName) continue;
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                var digest = asset.TryGetProperty("digest", out var digestNode) ? digestNode.GetString() ?? "" : "";
                var size = asset.GetProperty("size").GetInt64();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                    || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    || !uri.AbsolutePath.StartsWith($"/{UpdateOwner}/{UpdateRepository}/releases/download/", StringComparison.OrdinalIgnoreCase)
                    || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    || digest.Length != 71 || size < 1_000_000)
                    throw new InvalidDataException("安装文件的来源或校验值无效");
                availableUpdate = new ReleaseUpdate(tag, releaseVersion, uri, size, digest[7..]);
                if (updateStatus is not null) updateStatus.Text = $"发现新版本 {tag}，安装包约 {size / 1024 / 1024} MB。下载后会验证文件，再启动安装。";
                if (updateButton is not null) updateButton.Content = $"下载并安装 {tag}";
                Log($"发现软件更新 {tag}，可在设置中下载安装");
                return;
            }
            throw new InvalidDataException("新版本没有适用的 Windows 安装包");
        }
        catch (Exception ex)
        {
            if (interactive && updateStatus is not null) updateStatus.Text = "检查更新失败：" + ex.Message;
        }
        finally
        {
            updateCheckRunning = false;
            if (updateButton is not null) updateButton.IsEnabled = true;
        }
    }

    async Task DownloadAndInstallUpdateAsync(ReleaseUpdate release)
    {
        if (updateInstallRunning || updateCheckRunning) return;
        updateInstallRunning = true;
        if (updateButton is not null) updateButton.IsEnabled = false;
        var targetDir = Path.Combine(DataDir, "updates");
        var target = Path.Combine(targetDir, $"YinggeVideoLibrary-Setup-{release.Version}.exe");
        var temporary = target + ".part";
        try
        {
            Directory.CreateDirectory(targetDir);
            if (!File.Exists(target) || !await IsVerifiedUpdateAsync(target, release))
            {
                using var response = await UpdateHttp.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync())
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                {
                    var buffer = new byte[1024 * 1024];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read));
                        total += read;
                        if (updateStatus is not null) updateStatus.Text = $"下载 {release.Tag}：{total * 100 / release.Size}%";
                    }
                }
                if (!await IsVerifiedUpdateAsync(temporary, release))
                    throw new InvalidDataException("下载文件的大小或 SHA-256 校验不匹配");
                File.Move(temporary, target, true);
            }
            if (updateStatus is not null) updateStatus.Text = "校验通过，正在启动安装程序…";
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            if (updateStatus is not null) updateStatus.Text = "更新失败：" + ex.Message;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            updateInstallRunning = false;
            if (updateButton is not null) updateButton.IsEnabled = true;
        }
    }

    static async Task<bool> IsVerifiedUpdateAsync(string path, ReleaseUpdate release)
    {
        try
        {
            if (new FileInfo(path).Length != release.Size) return false;
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            return hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

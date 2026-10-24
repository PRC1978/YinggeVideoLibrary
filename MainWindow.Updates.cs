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
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    const string UpdateOwner = "PRC1978";
    const string UpdateRepository = "YinggeVideoLibrary";
    const string InstallerAssetName = "YinggeVideoLibrary-Setup.exe";
    const string PortableAssetName = "YinggeVideoLibrary-win-x64.exe";
    static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{UpdateOwner}/{UpdateRepository}/releases/latest");
    static readonly HttpClient UpdateHttp = CreateUpdateHttpClient();
    System.Windows.Controls.Button? updateButton;
    TextBlock? updateStatus;
    ReleaseUpdate? availableUpdate;
    bool updateCheckRunning;
    bool updateInstallRunning;

    sealed record ReleaseUpdate(string Tag, Version Version, Uri DownloadUrl, long Size, string Sha256, bool Portable);

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
        var section = new StackPanel { Name = "SettingsUpdateOptions", Margin = new Thickness(0, 16, 0, 8) };
        section.Children.Add(new TextBlock
        {
            Text = "软件更新", FontSize = 17, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 47, 75))
        });
        updateStatus = new TextBlock
        {
            Text = $"当前版本 {CurrentUpdateVersion()} · 从 GitHub Releases 获取更新",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(105, 123, 150)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 9), FontSize = 12
        };
        try
        {
            var resultPath = Path.Combine(DataDir, "updates", "last-update-result.txt");
            if (File.Exists(resultPath))
            {
                var result = File.ReadAllText(resultPath);
                if (result.StartsWith("更新失败：", StringComparison.Ordinal))
                {
                    updateStatus.Text = result;
                    File.Delete(resultPath);
                }
                else if (result.StartsWith("更新成功：", StringComparison.Ordinal))
                {
                    var clean = true;
                    foreach (var oldDownload in Directory.EnumerateFiles(Path.GetDirectoryName(resultPath)!, "YinggeVideoLibrary-win-x64-*.exe"))
                        try { File.Delete(oldDownload); } catch { clean = false; /* The helper may still be running. */ }
                    if (clean) File.Delete(resultPath);
                }
            }
        }
        catch { /* The update result is advisory; startup must continue. */ }
        section.Children.Add(updateStatus);
        var autoCheck = new CheckBox
        {
            Content = "启动后自动检查更新",
            IsChecked = config.UpdateAutoCheck,
            Margin = new Thickness(0, 2, 0, 10)
        };
        autoCheck.Checked += (_, _) => { config.UpdateAutoCheck = true; SaveConfig(); };
        autoCheck.Unchecked += (_, _) => { config.UpdateAutoCheck = false; SaveConfig(); };
        section.Children.Add(autoCheck);
        var intervalRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        intervalRow.Children.Add(new TextBlock { Text = "自动检查间隔", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var intervals = new[] { 1, 6, 12, 24, 72, 168 }.Append(config.UpdateCheckIntervalHours).Distinct().Order().ToArray();
        var interval = new ComboBox { Width = 90, Height = 34, ItemsSource = intervals, SelectedItem = config.UpdateCheckIntervalHours };
        interval.SelectionChanged += (_, _) =>
        {
            if (interval.SelectedItem is not int hours || hours == config.UpdateCheckIntervalHours) return;
            config.UpdateCheckIntervalHours = hours;
            SaveConfig();
        };
        intervalRow.Children.Add(interval);
        intervalRow.Children.Add(new TextBlock { Text = "小时", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        section.Children.Add(intervalRow);
        updateButton = new System.Windows.Controls.Button
        {
            Content = "检查更新", Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        updateButton.Click += async (_, _) =>
        {
            if (availableUpdate is null) await CheckForUpdatesAsync(true);
            else await DownloadAndApplyUpdateAsync(availableUpdate);
        };
        section.Children.Add(updateButton);
        panel.Children.Add(section);
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
            if (!config.UpdateAutoCheck) return;
            try
            {
                var stamp = Path.Combine(DataDir, "last-update-check.txt");
                var interval = TimeSpan.FromHours(Math.Clamp(config.UpdateCheckIntervalHours, 1, 168));
                if (File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < interval) return;
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
                if (interactive && updateStatus is not null) updateStatus.Text = "暂未发布新版本，稍后再试。";
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
            ReleaseUpdate? portable = null;
            ReleaseUpdate? installer = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (name != PortableAssetName && name != InstallerAssetName) continue;
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                var digest = asset.TryGetProperty("digest", out var digestNode) ? digestNode.GetString() ?? "" : "";
                var size = asset.GetProperty("size").GetInt64();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                    || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    || !uri.AbsolutePath.StartsWith($"/{UpdateOwner}/{UpdateRepository}/releases/download/", StringComparison.OrdinalIgnoreCase)
                    || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    || digest.Length != 71 || size < 1_000_000)
                    throw new InvalidDataException("更新文件的来源或校验值无效");
                var found = new ReleaseUpdate(tag, releaseVersion, uri, size, digest[7..], name == PortableAssetName);
                if (found.Portable) portable = found; else installer = found;
            }
            availableUpdate = portable ?? installer ?? throw new InvalidDataException("新版本没有适用的 Windows 更新文件");
            var method = availableUpdate.Portable ? "退出后自动替换程序并重启" : "启动一次安装程序";
            if (updateStatus is not null) updateStatus.Text = $"发现新版本 {tag}，约 {availableUpdate.Size / 1024 / 1024} MB。校验后{method}。";
            if (updateButton is not null) updateButton.Content = availableUpdate.Portable ? $"更新并重启 {tag}" : $"下载安装 {tag}";
            Log($"发现软件更新 {tag}，可在设置中更新");
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

    async Task DownloadAndApplyUpdateAsync(ReleaseUpdate release)
    {
        if (updateInstallRunning || updateCheckRunning) return;
        updateInstallRunning = true;
        if (updateButton is not null) updateButton.IsEnabled = false;
        var targetDir = Path.Combine(DataDir, "updates");
        var target = Path.Combine(targetDir, $"YinggeVideoLibrary-{(release.Portable ? "win-x64" : "Setup")}-{release.Version}.exe");
        var temporary = target + "." + Environment.ProcessId + ".part";
        try
        {
            Directory.CreateDirectory(targetDir);
            if (!File.Exists(target) || !await IsVerifiedUpdateAsync(target, release))
            {
                using var response = await UpdateHttp.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                using var downloadTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
                await using (var input = await response.Content.ReadAsStreamAsync())
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                {
                    var buffer = new byte[1024 * 1024];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(), downloadTimeout.Token)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), downloadTimeout.Token);
                        total += read;
                        if (total > release.Size) throw new InvalidDataException("下载文件大小超出发布信息");
                        if (updateStatus is not null) updateStatus.Text = $"下载 {release.Tag}：{total * 100 / release.Size}%";
                    }
                }
                if (!await IsVerifiedUpdateAsync(temporary, release))
                    throw new InvalidDataException("下载文件的大小或 SHA-256 校验不匹配");
                File.Move(temporary, target, true);
            }
            if (release.Portable)
            {
                var current = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径");
                // Check write access before closing; the replacement itself is
                // done by the verified new binary after this process exits.
                var probe = Path.Combine(Path.GetDirectoryName(current)!, $".yingge-write-{Guid.NewGuid():N}.tmp");
                try { File.WriteAllText(probe, ""); }
                finally { if (File.Exists(probe)) File.Delete(probe); }
                if (updateStatus is not null) updateStatus.Text = "校验通过，正在退出并自动重启新版…";
                var updater = new ProcessStartInfo(target)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = targetDir
                };
                updater.ArgumentList.Add("--apply-update");
                updater.ArgumentList.Add(current);
                updater.ArgumentList.Add(Environment.ProcessId.ToString());
                updater.ArgumentList.Add(release.Sha256);
                _ = Process.Start(updater) ?? throw new IOException("无法启动更新助手");
            }
            else
            {
                if (updateStatus is not null) updateStatus.Text = "校验通过，正在启动安装程序…";
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
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

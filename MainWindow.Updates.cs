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
using System.Windows.Media.Effects;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;
using TextBox = System.Windows.Controls.TextBox;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;

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
    TextBlock? updatePromptStatus;
    Window? updatePromptWindow;
    System.Windows.Controls.Button? updatePromptAction;
    System.Windows.Controls.Button? updatePromptSkip;
    System.Windows.Controls.Button? updatePromptCancel;
    ReleaseUpdate? availableUpdate;
    bool updateCheckRunning;
    bool updateInstallRunning;
    bool updatePromptDeferred;
    bool updateCloseAllowed;

    sealed record ReleaseUpdate(string Tag, Version Version, Uri DownloadUrl, long Size, string Sha256, bool Portable, string Notes);
    sealed record SkippedRelease(string Version, DateTimeOffset SkippedAtUtc);

    static bool BlockCloseWhileUpdating(bool running, bool closeAllowed) => running && !closeAllowed;

    static string UpdateDownloadTemporaryPath(string target, int processId) =>
        target + "." + processId + ".part.exe";

    static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("YinggeVideoLibrary/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    void InitializeUpdateUi()
    {
        Closing += (_, e) =>
        {
            if (!BlockCloseWhileUpdating(updateInstallRunning, updateCloseAllowed)) return;
            e.Cancel = true;
            SetUpdateStatus("更新正在下载或校验，请等待完成。失败时可以重试。");
        };
        if (SettingsPanel.Child is not StackPanel panel) return;
        var section = new StackPanel { Name = "SettingsUpdateOptions", Margin = new Thickness(0, 16, 0, 8) };
        section.Children.Add(new TextBlock
        {
            Text = "软件更新", FontSize = 17, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 47, 75))
        });
        updateStatus = new TextBlock
        {
            Text = $"当前版本 {FormatUpdateVersion(CurrentUpdateVersion())} · 从 GitHub Releases 获取更新",
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
        updateButton.Click += async (_, _) => await CheckForUpdatesAsync(true);
        section.Children.Add(updateButton);
        panel.Children.Add(section);
    }

    static Version CurrentUpdateVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    static string FormatUpdateVersion(Version version) => version.Revision > 0
        ? version.ToString(4) : version.ToString(3);

    static string SkippedReleasePath(string dataDirectory) => Path.Combine(dataDirectory, "updates", "skipped-release.json");

    static bool IsSkippedRelease(string dataDirectory, Version version)
    {
        try
        {
            var path = SkippedReleasePath(dataDirectory);
            if (!File.Exists(path)) return false;
            var saved = JsonSerializer.Deserialize<SkippedRelease>(File.ReadAllText(path));
            return Version.TryParse(saved?.Version, out var skipped) && skipped == version;
        }
        catch { return false; /* A damaged preference must not suppress updates. */ }
    }

    static void SkipRelease(string dataDirectory, Version version)
    {
        var path = SkippedReleasePath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SkippedRelease(version.ToString(4), DateTimeOffset.UtcNow)));
            File.Move(temporary, path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    static bool ShouldPresentUpdate(bool interactive, bool skipped) => interactive || !skipped;

    static bool CanDisplayUpdatePrompt(bool closing, bool visible, double opacity, bool showInTaskbar, WindowState state) =>
        !closing && visible && opacity >= 0.5 && showInTaskbar && state != WindowState.Minimized;

    static string SafeReleaseNotes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "此版本没有提供更新说明。";
        // GitHub release bodies are supplied by the publisher, but still display
        // them strictly as plain text. Never parse Markdown or HTML into controls.
        var clean = new string(text.Take(12000).Where(ch => !char.IsControl(ch) || ch is '\n' or '\r' or '\t').ToArray()).Trim();
        return clean.Length == 0 ? "此版本没有提供更新说明。" : clean;
    }

    static ReleaseUpdate ParseReleaseUpdate(JsonElement root)
    {
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsedVersion))
            throw new InvalidDataException("发布版本号无效");
        var releaseVersion = new Version(parsedVersion.Major, parsedVersion.Minor,
            Math.Max(0, parsedVersion.Build), Math.Max(0, parsedVersion.Revision));
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("新版本没有可用的便携更新文件");
        var notes = SafeReleaseNotes(root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null);
        ReleaseUpdate? portable = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (name != PortableAssetName) continue;
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var digest = asset.TryGetProperty("digest", out var digestNode) ? digestNode.GetString() ?? "" : "";
            var size = asset.GetProperty("size").GetInt64();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.StartsWith($"/{UpdateOwner}/{UpdateRepository}/releases/download/", StringComparison.OrdinalIgnoreCase)
                || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit) || size < 1_000_000)
                throw new InvalidDataException("更新文件的来源或校验值无效");
            portable = new ReleaseUpdate(tag, releaseVersion, uri, size, digest[7..], true, notes);
        }
        return portable ?? throw new InvalidDataException("新版本缺少便携更新文件；请稍后重试或手动下载安装包");
    }

    static System.Windows.Media.Brush UpdateBrush(string color) =>
        (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(color)!;

    static System.Windows.Controls.Button CreateUpdatePromptButton(string label, bool primary = false)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = label, MinWidth = primary ? 118 : 86, Height = 40,
            Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand, FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(1),
            Background = UpdateBrush(primary ? "#486CE1" : "#FFFFFF"),
            Foreground = UpdateBrush(primary ? "#FFFFFF" : "#40516A"),
            BorderBrush = UpdateBrush(primary ? "#486CE1" : "#D9E2ED")
        };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(System.Windows.Controls.Button.Background))
        { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(System.Windows.Controls.Button.BorderBrush))
        { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding(nameof(System.Windows.Controls.Button.BorderThickness))
        { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        button.Template = new ControlTemplate(typeof(System.Windows.Controls.Button)) { VisualTree = border };
        return button;
    }

    void SetUpdateStatus(string message)
    {
        if (updateStatus is not null) updateStatus.Text = message;
        if (updatePromptStatus is not null) updatePromptStatus.Text = message;
    }

    void ShowUpdatePrompt(ReleaseUpdate release, bool interactive, bool previouslySkipped)
    {
        // Hidden startup diagnostics display an offscreen, transparent MainWindow.
        // Never create or activate a visible update window from that process.
        if (!CanDisplayUpdatePrompt(isClosing, IsVisible, Opacity, ShowInTaskbar, WindowState))
        {
            updatePromptDeferred = true;
            return;
        }
        updatePromptDeferred = false;
        updatePromptWindow?.Close();

        var dialog = new Window
        {
            Owner = this, Title = $"映格视频库 · 发现新版本 {release.Tag}",
            Width = 690, Height = 570, MinWidth = 530, MinHeight = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None, AllowsTransparency = true,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            ShowActivated = interactive, Background = Brushes.Transparent,
            FontFamily = FontFamily, FontSize = 13
        };
        updatePromptWindow = dialog;
        UpdateBackdrop.Visibility = Visibility.Visible;
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(updatePromptWindow, dialog))
            {
                updatePromptWindow = null;
                UpdateBackdrop.Visibility = Visibility.Collapsed;
                updatePromptStatus = null;
                updatePromptAction = null;
                updatePromptSkip = null;
                updatePromptCancel = null;
            }
        };
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape || updateInstallRunning) return;
            dialog.Close();
            e.Handled = true;
        };
        dialog.Closing += (_, e) =>
        {
            if (!BlockCloseWhileUpdating(updateInstallRunning, updateCloseAllowed)) return;
            e.Cancel = true;
            SetUpdateStatus("更新正在下载或校验，请等待完成。失败时可以重试。");
        };

        var outer = new Border
        {
            Margin = new Thickness(20), CornerRadius = new CornerRadius(20),
            Background = Brushes.White, BorderBrush = UpdateBrush("#DEE6F1"), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 25, ShadowDepth = 9, Opacity = 0.30, Color = System.Windows.Media.Colors.SlateGray }
        };
        dialog.Content = outer;
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.Child = layout;

        var header = new DockPanel { Margin = new Thickness(28, 24, 28, 20) };
        Grid.SetRow(header, 0);
        layout.Children.Add(header);
        var close = new System.Windows.Controls.Button
        {
            Content = "×", Width = 30, Height = 30, FontSize = 20,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = UpdateBrush("#53647E"), Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "关闭更新提示"
        };
        close.Click += (_, _) => { if (!updateInstallRunning) dialog.Close(); };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var icon = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(10),
            Background = UpdateBrush("#486CE1"), Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock { Text = "✦", FontSize = 19, Foreground = Brushes.White, HorizontalAlignment = WpfHorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        header.Children.Add(new TextBlock
        {
            Text = "发现新版本", FontSize = 19, FontWeight = FontWeights.SemiBold,
            Foreground = UpdateBrush("#1E2B42"), VerticalAlignment = VerticalAlignment.Center
        });

        var body = new Grid { Margin = new Thickness(28, 0, 28, 0) };
        Grid.SetRow(body, 1);
        layout.Children.Add(body);
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(new TextBlock
        {
            Text = release.Tag, FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = UpdateBrush("#486CE1"), Margin = new Thickness(0, 3, 0, 8)
        });
        var current = new TextBlock
        {
            Text = $"当前版本 {FormatUpdateVersion(CurrentUpdateVersion())}，新版本 {release.Tag} 已可使用。"
                + (previouslySkipped ? " 你之前跳过了这个版本。" : ""),
            Foreground = UpdateBrush("#64758D"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        };
        Grid.SetRow(current, 1);
        body.Children.Add(current);
        var notesHeading = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        notesHeading.Children.Add(new Separator { Background = UpdateBrush("#E6ECF4"), Margin = new Thickness(0, 0, 0, 17) });
        notesHeading.Children.Add(new TextBlock { Text = "更新内容", FontWeight = FontWeights.SemiBold, Foreground = UpdateBrush("#24344E") });
        Grid.SetRow(notesHeading, 2);
        body.Children.Add(notesHeading);
        var notes = new TextBox
        {
            Text = release.Notes, IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            BorderThickness = new Thickness(0), Padding = new Thickness(0, 3, 10, 3),
            Foreground = UpdateBrush("#455875"), Background = Brushes.White,
            FontSize = 12.5, TabIndex = 1
        };
        Grid.SetRow(notes, 3);
        body.Children.Add(notes);

        var footer = new Border
        {
            Background = UpdateBrush("#F3F6FA"),
            BorderBrush = UpdateBrush("#E6ECF4"), BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 20, 20), Padding = new Thickness(24, 16, 28, 16)
        };
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Child = footerGrid;
        updatePromptStatus = new TextBlock
        {
            Text = "下载校验后自动替换并重启",
            Foreground = UpdateBrush("#71819A"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0)
        };
        footerGrid.Children.Add(updatePromptStatus);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = WpfHorizontalAlignment.Right };
        Grid.SetColumn(actions, 1);
        footerGrid.Children.Add(actions);
        updatePromptCancel = CreateUpdatePromptButton("取消");
        updatePromptSkip = CreateUpdatePromptButton("跳过此版本");
        updatePromptAction = CreateUpdatePromptButton("立即更新", primary: true);
        updatePromptCancel.Click += (_, _) => dialog.Close();
        updatePromptSkip.Click += (_, _) =>
        {
            try
            {
                SkipRelease(DataDir, release.Version);
                SetUpdateStatus($"已跳过版本 {release.Tag}。以后仍可手动检查更新。");
                dialog.Close();
            }
            catch (Exception ex) { SetUpdateStatus("保存跳过设置失败：" + ex.Message); }
        };
        updatePromptAction.Click += async (_, _) =>
        {
            updatePromptAction.IsEnabled = false;
            updatePromptSkip.IsEnabled = false;
            updatePromptCancel.IsEnabled = false;
            close.IsEnabled = false;
            await DownloadAndApplyUpdateAsync(release);
            if (!isClosing && dialog.IsVisible)
            {
                updatePromptAction.IsEnabled = true;
                updatePromptSkip.IsEnabled = true;
                updatePromptCancel.IsEnabled = true;
                close.IsEnabled = true;
            }
        };
        actions.Children.Add(updatePromptCancel);
        actions.Children.Add(updatePromptSkip);
        actions.Children.Add(updatePromptAction);
        dialog.Show();
    }

    void PresentDeferredUpdateIfAvailable()
    {
        if (!updatePromptDeferred || updatePromptWindow is not null || availableUpdate is null
            || IsSkippedRelease(DataDir, availableUpdate.Version)) return;
        ShowUpdatePrompt(availableUpdate, false, false);
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
            if (releaseVersion <= CurrentUpdateVersion())
            {
                TryRecordSuccessfulUpdateCheck();
                availableUpdate = null;
                updatePromptDeferred = false;
                if (updateStatus is not null) updateStatus.Text = $"当前已是最新版 {FormatUpdateVersion(CurrentUpdateVersion())}";
                updatePromptWindow?.Close();
                return;
            }
            // A newer release without a verified portable asset is a failed
            // check, so it must not postpone the next automatic retry.
            availableUpdate = ParseReleaseUpdate(root);
            TryRecordSuccessfulUpdateCheck();
            var skipped = IsSkippedRelease(DataDir, releaseVersion);
            if (updateStatus is not null) updateStatus.Text = skipped && !interactive
                ? $"已跳过版本 {tag}。点击“检查更新”仍可查看并安装。"
                : $"发现新版本 {tag}，约 {availableUpdate.Size / 1024 / 1024} MB。校验后退出旧程序、自动替换并重启。";
            if (ShouldPresentUpdate(interactive, skipped)) ShowUpdatePrompt(availableUpdate, interactive, skipped);
            else updatePromptDeferred = false;
            Log(skipped && !interactive ? $"已跳过软件更新 {tag}" : $"发现软件更新 {tag}");
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

    void TryRecordSuccessfulUpdateCheck()
    {
        try { File.WriteAllText(Path.Combine(DataDir, "last-update-check.txt"), DateTimeOffset.UtcNow.ToString("O")); }
        catch { /* A timestamp failure should not hide a usable release. */ }
    }

    async Task DownloadAndApplyUpdateAsync(ReleaseUpdate release)
    {
        if (updateInstallRunning || updateCheckRunning) return;
        if (!release.Portable)
        {
            SetUpdateStatus("此版本没有便携更新文件，不会启动安装向导。");
            return;
        }
        if (!PublishedExecutable.TryGetRunningPath(out var current, out var executableError))
        {
            SetUpdateStatus("更新失败：" + executableError);
            return;
        }
        if (batchOperationInProgress)
        {
            SetUpdateStatus("正在保存批量操作，完成后再点击更新。");
            return;
        }
        updateInstallRunning = true;
        if (updateButton is not null) updateButton.IsEnabled = false;
        var targetDir = Path.Combine(DataDir, "updates");
        var target = Path.Combine(targetDir, $"YinggeVideoLibrary-win-x64-{release.Version}.exe");
        // The identity check reads PE version resources only from an .exe path.
        var temporary = UpdateDownloadTemporaryPath(target, Environment.ProcessId);
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
                        SetUpdateStatus($"下载 {release.Tag}：{total * 100 / release.Size}%");
                    }
                }
                if (!await IsVerifiedUpdateAsync(temporary, release))
                    throw new InvalidDataException("下载文件的大小、SHA-256 或程序身份与目标版本不符");
                File.Move(temporary, target, true);
            }
            {
                if (batchOperationInProgress)
                    throw new InvalidOperationException("正在保存批量操作，完成后再重试更新。");
                // Check write access before closing; the replacement itself is
                // done by the verified new binary after this process exits.
                var probe = Path.Combine(Path.GetDirectoryName(current)!, $".yingge-write-{Guid.NewGuid():N}.tmp");
                try { File.WriteAllText(probe, ""); }
                finally { if (File.Exists(probe)) File.Delete(probe); }
                SetUpdateStatus("校验通过，正在退出并自动重启新版…");
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
                using var helper = Process.Start(updater) ?? throw new IOException("无法启动更新助手");
                updateCloseAllowed = true;
                Close();
                if (!isClosing)
                {
                    updateCloseAllowed = false;
                    try { if (!helper.HasExited) helper.Kill(); } catch { }
                    throw new InvalidOperationException("窗口仍在完成批量保存，更新助手已停止；请稍后重试。");
                }
            }
        }
        catch (Exception ex)
        {
            SetUpdateStatus("更新失败：" + ex.Message);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            updateInstallRunning = false;
            updateCloseAllowed = false;
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
            return hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)
                && PublishedExecutable.IsReleaseBinary(path, release.Version);
        }
        catch { return false; }
    }

    internal static IReadOnlyDictionary<string, bool> VerifyUpdatePromptForDiagnostics(string directory)
    {
        var findings = new Dictionary<string, bool>(StringComparer.Ordinal);
        var json = $$"""
            {
              "tag_name": "v1.5.1",
              "body": "修复启动问题\n<script>alert(1)</script>",
              "assets": [
                { "name": "{{InstallerAssetName}}", "browser_download_url": "https://github.com/{{UpdateOwner}}/{{UpdateRepository}}/releases/download/v1.5.1/{{InstallerAssetName}}", "digest": "sha256:{{new string('a', 64)}}", "size": 2000000 },
                { "name": "{{PortableAssetName}}", "browser_download_url": "https://github.com/{{UpdateOwner}}/{{UpdateRepository}}/releases/download/v1.5.1/{{PortableAssetName}}", "digest": "sha256:{{new string('b', 64)}}", "size": 3000000 }
              ]
            }
            """;
        using (var document = JsonDocument.Parse(json))
        {
            var release = ParseReleaseUpdate(document.RootElement);
            findings["releaseVersionAndPortablePreference"] = release.Version == new Version(1, 5, 1, 0)
                && release.Portable && release.Size == 3000000;
            findings["releaseNotesPlainText"] = release.Notes.Contains("<script>", StringComparison.Ordinal)
                && !SafeReleaseNotes("a\0b").Contains('\0')
                && SafeReleaseNotes(new string('x', 15000)).Length == 12000;
        }
        try
        {
            using var badHost = JsonDocument.Parse(json.Replace("github.com", "example.com", StringComparison.Ordinal));
            _ = ParseReleaseUpdate(badHost.RootElement);
            findings["rejectUntrustedAssetUrl"] = false;
        }
        catch (InvalidDataException) { findings["rejectUntrustedAssetUrl"] = true; }
        try
        {
            using var badDigest = JsonDocument.Parse(json.Replace("sha256:", "sha512:", StringComparison.Ordinal));
            _ = ParseReleaseUpdate(badDigest.RootElement);
            findings["rejectInvalidDigest"] = false;
        }
        catch (InvalidDataException) { findings["rejectInvalidDigest"] = true; }
        try
        {
            using var installerOnly = JsonDocument.Parse(json.Replace(PortableAssetName, "unrelated.exe", StringComparison.Ordinal));
            _ = ParseReleaseUpdate(installerOnly.RootElement);
            findings["installerOnlyDoesNotLaunchWizard"] = false;
        }
        catch (InvalidDataException) { findings["installerOnlyDoesNotLaunchWizard"] = true; }

        Directory.CreateDirectory(directory);
        var version = new Version(1, 5, 1, 0);
        findings["newReleaseNotSkipped"] = !IsSkippedRelease(directory, version);
        SkipRelease(directory, version);
        findings["skipPersistsOnlyOneVersion"] = IsSkippedRelease(directory, version)
            && !IsSkippedRelease(directory, new Version(1, 5, 2, 0));
        findings["manualCheckCanShowSkippedRelease"] = ShouldPresentUpdate(true, true)
            && !ShouldPresentUpdate(false, true);
        findings["hiddenDiagnosticsCannotShowPrompt"] = !CanDisplayUpdatePrompt(false, true, 0, false, WindowState.Normal)
            && !CanDisplayUpdatePrompt(false, false, 1, true, WindowState.Normal)
            && CanDisplayUpdatePrompt(false, true, 1, true, WindowState.Normal);
        findings["activeUpdateCannotCloseSilently"] = BlockCloseWhileUpdating(true, false)
            && !BlockCloseWhileUpdating(true, true) && !BlockCloseWhileUpdating(false, false);
        findings["downloadTemporaryPathPreservesExecutableIdentity"] =
            UpdateDownloadTemporaryPath(Path.Combine(directory, "update.exe"), 123)
                .EndsWith(".part.exe", StringComparison.OrdinalIgnoreCase);
        var runningExecutable = Path.Combine(AppContext.BaseDirectory, "映格视频库.exe");
        findings["downloadMustBeTargetVersionBinary"] =
            PublishedExecutable.IsReleaseBinary(runningExecutable, CurrentUpdateVersion())
            && !PublishedExecutable.IsReleaseBinary(runningExecutable, new Version(99, 0, 0, 0))
            && !PublishedExecutable.TryGetBinaryVersion(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"), out _);
        if (PublishedExecutable.IsReleaseBinary(runningExecutable, CurrentUpdateVersion()))
        {
            // Exercise the same verifier used by a downloaded GitHub asset. Run it
            // off the WPF thread because it hashes asynchronously.
            static bool Verified(string path, ReleaseUpdate candidate) =>
                Task.Run(() => IsVerifiedUpdateAsync(path, candidate)).GetAwaiter().GetResult();

            var length = new FileInfo(runningExecutable).Length;
            using var publishedStream = File.OpenRead(runningExecutable);
            var hash = Convert.ToHexString(SHA256.HashData(publishedStream));
            var valid = new ReleaseUpdate("v" + FormatUpdateVersion(CurrentUpdateVersion()),
                CurrentUpdateVersion(), new Uri("https://github.com/PRC1978/YinggeVideoLibrary"),
                length, hash, true, "");
            findings["downloadedBinaryWithMatchingSizeHashAndIdentityAccepted"] =
                Verified(runningExecutable, valid);

            var truncated = Path.Combine(directory, "truncated-update.exe");
            using (var source = File.OpenRead(runningExecutable))
            using (var destination = File.Create(truncated))
            {
                var bytes = new byte[4096];
                destination.Write(bytes, 0, source.Read(bytes, 0, bytes.Length));
            }
            findings["truncatedDownloadedBinaryRejected"] = !Verified(truncated, valid);
            findings["wrongHashDownloadedBinaryRejected"] =
                !Verified(runningExecutable, valid with { Sha256 = new string('0', 64) });

            var foreignExecutable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            using var foreignStream = File.OpenRead(foreignExecutable);
            var foreignHash = Convert.ToHexString(SHA256.HashData(foreignStream));
            var foreignCandidate = valid with
            {
                Size = new FileInfo(foreignExecutable).Length,
                Sha256 = foreignHash
            };
            findings["wrongIdentityDownloadedBinaryRejectedDespiteMatchingHash"] =
                !Verified(foreignExecutable, foreignCandidate);
        }
        else
        {
            findings["downloadedBinaryWithMatchingSizeHashAndIdentityAccepted"] = false;
            findings["truncatedDownloadedBinaryRejected"] = false;
            findings["wrongHashDownloadedBinaryRejected"] = false;
            findings["wrongIdentityDownloadedBinaryRejectedDespiteMatchingHash"] = false;
        }
        File.WriteAllText(SkippedReleasePath(directory), "{damaged");
        findings["corruptSkipFileDoesNotHideRelease"] = !IsSkippedRelease(directory, version);
        return findings;
    }
}

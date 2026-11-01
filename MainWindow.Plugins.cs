using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBlock = System.Windows.Controls.TextBlock;

namespace YinggeVideoLibrary;

// First batch of the plugin platform: discovery, enablement, permissions,
// out-of-process calls and the scrape bridge. Everything here is asynchronous
// and bounded; no plugin code runs during startup and no plugin can write
// metadata directly.
public partial class MainWindow
{
    readonly List<PluginDescriptor> pluginCatalog = [];
    PluginState? pluginState;
    bool pluginCatalogLoaded;
    bool pluginBusy;

    string PluginRoot => Path.Combine(DataDir, "plugins");

    void EnsurePluginCatalog(bool reload = false)
    {
        if (pluginCatalogLoaded && !reload) return;
        pluginCatalog.Clear();
        pluginCatalog.AddRange(PluginManifestReader.Scan(PluginRoot));
        pluginState = PluginStateStore.Load(DataDir);
        foreach (var plugin in pluginCatalog)
        {
            plugin.Enabled = PluginStateStore.IsEnabled(pluginState, plugin.Id);
            if (pluginState.LastErrors.TryGetValue(plugin.Id, out var lastError))
            {
                plugin.LastError = lastError.Message;
                plugin.LastErrorAt = lastError.At;
            }
            // An enabled plugin whose permission set changed must be confirmed
            // again; it stops running instead of silently keeping old consent.
            if (plugin.Enabled && plugin.Manifest is not null && !PluginStateStore.ConsentMatches(pluginState, plugin))
            {
                plugin.Enabled = false;
                plugin.Status = PluginStatus.Disabled;
                plugin.Message = "权限已变化，需要重新确认后才会启用。";
            }
        }
        pluginCatalogLoaded = true;
    }

    void RenderPlugins()
    {
        if (PluginListPanel is null) return;
        PluginListPanel.Children.Clear();
        var loadable = pluginCatalog.Count(item => item.Manifest is not null && item.Status is not PluginStatus.Disabled
            && item.Status is not PluginStatus.DuplicateId && item.Status is not PluginStatus.IncompatibleApi
            && item.Status is not PluginStatus.UnsupportedCapability && item.Status is not PluginStatus.UnsafeEntry
            && item.Status is not PluginStatus.MissingEntry && item.Status is not PluginStatus.DamagedManifest
            && item.Status is not PluginStatus.MissingManifest);
        var enabled = pluginCatalog.Count(item => item.Enabled);
        var broken = pluginCatalog.Count - loadable;
        PluginRootText.Text = PluginRoot;
        PluginSummaryText.Text = pluginCatalog.Count == 0
            ? "还没有发现插件。把包含 plugin.json 的文件夹放进插件目录，然后点“重新扫描”。"
            : $"发现 {pluginCatalog.Count} 个 · 可用 {loadable} 个 · 已启用 {enabled} 个 · 需要处理 {broken} 个";
        if (!string.IsNullOrWhiteSpace(pluginState?.LoadError))
        {
            PluginStateErrorText.Text = pluginState!.LoadError;
            PluginStateErrorText.Visibility = Visibility.Visible;
        }
        else PluginStateErrorText.Visibility = Visibility.Collapsed;

        if (pluginCatalog.Count == 0)
        {
            PluginListPanel.Children.Add(new TextBlock
            {
                Text = "插件目录为空。示例插件位于仓库的 samples\\ExampleScrapePlugin，构建后复制到上面的目录即可。",
                Foreground = new SolidColorBrush(Color.FromRgb(131, 144, 163)), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 6, 2, 6)
            });
        }
        foreach (var plugin in pluginCatalog) PluginListPanel.Children.Add(BuildPluginRow(plugin));
        RenderPluginSourceBox();
    }

    Border BuildPluginRow(PluginDescriptor plugin)
    {
        var (badgeText, badgeColor) = plugin.Status switch
        {
            PluginStatus.Ready when plugin.Enabled => ("已启用", Color.FromRgb(21, 128, 61)),
            PluginStatus.Ready => ("可用", Color.FromRgb(62, 99, 213)),
            PluginStatus.Disabled => ("需重新确认", Color.FromRgb(180, 83, 9)),
            PluginStatus.DuplicateId => ("ID 重复", Color.FromRgb(185, 28, 28)),
            PluginStatus.IncompatibleApi => ("版本不兼容", Color.FromRgb(185, 28, 28)),
            PluginStatus.UnsupportedCapability => ("能力未开放", Color.FromRgb(180, 83, 9)),
            PluginStatus.UnsafeEntry => ("入口不安全", Color.FromRgb(185, 28, 28)),
            PluginStatus.MissingEntry => ("缺少入口文件", Color.FromRgb(185, 28, 28)),
            PluginStatus.MissingManifest => ("缺少清单", Color.FromRgb(185, 28, 28)),
            _ => ("清单损坏", Color.FromRgb(185, 28, 28))
        };
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(250, 251, 254)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 242)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11),
            Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10)
        };
        var stack = new StackPanel();
        var header = new DockPanel { LastChildFill = true };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = plugin.DisplayName, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(38, 51, 71)), VerticalAlignment = VerticalAlignment.Center
        });
        title.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(28, badgeColor.R, badgeColor.G, badgeColor.B)),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock { Text = badgeText, FontSize = 11, Foreground = new SolidColorBrush(badgeColor) }
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var toggle = new Button
        {
            Content = plugin.Enabled ? "停用" : "启用", Height = 32, MinWidth = 66, Padding = new Thickness(12, 0, 12, 0),
            Style = (Style)FindResource("Action"), Tag = plugin, Margin = new Thickness(0, 0, 6, 0)
        };
        toggle.Click += PluginToggleClick;
        // A plugin that cannot be loaded must not present an enable button that
        // would fail later; the reason is already on the card.
        toggle.IsEnabled = plugin.Manifest is not null && plugin.Status is PluginStatus.Ready or PluginStatus.Disabled;
        actions.Children.Add(toggle);
        if (!string.IsNullOrWhiteSpace(plugin.LastError))
        {
            var clear = new Button
            {
                Content = "清除错误", Height = 32, Padding = new Thickness(10, 0, 10, 0),
                Style = (Style)FindResource("Action"), Tag = plugin, Margin = new Thickness(0)
            };
            clear.Click += PluginClearErrorClick;
            actions.Children.Add(clear);
        }
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(title);
        stack.Children.Add(header);

        var identity = plugin.Manifest is null
            ? plugin.FolderName
            : $"{plugin.Id} · v{plugin.Manifest.Version} · 接口 v{plugin.Manifest.ApiVersion}" +
              (string.IsNullOrWhiteSpace(plugin.Manifest.Author) ? "" : " · " + plugin.Manifest.Author);
        stack.Children.Add(new TextBlock
        {
            Text = identity, Foreground = new SolidColorBrush(Color.FromRgb(131, 144, 163)),
            FontSize = 12, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
        });
        var capabilities = plugin.Manifest?.Capabilities is { Count: > 0 } list ? string.Join("、", list) : "未声明能力";
        var network = plugin.Manifest?.Permissions.Network is { Count: > 0 } hosts ? string.Join("、", hosts) : "无网络权限";
        var data = plugin.Manifest?.Permissions.Data is { Count: > 0 } scopes ? string.Join("、", scopes) : "不接收数据";
        stack.Children.Add(new TextBlock
        {
            Text = $"能力：{capabilities}　·　域名：{network}　·　数据：{data}",
            Foreground = new SolidColorBrush(Color.FromRgb(82, 100, 122)), FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0)
        });
        if (!string.IsNullOrWhiteSpace(plugin.Message))
        {
            stack.Children.Add(new TextBlock
            {
                Text = plugin.Message,
                Foreground = plugin.Status is PluginStatus.Ready or PluginStatus.Disabled
                    ? new SolidColorBrush(Color.FromRgb(180, 83, 9)) : new SolidColorBrush(Color.FromRgb(185, 28, 28)),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
            });
        }
        if (!string.IsNullOrWhiteSpace(plugin.LastError))
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"最近错误（{plugin.LastErrorAt:MM-dd HH:mm}）：{plugin.LastError}",
                Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9)), FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
            });
        }
        card.Child = stack;
        return card;
    }

    void RenderPluginSourceBox()
    {
        if (PluginScrapeSourceBox is null) return;
        var selected = (PluginScrapeSourceBox.SelectedItem as PluginDescriptor)?.Id;
        PluginScrapeSourceBox.Items.Clear();
        PluginScrapeSourceBox.DisplayMemberPath = nameof(PluginDescriptor.DisplayName);
        foreach (var plugin in pluginCatalog.Where(item => item.Enabled && item.Status is PluginStatus.Ready or PluginStatus.Disabled))
            PluginScrapeSourceBox.Items.Add(plugin);
        if (PluginScrapeSourceBox.Items.Count == 0)
        {
            PluginScrapeSourceBox.IsEnabled = false;
            PluginScrapeButton.IsEnabled = false;
            PluginScrapeHintText.Text = "没有已启用的搜刮来源插件。先在上方启用一个，再用它查询。";
            return;
        }
        PluginScrapeSourceBox.IsEnabled = true;
        PluginScrapeButton.IsEnabled = !pluginBusy;
        PluginScrapeHintText.Text = "先在视频库点选一个视频；或在输入框填写标题，仅用于离线检查插件。";
        var restored = PluginScrapeSourceBox.Items.OfType<PluginDescriptor>().FirstOrDefault(item => item.Id == selected);
        PluginScrapeSourceBox.SelectedItem = restored ?? PluginScrapeSourceBox.Items[0];
    }

    void PluginToggleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PluginDescriptor plugin || pluginState is null) return;
        if (plugin.Enabled)
        {
            pluginState.Enabled.RemoveAll(id => id.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase));
            plugin.Enabled = false;
            plugin.Status = PluginStatus.Ready;
            plugin.Message = "";
            PluginStateStore.Save(DataDir, pluginState, out _);
            PluginLog.Append(DataDir, plugin.Id, "已停用");
            RenderPlugins();
            return;
        }
        if (plugin.Manifest is null) return;
        var signature = plugin.PermissionSignature;
        var consented = pluginState.Consents.TryGetValue(plugin.Id, out var saved) && saved == signature;
        if (!consented)
        {
            var network = plugin.Manifest.Permissions.Network.Count == 0 ? "（无）" : string.Join("、", plugin.Manifest.Permissions.Network);
            var data = plugin.Manifest.Permissions.Data.Count == 0 ? "（不接收任何数据）" : string.Join("、", plugin.Manifest.Permissions.Data);
            var answer = MessageBox.Show(this,
                $"启用“{plugin.DisplayName}”将允许它：\n\n" +
                $"· 访问这些域名：{network}\n" +
                $"· 接收这些数据：{data}\n\n" +
                "程序只发送标题或番号，不发送完整路径、私有标签和密钥。插件在独立进程运行，但独立进程不是 Windows 安全沙箱，请只安装你信任的来源。\n\n确认启用？",
                "确认插件权限", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
            pluginState.Consents[plugin.Id] = signature;
        }
        if (!pluginState.Enabled.Contains(plugin.Id, StringComparer.OrdinalIgnoreCase))
            pluginState.Enabled.Add(plugin.Id);
        plugin.Enabled = true;
        plugin.Status = PluginStatus.Ready;
        plugin.Message = "";
        if (!PluginStateStore.Save(DataDir, pluginState, out var error))
            MessageBox.Show(this, "插件状态无法保存：" + error, "插件", MessageBoxButton.OK, MessageBoxImage.Warning);
        else PluginLog.Append(DataDir, plugin.Id, "已启用");
        RenderPlugins();
    }

    void PluginClearErrorClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PluginDescriptor plugin || pluginState is null) return;
        PluginStateStore.ClearError(DataDir, plugin.Id);
        pluginState.LastErrors.Remove(plugin.Id);
        plugin.LastError = null;
        plugin.LastErrorAt = null;
        RenderPlugins();
    }

    void PluginRescanClick(object sender, RoutedEventArgs e)
    {
        EnsurePluginCatalog(reload: true);
        RenderPlugins();
        Log($"插件目录已重新扫描：发现 {pluginCatalog.Count} 个。");
    }

    void PluginOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(PluginRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{PluginRoot}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法打开插件目录：" + ex.Message, "插件", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    async void PluginScrapeClick(object sender, RoutedEventArgs e)
    {
        if (pluginBusy) return;
        if (PluginScrapeSourceBox.SelectedItem is not PluginDescriptor plugin)
        {
            PluginScrapeResultText.Text = "先在插件页启用一个搜刮来源插件。";
            return;
        }
        var query = PluginScrapeQueryBox.Text.Trim();
        var video = videos.FirstOrDefault(item => selectedVideoIds.Contains(item.Id));
        if (query.Length == 0 && video is null)
        {
            PluginScrapeResultText.Text = "请先在视频库点选一个视频，或在输入框填写要查询的标题。";
            return;
        }
        if (query.Length == 0) query = video!.Title;

        pluginBusy = true;
        PluginScrapeButton.IsEnabled = false;
        PluginScrapeResultText.Text = $"正在调用 {plugin.DisplayName}…";
        var clock = Stopwatch.StartNew();
        try
        {
            var guessed = JavDbScraper.GuessCode(query);
            var parameters = new PluginSearchParameters
            {
                Token = Guid.NewGuid().ToString("N"),
                Query = query,
                Kind = (guessed?.Length ?? 0) >= 4 ? "code" : "title",
                Limit = 8
            };
            var outcome = await PluginProcessHost.SearchAsync(plugin, parameters, CancellationToken.None);
            PluginLog.Append(DataDir, plugin.Id, $"查询「{query}」→ {PluginScrapeBridge.DescribeOutcome(outcome)}" +
                (outcome.DroppedCandidates > 0 ? $"；丢弃 {outcome.DroppedCandidates} 条无效候选" : ""));
            if (!outcome.Ok)
            {
                plugin.LastError = PluginLog.Redact(outcome.ErrorMessage);
                plugin.LastErrorAt = DateTime.Now;
                PluginStateStore.RecordError(DataDir, plugin.Id, $"{outcome.ErrorCode}：{outcome.ErrorMessage}");
                PluginScrapeResultText.Text = $"插件调用失败：{plugin.LastError}";
                RenderPlugins();
                return;
            }
            if (outcome.Candidates.Count == 0)
            {
                PluginScrapeResultText.Text = $"插件在 {outcome.ElapsedMs} ms 内没有找到候选。";
                return;
            }
            if (video is null)
            {
                PluginScrapeResultText.Text =
                    $"插件返回 {outcome.Candidates.Count} 条候选（{outcome.ElapsedMs} ms）。没有选中视频，因此只预览、不入队。";
                return;
            }
            // The commit path updates bound VideoItem state, so it must stay on
            // the UI thread; the file writes inside are short and journaled.
            var stored = StorePluginCandidates(video, outcome.Candidates, plugin);
            PluginScrapeResultText.Text = stored == 0
                ? "候选都已存在，未重复入队。"
                : $"已把 {stored} 条候选放入“搜刮”页的人工审核队列（{outcome.ElapsedMs} ms）。原始资料没有被修改。";
            RefreshReviewQueue();
        }
        catch (Exception ex)
        {
            PluginScrapeResultText.Text = "插件调用异常：" + ex.Message;
            PluginLog.Append(DataDir, plugin.Id, "调用异常：" + ex.Message);
        }
        finally
        {
            pluginBusy = false;
            PluginScrapeButton.IsEnabled = PluginScrapeSourceBox.Items.Count > 0;
            Log($"插件搜刮结束，用时 {clock.ElapsedMilliseconds} ms。");
        }
    }

    // Each candidate is stored through the same journaled review transaction as
    // a built-in source, with Status forced to pending. Nothing here touches
    // metadata directly.
    int StorePluginCandidates(VideoItem video, IReadOnlyList<PluginCandidate> candidates, PluginDescriptor plugin)
    {
        var stored = 0;
        foreach (var candidate in candidates)
        {
            var metadata = PluginScrapeBridge.ToMetadata(candidate, plugin);
            var review = PluginScrapeBridge.BuildPendingReview(video.Id, video.Title, metadata, plugin, candidate.ConfidenceHint);
            var locked = false;
            try
            {
                try { ConfigWriteMutex.WaitOne(); locked = true; }
                catch (AbandonedMutexException) { locked = true; }
                var before = CaptureReviewState(video.Id);
                // A human-approved record is never replaced by a plugin claim.
                if (before.Review is { Status: "approved" })
                {
                    Log($"插件候选已跳过：{video.Title} 已有通过审核的资料。");
                    continue;
                }
                var after = before.Clone();
                after.Review = review;
                after = WithAutomaticAmateurTag(video, after, false);
                var transaction = ReviewTransactionStore.Create("candidate",
                    new Dictionary<string, ReviewItemSnapshot> { [video.Id] = before },
                    new Dictionary<string, ReviewItemSnapshot> { [video.Id] = after });
                if (!TryCommitJournaledReview(transaction, out var error))
                {
                    Log("插件候选无法写入审核记录：" + error);
                    continue;
                }
                ApplyReviewBadge(video);
                stored++;
            }
            catch (Exception ex)
            {
                Log("插件候选入队失败：" + ex.Message);
            }
            finally { if (locked) ConfigWriteMutex.ReleaseMutex(); }
        }
        if (stored > 0) RenderCards();
        return stored;
    }
}

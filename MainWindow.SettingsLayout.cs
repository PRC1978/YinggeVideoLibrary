using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Binding = System.Windows.Data.Binding;
using Control = System.Windows.Controls.Control;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Panel = System.Windows.Controls.Panel;
using Brushes = System.Windows.Media.Brushes;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    readonly Dictionary<string, Expander> settingsSections = new(StringComparer.Ordinal);
    readonly Dictionary<string, Button> settingsNavigation = new(StringComparer.Ordinal);
    Grid? settingsWorkspace;
    StackPanel? settingsNavigationRail;
    string activeSettingsSection = "appearance";
    string settingsSearchQuery = string.Empty;

    // Keep the original controls alive: they carry the existing events, values and validation.
    // The startup initializers add their controls to the flat panel first; only then do we
    // move those instances into the themed sections below.
    void RegroupSettingsPanel()
    {
        if (SettingsPanel.Child is not StackPanel panel || settingsSections.Count != 0) return;
        var original = panel.Children.Cast<UIElement>().ToArray();
        var remaining = new HashSet<UIElement>(original);
        var buckets = new Dictionary<string, List<UIElement>>(StringComparer.Ordinal)
        {
            ["appearance"] = [], ["library"] = [], ["scrape"] = [],
            ["ai"] = [], ["playback"] = [], ["online"] = [],
            ["startup"] = [], ["updates"] = [], ["data"] = []
        };

        int IndexOf(UIElement? element) => element is null ? -1 : Array.IndexOf(original, element);
        int FindText(string label) => Array.FindIndex(original, item => item is TextBlock text && text.Text?.StartsWith(label, StringComparison.Ordinal) == true);
        int FindButton(string label) => Array.FindIndex(original, item => item is Button button && Equals(button.Content, label));
        UIElement? Named(string name) => original.FirstOrDefault(item => item is FrameworkElement element && element.Name == name);
        void Add(string key, UIElement? element)
        {
            if (element is not null && element is not Separator && remaining.Remove(element)) buckets[key].Add(element);
        }
        void AddRange(string key, int first, int last)
        {
            if (first < 0 || last < first) return;
            for (var i = first; i <= Math.Min(last, original.Length - 1); i++) Add(key, original[i]);
        }

        AddRange("playback", 0, IndexOf(ThumbnailInfo));
        Add("appearance", ShowAllBox);
        Add("appearance", Named("SettingsAppearanceOptions"));
        var reset = FindButton("恢复默认界面布局");
        if (reset >= 0)
        {
            Add("appearance", original[Math.Max(0, reset - 1)]);
            Add("appearance", original[reset]);
        }

        Add("library", GpuBox);
        var duplicates = FindButton("查找重复视频");
        if (duplicates >= 0) Add("library", original[duplicates]);
        Add("library", Named("SiteListImportActions"));
        Add("library", Named("SettingsLibraryOptions"));

        AddRange("scrape", IndexOf(TmdbTokenBox) - 1, FindButton("保存 TMDB 凭据"));
        Add("scrape", Named("SettingsScrapeOptions"));
        AddRange("ai", FindText("AI 分类接口"), FindButton("保存 AI 设置"));
        AddRange("online", FindText("在线站点与扩展"), IndexOf(ExtensionInfo));
        Add("startup", AutoStartBox);
        Add("startup", Named("SettingsStartupOptions"));
        Add("updates", Named("SettingsUpdateOptions"));
        AddRange("data", IndexOf(BackupInfo) - 1, IndexOf(BackupInfo));

        // New settings added in future versions remain reachable even before they have an
        // explicit section mapping. Decorative separators and old group titles can be dropped.
        var extra = original.Where(item => remaining.Contains(item) && item is not Separator).ToList();
        // The former flat headings are represented by the card headers.
        extra.RemoveAll(item => item is TextBlock text &&
            (text.Text == "界面与数据" || text.Text == "软件更新"));
        foreach (var item in extra) Add("data", item);

        panel.Children.Clear();
        panel.Width = double.NaN;
        panel.MaxWidth = double.PositiveInfinity;
        panel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        SettingsPanel.Padding = new Thickness(0);
        SettingsPanel.MaxWidth = double.PositiveInfinity;
        SettingsPanel.Background = Brushes.Transparent;
        SettingsPanel.BorderThickness = new Thickness(0);
        SettingsPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;

        panel.Children.Add(CreateSettingsIntro());
        AddSection(panel, "appearance", "外观与浏览", "卡片密度、显示方式和界面布局", "◫", "#EAF0FF", "#4C6BDC", buckets["appearance"], true);
        AddSection(panel, "library", "视频库与缓存", "索引、缩略图和重复视频", "▣", "#EAF6F3", "#2D9A82", buckets["library"]);
        AddSection(panel, "scrape", "搜刮与审核", "来源凭据和批量搜刮范围", "⌕", "#FFF3E9", "#CB7A33", buckets["scrape"]);
        // Plugin management is a page of its own; this card is the discoverable entry.
        var pluginHint = new TextBlock
        {
            Text = "第三方搜刮来源在独立进程中运行。插件返回的候选只进入搜刮页的人工审核，不会直接写入视频资料；默认只发送标题或番号，不发送路径、私有标签和密钥。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Tint("#75859E"), MaxWidth = 640,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        var pluginOpen = new Button
        {
            Content = "打开插件管理", Style = (Style)FindResource("Action"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0)
        };
        pluginOpen.Click += OpenPluginsFromSettings;
        AddSection(panel, "plugins", "插件", "第三方搜刮来源：发现、启停、权限与最近错误",
            "⧉", "#EEF3FF", "#4C6BDC", [pluginHint, pluginOpen]);
        AddSection(panel, "ai", "AI 接口与费用", "本地或兼容接口、限速和预算", "✦", "#F2ECFC", "#8660BD", buckets["ai"]);
        AddSection(panel, "playback", "播放与封面", "播放器位置和缩略图生成状态", "▶", "#EBF5FD", "#3987B6", buckets["playback"]);
        AddSection(panel, "online", "在线与扩展", "内嵌浏览器的扩展设置", "◎", "#EEF2FB", "#5879BA", buckets["online"]);
        AddSection(panel, "startup", "启动", "开机自启、最小化与启动页", "↗", "#ECF7F0", "#4A9E67", buckets["startup"]);
        AddSection(panel, "updates", "软件更新", "检查新版本并在应用内更新、重启", "↻", "#EAF2FF", "#4D7BCB", buckets["updates"]);
        AddSection(panel, "data", "数据与备份", "本地备份、恢复和数据目录", "▤", "#F9F0EC", "#B6785A", buckets["data"]);
        panel.Children.Add(BuildAboutFooter());
        BuildSettingsWorkspace(panel);
    }

    FrameworkElement CreateSettingsIntro()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var introduction = new StackPanel();
        var saveFeedback = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        settingsSaveStatus = new TextBlock
        {
            Text = "设置更改会自动保存", FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        settingsSaveStatus.SetBinding(TextBlock.ForegroundProperty,
            new Binding(nameof(UiThemeState.TextSecondaryBrush)) { Source = UiThemeState.Instance });
        System.Windows.Automation.AutomationProperties.SetLiveSetting(settingsSaveStatus,
            System.Windows.Automation.AutomationLiveSetting.Polite);
        settingsSaveRetryButton = new Button
        {
            Content = "重试保存", Style = (Style)FindResource("Action"), Height = 28,
            Padding = new Thickness(9, 0, 9, 0), Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };
        settingsSaveRetryButton.Click += (_, _) => QueueSettingsSave();
        saveFeedback.Children.Add(settingsSaveStatus);
        saveFeedback.Children.Add(settingsSaveRetryButton);
        introduction.Children.Add(saveFeedback);
        grid.Children.Add(introduction);
        var search = new TextBox
        {
            Width = 210, Height = 36, Padding = new Thickness(10, 6, 10, 6),
            ToolTip = "搜索设置主题，例如缓存、AI、更新"
        };
        search.SetBinding(Control.BorderBrushProperty,
            new Binding(nameof(UiThemeState.BorderBrushToken)) { Source = UiThemeState.Instance });
        search.SetBinding(Control.BackgroundProperty,
            new Binding(nameof(UiThemeState.SurfaceBrush)) { Source = UiThemeState.Instance });
        search.TextChanged += (_, _) =>
        {
            settingsSearchQuery = search.Text.Trim();
            UpdateSettingsNavigation();
        };
        var searchWithPlaceholder = UiTextInput.WithPlaceholder(search, "搜索设置主题");
        Grid.SetColumn(searchWithPlaceholder, 1);
        grid.Children.Add(searchWithPlaceholder);
        return grid;
    }

    void BuildSettingsWorkspace(StackPanel panel)
    {
        var sections = settingsSections.ToArray();
        foreach (var (_, section) in sections) panel.Children.Remove(section);
        settingsWorkspace = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        settingsWorkspace.SizeChanged += (_, _) => UpdateSettingsNavigation();
        settingsWorkspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(204) });
        settingsWorkspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        settingsWorkspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        settingsNavigationRail = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        settingsWorkspace.Children.Add(settingsNavigationRail);
        var content = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
        foreach (var (key, section) in sections)
        {
            content.Children.Add(section);
            var label = section.Header is Grid header && header.Children.OfType<StackPanel>().FirstOrDefault() is { } labels
                ? labels.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? key : key;
            var button = new Button
            {
                Content = label, Tag = key, Style = (Style)FindResource("SettingsRailNav"),
                Height = 40, Margin = new Thickness(0, 0, 0, 3),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(12, 0, 10, 0)
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "SettingsNav_" + key);
            button.Click += (_, _) => { activeSettingsSection = key; UpdateSettingsNavigation(); };
            settingsNavigationRail.Children.Add(button);
            settingsNavigation[key] = button;
        }
        Grid.SetColumn(content, 2);
        settingsWorkspace.Children.Add(content);
        panel.Children.Insert(1, settingsWorkspace);
        UpdateSettingsNavigation();
    }

    void UpdateSettingsNavigation()
    {
        if (settingsWorkspace is null || settingsNavigationRail is null) return;
        var classic = UiTheme.IsClassicMode;
        var available = settingsWorkspace.ActualWidth;
        var narrow = available > 0 && available < 800;
        settingsWorkspace.ColumnDefinitions[0].Width = new GridLength(classic || narrow ? 0 : 204);
        settingsWorkspace.ColumnDefinitions[1].Width = new GridLength(classic || narrow ? 0 : 24);
        settingsNavigationRail.Visibility = classic || narrow ? Visibility.Collapsed : Visibility.Visible;
        var matches = settingsSections.Where(pair => settingsSearchQuery.Length == 0 ||
            (pair.Value.Tag as string ?? string.Empty).Contains(settingsSearchQuery, StringComparison.CurrentCultureIgnoreCase))
            .Select(pair => pair.Key).ToArray();
        if (!classic && !narrow && matches.Length > 0 && !matches.Contains(activeSettingsSection))
            activeSettingsSection = matches[0];
        foreach (var (key, section) in settingsSections)
        {
            var matched = matches.Contains(key);
            section.Visibility = matched && (classic || narrow || key == activeSettingsSection)
                ? Visibility.Visible : Visibility.Collapsed;
            if (!classic && !narrow && key == activeSettingsSection) section.IsExpanded = true;
            if (!settingsNavigation.TryGetValue(key, out var button)) continue;
            button.Visibility = matched ? Visibility.Visible : Visibility.Collapsed;
            button.FontWeight = key == activeSettingsSection ? FontWeights.SemiBold : FontWeights.Normal;
            button.SetBinding(Control.ForegroundProperty, new Binding(key == activeSettingsSection
                ? nameof(UiThemeState.AccentBrush) : nameof(UiThemeState.TextSecondaryBrush)) { Source = UiThemeState.Instance });
            if (key == activeSettingsSection)
            {
                button.SetBinding(Control.BackgroundProperty, new Binding(nameof(UiThemeState.AccentSoftBackgroundBrush)) { Source = UiThemeState.Instance });
                button.SetBinding(Control.BorderBrushProperty, new Binding(nameof(UiThemeState.AccentBrush)) { Source = UiThemeState.Instance });
            }
            else
            {
                button.ClearValue(Control.BackgroundProperty);
                button.ClearValue(Control.BorderBrushProperty);
            }
        }
    }

    void AddSection(StackPanel host, string key, string title, string detail, string icon,
        string iconBackground, string iconForeground, List<UIElement> controls, bool expanded = false)
    {
        if (controls.Count == 0) return;
        var body = new StackPanel();
        foreach (var control in controls) body.Children.Add(control);
        if (key == "scrape")
        {
            var open = new Button
            {
                Content = "打开搜刮与审核", Style = (Style)FindResource("Action"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 13, 0, 0)
            };
            open.Click += (_, _) => ShowView("scrape");
            body.Children.Add(open);
        }
        if (key == "data")
        {
            body.Children.Add(BuildErrorReportSettingsEntry());
            body.Children.Add(BuildCloudAccountSettingsEntry());
            body.Children.Add(BuildLibraryRelocationSettingsEntry());
            body.Children.Add(BuildLicenseSettingsEntry());
            body.Children.Add(BuildCreditsSettingsEntry());
            body.Children.Add(new TextBlock
            {
                Text = "接口密钥保存在 Windows 凭据管理器；视频资料与备份保存在固定的用户数据目录。",
                FontSize = 11, Foreground = Tint("#8290A5"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0)
            });
        }
        var header = new Grid { MinHeight = 42 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        var iconPlate = new Border
        {
            Background = Tint(iconBackground), Width = 42, Height = 42,
            CornerRadius = new CornerRadius(12), HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Child = new TextBlock { Text = icon, FontSize = 20, FontWeight = FontWeights.SemiBold,
                Foreground = Tint(iconForeground), HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center }
        };
        header.Children.Add(iconPlate);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleLabel = ThemedLabel(title, 15, nameof(UiThemeState.TextPrimaryBrush));
        titleLabel.FontWeight = FontWeights.SemiBold;
        labels.Children.Add(titleLabel);
        var detailLabel = ThemedLabel(detail, 12, nameof(UiThemeState.TextSecondaryBrush));
        detailLabel.Margin = new Thickness(0, 3, 0, 0);
        detailLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        labels.Children.Add(detailLabel);
        Grid.SetColumn(labels, 1);
        header.Children.Add(labels);
        var chevron = ThemedLabel("⌄", 20, nameof(UiThemeState.TextSecondaryBrush));
        chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        Grid.SetColumn(chevron, 2);
        header.Children.Add(chevron);
        var section = new Expander
        {
            Style = (Style)FindResource("SettingsCard"), Header = header, Content = body,
            Tag = title + " " + detail + " " + string.Join(" ", controls.Select(SettingSearchText))
                + (key == "data" ? " 非商业许可 项目许可 关于 来源 致谢 TMDB" : ""),
            IsExpanded = expanded
        };
        // A stable id per card: the header is a composite of text blocks, so the
        // card had no reachable name for keyboard or UI automation.
        System.Windows.Automation.AutomationProperties.SetAutomationId(section, "SettingsSection_" + key);
        section.Expanded += (_, _) =>
        {
            chevron.Text = "⌃";
            var fade = UiMotion.FeedbackMilliseconds(config.UiMotion);
            if (fade > 0) body.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(fade)));
            if (key == "ai") _ = RefreshAiJobsAsync();
        };
        section.Collapsed += (_, _) => chevron.Text = "⌄";
        settingsSections[key] = section;
        host.Children.Add(section);
    }

    static string SettingSearchText(UIElement control)
    {
        if (control is TextBlock label) return label.Text;
        if (control is CheckBox check) return check.Content?.ToString() ?? string.Empty;
        if (control is Button button) return button.Content?.ToString() ?? string.Empty;
        if (control is Border border && border.Child is UIElement child) return SettingSearchText(child);
        if (control is Panel panel) return string.Join(" ", panel.Children.Cast<UIElement>().Select(SettingSearchText));
        return string.Empty;
    }

    static System.Windows.Media.Brush Tint(string color) => UiTheme.FromLegacyHex(color);

    static TextBlock ThemedLabel(string text, double size, string brushProperty)
    {
        var label = new TextBlock { Text = text, FontSize = size };
        label.SetBinding(TextBlock.ForegroundProperty,
            new Binding(brushProperty) { Source = UiThemeState.Instance });
        return label;
    }

    internal (bool Valid, string Detail) VerifySettingsLayout()
    {
        var expected = new[] { "appearance", "library", "scrape", "ai", "playback", "online", "startup", "updates", "data" };
        var missing = expected.Where(key => !settingsSections.ContainsKey(key)).ToArray();
        var options = new[] { "SettingsAppearanceOptions", "SettingsScrapeOptions", "SettingsLibraryOptions", "SettingsStartupOptions", "SettingsUpdateOptions" };
        var unfound = options.Where(name => !settingsSections.Values.SelectMany(section => ((StackPanel)section.Content).Children.Cast<UIElement>())
            .OfType<FrameworkElement>().Any(element => element.Name == name)).ToArray();
        var relocation = VerifyLibraryRelocationUiForDiagnostics();
        var licenseReadable = ReadEmbeddedLicense().Contains("Required Notice: Copyright (c) 2026 PRC1978", StringComparison.Ordinal)
            && ReadEmbeddedLicense().Contains("PolyForm Noncommercial License 1.0.0", StringComparison.Ordinal)
            && ReadEmbeddedWebView2License().Length > 100
            && ReadEmbeddedWebView2Notice().Length > 100
            && ReadEmbeddedDotNetCoreRuntimeLicense().Length > 100
            && ReadEmbeddedDotNetCoreRuntimeNotices().Length > 100
            && ReadEmbeddedWindowsDesktopRuntimeLicense().Length > 100;
        var credits = VerifyCreditsResources()
            && settingsSections.TryGetValue("data", out var dataSection)
            && dataSection.Content is StackPanel dataBody
            && dataBody.Children.OfType<Button>().Any(button => Equals(button.Content, "关于软件与资料来源"));
        var encryptedReports = settingsSections.TryGetValue("data", out var reportSection)
            && reportSection.Content is StackPanel reportBody
            && reportBody.Children.Cast<UIElement>().Any(child =>
                SettingSearchText(child).Contains("加密导出日志包", StringComparison.Ordinal) &&
                !SettingSearchText(child).Contains("上传加密日志包", StringComparison.Ordinal))
            && !reportBody.Children.Cast<UIElement>().Any(child =>
                SettingSearchText(child).Contains("云端账号（错误日志）", StringComparison.Ordinal));
        var valid = missing.Length == 0 && unfound.Length == 0 && settingsSections.Values.All(section =>
            section.Content is StackPanel stack && stack.Children.Count > 0) && relocation && licenseReadable && credits && encryptedReports;
        return (valid, $"主题 {settingsSections.Count}；缺少主题 [{string.Join(", ", missing)}]；缺少选项 [{string.Join(", ", unfound)}]；迁移向导 {(relocation ? "可用" : "不可用")}；内嵌许可 {(licenseReadable ? "可读" : "缺失")}；来源致谢 {(credits ? "可用" : "缺失")}");
    }
}

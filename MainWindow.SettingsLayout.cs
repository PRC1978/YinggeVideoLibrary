using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Panel = System.Windows.Controls.Panel;
using Brushes = System.Windows.Media.Brushes;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    readonly Dictionary<string, Expander> settingsSections = new(StringComparer.Ordinal);

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
        panel.MaxWidth = 940;
        panel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        SettingsPanel.Padding = new Thickness(0);
        SettingsPanel.MaxWidth = 940;
        SettingsPanel.Background = Brushes.Transparent;
        SettingsPanel.BorderThickness = new Thickness(0);
        SettingsPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;

        panel.Children.Add(CreateSettingsIntro());
        AddSection(panel, "appearance", "外观与浏览", "卡片密度、显示方式和界面布局", "◫", "#EAF0FF", "#4C6BDC", buckets["appearance"], true);
        AddSection(panel, "library", "视频库与缓存", "索引、缩略图和重复视频", "▣", "#EAF6F3", "#2D9A82", buckets["library"]);
        AddSection(panel, "scrape", "搜刮与审核", "来源凭据和批量搜刮范围", "⌕", "#FFF3E9", "#CB7A33", buckets["scrape"]);
        AddSection(panel, "ai", "AI 接口与费用", "本地或兼容接口、限速和预算", "✦", "#F2ECFC", "#8660BD", buckets["ai"]);
        AddSection(panel, "playback", "播放与封面", "播放器位置和缩略图生成状态", "▶", "#EBF5FD", "#3987B6", buckets["playback"]);
        AddSection(panel, "online", "在线与扩展", "内嵌浏览器的扩展设置", "◎", "#EEF2FB", "#5879BA", buckets["online"]);
        AddSection(panel, "startup", "启动", "开机自启、最小化与启动页", "↗", "#ECF7F0", "#4A9E67", buckets["startup"]);
        AddSection(panel, "updates", "软件更新", "检查新版本并在应用内更新、重启", "↻", "#EAF2FF", "#4D7BCB", buckets["updates"]);
        AddSection(panel, "data", "数据与备份", "本地备份、恢复和数据目录", "▤", "#F9F0EC", "#B6785A", buckets["data"]);
    }

    FrameworkElement CreateSettingsIntro()
    {
        var grid = new Grid { Margin = new Thickness(2, 0, 2, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var introduction = new StackPanel();
        introduction.Children.Add(new TextBlock
        {
            Text = "按主题找到需要的选项", FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = Tint("#24344F")
        });
        introduction.Children.Add(new TextBlock
        {
            Text = "展开卡片调整设置；需要手动保存的选项保留保存按钮。",
            FontSize = 12, Foreground = Tint("#75859E"), Margin = new Thickness(0, 5, 0, 0)
        });
        grid.Children.Add(introduction);
        var search = new TextBox
        {
            Width = 210, Height = 36, Padding = new Thickness(10, 6, 10, 6),
            BorderBrush = Tint("#DDE6F2"), Background = Brushes.White,
            ToolTip = "搜索设置主题，例如缓存、AI、更新"
        };
        search.TextChanged += (_, _) =>
        {
            var query = search.Text.Trim();
            foreach (var section in settingsSections.Values)
            {
                var text = section.Tag as string ?? string.Empty;
                section.Visibility = query.Length == 0 || text.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
                if (query.Length > 0 && section.Visibility == Visibility.Visible) section.IsExpanded = true;
            }
        };
        Grid.SetColumn(search, 1);
        grid.Children.Add(search);
        return grid;
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
        labels.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = Tint("#273851") });
        labels.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = Tint("#8190A6"),
            Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(labels, 1);
        header.Children.Add(labels);
        var chevron = new TextBlock { Text = "⌄", FontSize = 20, Foreground = Tint("#7B8AA0"),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        Grid.SetColumn(chevron, 2);
        header.Children.Add(chevron);
        var section = new Expander
        {
            Style = (Style)FindResource("SettingsCard"), Header = header, Content = body,
            Tag = title + " " + detail + " " + string.Join(" ", controls.Select(SettingSearchText))
                + (key == "data" ? " 非商业许可 项目许可 关于 来源 致谢 TMDB" : ""),
            IsExpanded = expanded
        };
        section.Expanded += (_, _) =>
        {
            chevron.Text = "⌃";
            body.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(160)));
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

    static System.Windows.Media.Brush Tint(string color) => (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(color)!;

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
        var valid = missing.Length == 0 && unfound.Length == 0 && settingsSections.Values.All(section =>
            section.Content is StackPanel stack && stack.Children.Count > 0) && relocation && licenseReadable && credits;
        return (valid, $"主题 {settingsSections.Count}；缺少主题 [{string.Join(", ", missing)}]；缺少选项 [{string.Join(", ", unfound)}]；迁移向导 {(relocation ? "可用" : "不可用")}；内嵌许可 {(licenseReadable ? "可读" : "缺失")}；来源致谢 {(credits ? "可用" : "缺失")}");
    }
}

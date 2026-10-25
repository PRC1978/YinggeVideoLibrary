using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    sealed record StartupPageOption(string Id, string Label);

    static readonly StartupPageOption[] StartupPages =
    [
        new("all", "全部视频"), new("recent", "最近更新"), new("favorites", "收藏"),
        new("recommendations", "为你推荐"), new("categories", "分类"),
        new("folders", "视频文件夹"), new("status", "状态"),
        new("scrape", "搜刮"), new("online", "在线"), new("settings", "设置")
    ];

    TextBlock? startupStatus;
    Button? startupUseCurrentButton;
    Forms.NotifyIcon? startupTrayIcon;
    Forms.ContextMenuStrip? startupTrayMenu;
    Drawing.Icon? startupTrayDrawingIcon;
    bool startupOptionsUpdating;

    internal static string NormalizeStartupPage(string? page) =>
        StartupPages.Any(option => option.Id == page) ? page! : "all";

    void InitializeStartupBehavior()
    {
        StateChanged += (_, _) =>
        {
            if (ShouldHideInTray(IsVisible, WindowState, config.MinimizeToTray, isClosing))
                HideInTray();
            else if (IsVisible && WindowState == WindowState.Normal)
                Dispatcher.BeginInvoke(new Action(PresentDeferredUpdateIfAvailable));
        };
        Closed += (_, _) =>
        {
            if (startupTrayIcon is not null)
            {
                startupTrayIcon.Visible = false;
                startupTrayIcon.Dispose();
                startupTrayIcon = null;
            }
            startupTrayMenu?.Dispose();
            startupTrayMenu = null;
            startupTrayDrawingIcon?.Dispose();
            startupTrayDrawingIcon = null;
        };
    }

    internal static bool ShouldStartMinimized(bool launchedAtLogin, bool autoStart, bool startMinimized) =>
        launchedAtLogin && autoStart && startMinimized;

    internal static bool ShouldHideInTray(bool visible, WindowState state, bool minimizeToTray,
        bool closing) => visible && state == WindowState.Minimized && minimizeToTray && !closing;

    internal static Forms.ContextMenuStrip CreateTrayContextMenu(Action restore, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开映格", null, (_, _) => restore());
        menu.Items.Add("退出映格", null, (_, _) => exit());
        return menu;
    }

    internal void ApplyStartupWindowPreferences(bool launchedAtLogin)
    {
        if (!ShouldStartMinimized(launchedAtLogin, config.AutoStart, config.StartMinimized)) return;
        WindowState = WindowState.Minimized;
        if (config.MinimizeToTray)
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ShouldHideInTray(IsVisible, WindowState, config.MinimizeToTray, isClosing)) HideInTray();
            }));
    }

    internal void ReconcileStartupRegistration()
    {
        var message = StartupRegistration.Reconcile(config.AutoStart);
        RefreshStartupStatus(message);
    }

    void HideInTray()
    {
        try
        {
            if (startupTrayIcon is null)
            {
                var path = Environment.ProcessPath;
                startupTrayDrawingIcon = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                    ? Drawing.Icon.ExtractAssociatedIcon(path) : null;
                startupTrayMenu = CreateTrayContextMenu(
                    () => Dispatcher.BeginInvoke(RestoreFromTray),
                    () => Dispatcher.BeginInvoke(new Action(Close)));
                startupTrayIcon = new Forms.NotifyIcon
                {
                    Icon = startupTrayDrawingIcon ?? Drawing.SystemIcons.Application,
                    Text = "映格 · 本地视频库",
                    ContextMenuStrip = startupTrayMenu
                };
                startupTrayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);
            }
            startupTrayIcon.Visible = true;
            Hide();
        }
        catch (Exception ex)
        {
            if (startupTrayIcon is not null) startupTrayIcon.Visible = false;
            RefreshStartupStatus("无法最小化到托盘：" + ex.Message);
            Show();
        }
    }

    void RestoreFromTray()
    {
        if (startupTrayIcon is not null) startupTrayIcon.Visible = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        PresentDeferredUpdateIfAvailable();
    }

    StackPanel BuildStartupOptions()
    {
        if (AutoStartBox.Parent is System.Windows.Controls.Panel oldParent) oldParent.Children.Remove(AutoStartBox);
        var section = new StackPanel { Name = "SettingsStartupOptions", Margin = new Thickness(0, 8, 0, 4) };
        AutoStartBox.Content = null;
        AutoStartBox.Style = (Style)FindResource("StartupSwitch");
        AutoStartBox.Margin = new Thickness(0);
        AutoStartBox.IsChecked = config.AutoStart;
        section.Children.Add(StartupRow("开机自启", "当前 Windows 用户登录后启动映格。",
            StartupToggle(AutoStartBox, "开机自启", "当前 Windows 用户登录后启动映格。")));

        var tray = new CheckBox { IsChecked = config.MinimizeToTray,
            Style = (Style)FindResource("StartupSwitch") };
        tray.Checked += (_, _) => SaveStartupToggle(() => config.MinimizeToTray = true);
        tray.Unchecked += (_, _) => SaveStartupToggle(() => config.MinimizeToTray = false);
        section.Children.Add(StartupRow("最小化到系统托盘", "最小化后留在通知区域；双击图标可恢复，右键图标可退出。",
            StartupToggle(tray, "最小化到系统托盘", "最小化后留在通知区域；双击图标可恢复，右键图标可退出。")));

        var minimized = new CheckBox { IsChecked = config.StartMinimized,
            Style = (Style)FindResource("StartupSwitch") };
        minimized.Checked += (_, _) => SaveStartupToggle(() => config.StartMinimized = true);
        minimized.Unchecked += (_, _) => SaveStartupToggle(() => config.StartMinimized = false);
        section.Children.Add(StartupRow("开机自启后最小化", "仅在 Windows 登录自动启动时最小化；开启托盘选项时会进入通知区域。",
            StartupToggle(minimized, "开机自启后最小化", "仅在 Windows 登录自动启动时最小化；开启托盘选项时会进入通知区域。")));

        var page = new System.Windows.Controls.ComboBox
        {
            Width = 145, Height = 34, ItemsSource = StartupPages,
            DisplayMemberPath = nameof(StartupPageOption.Label),
            SelectedValuePath = nameof(StartupPageOption.Id),
            SelectedValue = NormalizeStartupPage(config.StartPage)
        };
        page.SelectionChanged += (_, _) =>
        {
            if (page.SelectedValue is not string selected || selected == config.StartPage || !IsLoaded) return;
            config.StartPage = selected;
            SaveConfig();
        };
        section.Children.Add(StartupRow("启动页", "下次打开时默认显示的页面。", page));

        startupStatus = SettingsHint("");
        section.Children.Add(startupStatus);
        startupUseCurrentButton = new Button
        {
            Content = "将开机自启改为当前程序", Style = (Style)FindResource("Action"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        startupUseCurrentButton.Click += (_, _) =>
        {
            if (!config.AutoStart) return;
            RefreshStartupStatus(StartupRegistration.TrySetEnabled(true, out var error)
                ? "已将开机自启改为当前程序。" : "设置失败：" + error);
        };
        section.Children.Add(startupUseCurrentButton);
        var desktopShortcut = new Button
        {
            Content = "创建桌面快捷方式", Style = (Style)FindResource("Action"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 5, 0, 0)
        };
        desktopShortcut.Click += (_, _) =>
        {
            try { RefreshStartupStatus("桌面快捷方式已创建：" + StartupRegistration.CreateDesktopShortcut()); }
            catch (Exception ex) { RefreshStartupStatus("创建桌面快捷方式失败：" + ex.Message); }
        };
        section.Children.Add(desktopShortcut);
        RefreshStartupStatus();
        return section;
    }

    Border StartupRow(string title, string description, FrameworkElement input)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold,
            Foreground = Tint("#273851") });
        labels.Children.Add(new TextBlock { Text = description, FontSize = 11,
            Foreground = Tint("#8190A6"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 10, 0) });
        row.Children.Add(labels);
        input.VerticalAlignment = VerticalAlignment.Center;
        input.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        Grid.SetColumn(input, 1);
        row.Children.Add(input);
        return new Border { Background = System.Windows.Media.Brushes.White, BorderBrush = Tint("#E2E8F1"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(14, 13, 14, 13), Margin = new Thickness(0, 0, 0, 9), Child = row };
    }

    FrameworkElement StartupToggle(CheckBox toggle, string title, string description)
    {
        System.Windows.Automation.AutomationProperties.SetName(toggle, title);
        System.Windows.Automation.AutomationProperties.SetHelpText(toggle, description);
        toggle.ToolTip = title;
        var status = new TextBlock
        {
            Text = toggle.IsChecked == true ? "开" : "关",
            MinWidth = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Tint("#52647A"),
            Margin = new Thickness(5, 0, 0, 0)
        };
        toggle.Checked += (_, _) => status.Text = "开";
        toggle.Unchecked += (_, _) => status.Text = "关";
        var wrapper = new StackPanel { Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center };
        wrapper.Children.Add(toggle);
        wrapper.Children.Add(status);
        return wrapper;
    }

    void SaveStartupToggle(Action update)
    {
        if (!IsLoaded || startupOptionsUpdating) return;
        update();
        SaveConfig();
    }

    void RefreshStartupStatus(string? message = null)
    {
        if (startupStatus is null) return;
        string status;
        try
        {
            var target = StartupRegistration.RegisteredExecutable();
            var current = Environment.ProcessPath;
            var currentIsPublished = PublishedExecutable.IsThisApplication(current);
            var other = currentIsPublished && config.AutoStart && !string.IsNullOrWhiteSpace(target)
                && File.Exists(target) && !string.Equals(Path.GetFullPath(target), current,
                    StringComparison.OrdinalIgnoreCase);
            status = !config.AutoStart ? "开机自启已关闭。" : !currentIsPublished
                ? "当前为源码运行；请在发布版程序中配置开机自启。" : other
                ? "开机自启当前指向：" + target
                : string.IsNullOrWhiteSpace(target) ? "开机自启等待注册。" : "开机自启已指向当前程序。";
            if (startupUseCurrentButton is not null)
                startupUseCurrentButton.Visibility = other ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { status = "无法读取开机自启状态：" + ex.Message; }
        startupStatus.Text = string.IsNullOrWhiteSpace(message) ? status : message + " " + status;
    }
}

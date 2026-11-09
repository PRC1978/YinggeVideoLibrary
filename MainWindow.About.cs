using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    FrameworkElement BuildAboutFooter()
    {
        var body = new StackPanel { Margin = new Thickness(4, 20, 4, 20) };
        body.Children.Add(new TextBlock { Text = "关于映格视频库", FontSize = 19,
            FontWeight = FontWeights.SemiBold, Foreground = UiTheme.TextPrimaryBrush });
        body.Children.Add(new TextBlock { Text = "版本 " + typeof(MainWindow).Assembly.GetName().Version,
            Foreground = UiTheme.TextSecondaryBrush, Margin = new Thickness(0, 6, 0, 10) });
        var links = new WrapPanel();
        void Link(string label, Action action)
        {
            var button = new Button { Content = label, Style = (Style)FindResource("Action") };
            button.Click += (_, _) => action();
            links.Children.Add(button);
        }
        Link("关于与数据目录", () => ShowAboutWindow(false));
        Link("隐私政策", () => ShowAboutWindow(true));
        Link("仓库地址", () => OpenAboutUrl("https://github.com/PRC1978/YinggeVideoLibrary"));
        Link("更新记录", () => OpenAboutUrl("https://github.com/PRC1978/YinggeVideoLibrary/releases"));
        Link("许可与第三方声明", ShowLicenseDialog);
        body.Children.Add(links);
        return body;
    }

    void OpenAboutUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { ThemeMessageBox.Show(this, "无法打开链接：" + ex.Message,
            "打开链接", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void ShowAboutWindow(bool privacy)
    {
        var dialog = new Window { Owner = this, Title = privacy ? "隐私政策" : "关于映格视频库",
            Width = 700, Height = 560, MinWidth = 400, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = UiTheme.SurfaceBrush, Foreground = UiTheme.TextPrimaryBrush };
        var body = new StackPanel { Margin = new Thickness(28) };
        void Section(string title, string text)
        {
            body.Children.Add(new TextBlock { Text = title, FontSize = 18,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 8) });
            body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
                FontSize = 13, Foreground = UiTheme.TextSecondaryBrush, LineHeight = 22 });
        }
        Section(privacy ? "隐私政策" : "映格视频库", privacy
            ? "视频库在本机管理。使用搜刮、AI、在线浏览或云端账号时，相关请求会发送到你所选择的服务。"
            : "Windows 本地视频管理工具\n版本 " + typeof(MainWindow).Assembly.GetName().Version);
        Section("本地数据与凭据", "索引、作品资料、人物、标签、审核历史、队列、缓存及数据库保存在当前数据目录。API Key、数据库凭据和云端会话令牌使用 Windows 凭据管理器保存。\n\n当前数据目录：\n" + DataDir);
        Section("搜刮与 AI", "搜刮会向选定来源发送标题或番号。AI 会按所选接口处理匹配证据；请在开始前检查费用及隐私预览。第三方网站和模型服务适用各自的政策。");
        Section("云端账号、头像与错误报告", "云端账号用于邀请注册、错误报告和你主动设置的账号头像同步。上传错误日志前会显示脱敏预览；更换头像时只上传裁切并重新编码后的 PNG，不上传原图路径或元数据。视频文件、视频封面、视频库索引和 API Key 不会上传。你可以在账号区域删除头像或查看、删除自己的报告。");
        Section("保留与删除", "退出登录可清除本机保存的云端会话。卸载时请核对数据保留或删除选项；删除数据目录前应备份，视频原文件不属于程序数据清理范围。服务器报告的保留状态以服务返回的到期时间为准。");
        var actions = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        var open = new Button { Content = "打开当前数据目录", Style = (Style)FindResource("Action") };
        open.Click += (_, _) => OpenDataDirectory(open, new RoutedEventArgs());
        var close = new Button { Content = "关闭", IsCancel = true, Style = (Style)FindResource("Action") };
        close.Click += (_, _) => dialog.Close();
        actions.Children.Add(open); actions.Children.Add(close); body.Children.Add(actions);
        dialog.Content = new ScrollViewer { Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        dialog.ShowDialog();
    }
}

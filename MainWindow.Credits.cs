using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    const string TmdbAttribution =
        "This product uses the TMDB API but is not endorsed or certified by TMDB.";

    internal static bool VerifyCreditsResources()
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri(
            "pack://application:,,,/third-party/TMDB/tmdb-primary-short-blue.png",
            UriKind.Absolute))?.Stream;
        if (stream is null) return false;
        var signature = new byte[8];
        return stream.Read(signature, 0, signature.Length) == signature.Length
            && signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && TmdbAttribution ==
                "This product uses the TMDB API but is not endorsed or certified by TMDB.";
    }

    FrameworkElement BuildCreditsSettingsEntry()
    {
        var button = new Button
        {
            Content = "关于软件与资料来源",
            Style = (Style)FindResource("Action"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        button.Click += (_, _) => ShowCreditsDialog();
        return button;
    }

    void ShowCreditsDialog()
    {
        var dialog = new Window
        {
            Title = "关于映格视频库 · 来源与致谢",
            Owner = this,
            Width = 620, Height = 390, MinWidth = 460, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White
        };
        var root = new StackPanel { Margin = new Thickness(26) };
        root.Children.Add(new TextBlock
        {
            Text = "映格视频库", FontSize = 24, FontWeight = FontWeights.Bold,
            Foreground = Tint("#263A5A")
        });
        root.Children.Add(new TextBlock
        {
            Text = $"本地视频管理 · 版本 {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · 项目采用非商业使用许可",
            FontSize = 12, Foreground = Tint("#64748B"),
            Margin = new Thickness(0, 5, 0, 19)
        });
        root.Children.Add(new TextBlock
        {
            Text = "资料来源与致谢", FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        var tmdbLogo = new Image
        {
            Source = new BitmapImage(new Uri(
                "pack://application:,,,/third-party/TMDB/tmdb-primary-short-blue.png",
                UriKind.Absolute)),
            Width = 145, Height = 25, Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        root.Children.Add(tmdbLogo);
        root.Children.Add(new TextBlock
        {
            Text = TmdbAttribution, TextWrapping = TextWrapping.Wrap,
            FontSize = 12, Foreground = Tint("#4B5563"),
            Margin = new Thickness(0, 9, 0, 3)
        });
        root.Children.Add(new TextBlock
        {
            Text = "TMDB 仅为启用该来源时的电影、剧集与人物资料提供者；其标志与资料仍归各自权利人。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Foreground = Tint("#64748B"), Margin = new Thickness(0, 0, 0, 12)
        });
        var website = new Button
        {
            Content = "打开 TMDB 官网",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Style = (Style)FindResource("Action")
        };
        website.Click += (_, _) => Process.Start(new ProcessStartInfo(
            "https://www.themoviedb.org") { UseShellExecute = true });
        root.Children.Add(website);
        root.Children.Add(new TextBlock
        {
            Text = "TVmaze · 剧集与人物资料",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 4)
        });
        root.Children.Add(new TextBlock
        {
            Text = "启用 TVmaze 时，剧集与人物资料来自 TVmaze，依其 CC BY-SA 条款使用；具体资料页保留来源链接。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Foreground = Tint("#4B5563"), Margin = new Thickness(0, 0, 0, 8)
        });
        var tvmazeWebsite = new Button
        {
            Content = "打开 TVmaze 官网",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Style = (Style)FindResource("Action")
        };
        tvmazeWebsite.Click += (_, _) => Process.Start(new ProcessStartInfo(
            "https://www.tvmaze.com") { UseShellExecute = true });
        root.Children.Add(tvmazeWebsite);
        root.Children.Add(new TextBlock
        {
            Text = "第三方组件许可可在“开源许可”中查看。",
            FontSize = 11, Foreground = Tint("#64748B"),
            Margin = new Thickness(0, 17, 0, 0)
        });
        dialog.Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        dialog.ShowDialog();
    }
}

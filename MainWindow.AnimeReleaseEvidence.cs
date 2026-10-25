using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using StackPanel = System.Windows.Controls.StackPanel;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    // A release is evidence about one episode or subtitle group, not a work
    // metadata record. It is displayed beside the metadata controls and never
    // enters the approval queue or overwrites the work's title and rating.
    void InitializeAnimeReleaseEvidence()
    {
        if (ScrapePanel.Child is not StackPanel panel || mikanEnabledBox?.Parent is not StackPanel sourceGroup)
            return;

        var section = new StackPanel { Margin = new Thickness(0, 7, 0, 18) };
        section.Children.Add(new Separator { Margin = new Thickness(0, 5, 0, 14) });
        section.Children.Add(new TextBlock
        {
            Text = "番剧发布线索 · 动漫花园", FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        section.Children.Add(new TextBlock
        {
            Text = "按番名查看公开 RSS 中的字幕组、集数和发布时间。线索只供核对，不会覆盖作品资料或下载资源。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 10)
        });

        var query = new TextBox
        {
            Height = 36, Padding = new Thickness(8),
            ToolTip = "输入番剧名称；只会向动漫花园发送这里显示的关键词"
        };
        section.Children.Add(query);
        var actions = new WrapPanel { Margin = new Thickness(0, 7, 0, 5) };
        var fromVideo = new Button { Content = "从视频填入番名", Style = (Style)FindResource("Action") };
        var search = new Button { Content = "查询发布线索", Style = (Style)FindResource("Primary") };
        var cancel = new Button { Content = "取消查询", Style = (Style)FindResource("Action"), IsEnabled = false };
        actions.Children.Add(fromVideo);
        actions.Children.Add(search);
        actions.Children.Add(cancel);
        section.Children.Add(actions);
        var status = new TextBlock
        {
            Text = "输入番名后手动查询。", Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6)
        };
        section.Children.Add(status);
        var rows = new StackPanel();
        section.Children.Add(new ScrollViewer
        {
            Content = rows, Height = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 236, 242)),
            BorderThickness = new Thickness(1), Padding = new Thickness(7)
        });

        CancellationTokenSource? running = null;
        fromVideo.Click += (_, _) =>
        {
            var video = ChooseVideoForTitleScrape("选择番剧视频", "填入番名");
            if (video is null) return;
            query.Text = AnimeReleaseTitleParser.CleanWorkTitle(video.DisplayTitle);
            status.Text = $"已填入《{query.Text}》；点击查询后才会访问动漫花园。";
        };
        cancel.Click += (_, _) => running?.Cancel();
        search.Click += async (_, _) =>
        {
            if (dmhyEnabledBox?.IsChecked != true)
            {
                status.Text = "请先勾选“启用动漫花园发布线索手动查询”。";
                return;
            }
            string title;
            try { title = AnimeReleaseTitleParser.CleanWorkTitle(query.Text); }
            catch (Exception ex) { status.Text = ex.Message; return; }
            if (string.IsNullOrWhiteSpace(title))
            {
                status.Text = "请输入可识别的番剧名称。";
                return;
            }
            rows.Children.Clear();
            search.IsEnabled = false;
            fromVideo.IsEnabled = false;
            cancel.IsEnabled = true;
            running = new CancellationTokenSource();
            status.Text = $"正在查询《{title}》的公开发布线索…";
            try
            {
                var releases = await RunQueueSourceAsync("DMHY",
                    () => DmhyAnimeReleaseScraper.SearchAsync(title, running.Token), running.Token);
                RefreshSourceDiagnostics();
                status.Text = releases.Count == 0
                    ? "未找到发布线索；作品信息和视频状态未改变。"
                    : $"找到 {releases.Count} 条发布线索，显示前 {Math.Min(releases.Count, 40)} 条。请核对番名、季度与集数后再查看来源页。";
                foreach (var release in releases.Take(40))
                {
                    var card = new StackPanel { Margin = new Thickness(3, 5, 3, 10) };
                    card.Children.Add(new TextBlock
                    {
                        Text = release.WorkTitle.Length > 0 ? release.WorkTitle : release.ReleaseTitle,
                        FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
                    });
                    var details = new List<string>();
                    if (release.Season is int season) details.Add($"第 {season} 季");
                    if (!string.IsNullOrWhiteSpace(release.Episode)) details.Add($"第 {release.Episode} 集");
                    if (!string.IsNullOrWhiteSpace(release.SubtitleGroup)) details.Add(release.SubtitleGroup);
                    if (!string.IsNullOrWhiteSpace(release.Resolution)) details.Add(release.Resolution);
                    if (release.PublishedAt is DateTimeOffset published) details.Add(published.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                    card.Children.Add(new TextBlock
                    {
                        Text = details.Count == 0 ? "发布条目" : string.Join(" · ", details),
                        Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2)
                    });
                    card.Children.Add(new TextBlock
                    {
                        Text = release.ReleaseTitle, Foreground = Brushes.SlateGray,
                        TextWrapping = TextWrapping.Wrap, MaxHeight = 48
                    });
                    var open = new Button
                    {
                        Content = "打开发布页", Style = (Style)FindResource("Action"),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                        Margin = new Thickness(0, 5, 0, 0)
                    };
                    open.Click += (_, _) =>
                    {
                        if (!Uri.TryCreate(release.SourceUrl, UriKind.Absolute, out var uri) ||
                            uri.Scheme != Uri.UriSchemeHttps ||
                            !uri.Host.Equals("share.dmhy.org", StringComparison.OrdinalIgnoreCase) ||
                            uri.UserInfo.Length > 0 || !uri.IsDefaultPort) return;
                        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                        catch (Exception ex) { status.Text = "无法打开来源页：" + ex.Message; }
                    };
                    card.Children.Add(open);
                    rows.Children.Add(card);
                    rows.Children.Add(new Separator());
                }
            }
            catch (OperationCanceledException) { status.Text = "已取消发布线索查询。"; }
            catch (Exception ex) { status.Text = "查询失败：" + ex.Message; RefreshSourceDiagnostics(); }
            finally
            {
                running.Dispose(); running = null;
                search.IsEnabled = true;
                fromVideo.IsEnabled = true;
                cancel.IsEnabled = false;
            }
        };

        panel.Children.Insert(panel.Children.IndexOf(sourceGroup) + 1, section);
    }
}

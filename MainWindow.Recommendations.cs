using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    private const int RecommendationPageSize = 16;
    private int recommendationVersion;
    private int recommendationPage;
    private IReadOnlyList<LocalRecommendation> rankedRecommendations = [];
    private Dictionary<string, VideoItem> recommendationVideos = new(StringComparer.OrdinalIgnoreCase);
    private StackPanel? recommendationCardsSection;

    void NavRecommendations(object sender, RoutedEventArgs e) => ShowView("recommendations");

    async void RenderRecommendations()
    {
        var version = ++recommendationVersion;
        recommendationPage = 0;
        rankedRecommendations = [];
        recommendationCardsSection = null;
        RecommendationPanel.Children.Clear();
        RecommendationPanel.Children.Add(new TextBlock
        {
            Text = "正在整理本机的视频偏好…",
            Foreground = Brush("#7286A1"),
            FontSize = 13,
            Margin = new Thickness(0, 14, 0, 0)
        });

        // Capturing the small immutable fields must happen on the UI thread;
        // the scoring and topic aggregation then run in the background.
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
        var snapshot = videos.Select(RecommendationSnapshot.Capture).ToArray();
        try
        {
            var result = await Task.Run(() => (
                Local: RecommendationEngine.Rank(snapshot, snapshot.Length),
                Topics: RecommendationEngine.BuildBilibiliTopics(snapshot, 6)));
            if (version != recommendationVersion || view != "recommendations") return;
            rankedRecommendations = result.Local;
            recommendationVideos = videos.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            RecommendationPanel.Children.Clear();
            CountText.Text = $"从 {snapshot.Length:N0} 部视频中挑选";
            RenderRecommendationIntro(snapshot.Length);
            RenderRecommendationTopics(result.Topics);
            RenderRecommendationCards();
        }
        catch (Exception ex)
        {
            if (version != recommendationVersion || view != "recommendations") return;
            RecommendationPanel.Children.Clear();
            RecommendationPanel.Children.Add(new TextBlock
            {
                Text = "暂时无法生成推荐：" + ex.Message,
                Foreground = Brush("#A94D4D"), TextWrapping = TextWrapping.Wrap
            });
        }
    }

    void RenderRecommendationIntro(int count)
    {
        var header = new Border
        {
            Background = new LinearGradientBrush(ColorOf("#213A66"), ColorOf("#4666C8"), 0),
            CornerRadius = new CornerRadius(20), Padding = new Thickness(22, 19, 22, 18),
            Margin = new Thickness(0, 0, 0, 16)
        };
        var layout = new DockPanel();
        var refresh = new Button
        {
            Content = "↻ 更新推荐", Padding = new Thickness(13, 8, 13, 8),
            BorderThickness = new Thickness(0), Background = Brush("#E6FFFFFF"),
            Foreground = Brush("#25375C"), VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        refresh.Click += (_, _) => RenderRecommendations();
        DockPanel.SetDock(refresh, Dock.Right);
        layout.Children.Add(refresh);
        var copy = new StackPanel();
        copy.Children.Add(new TextBlock { Text = "为你挑选", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        copy.Children.Add(new TextBlock
        {
            Text = count == 0 ? "添加本地视频后，这里会给出推荐。" : "根据收藏、观看状态、标签、分类和新近程度，在本机挑选。",
            FontSize = 12, Foreground = Brush("#E5EBFF"), Margin = new Thickness(0, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(copy);
        header.Child = layout;
        RecommendationPanel.Children.Add(header);
    }

    void RenderRecommendationTopics(IReadOnlyList<BilibiliRecommendationTopic> topics)
    {
        var shell = new Border
        {
            Background = Brushes.White, BorderBrush = Brush("#E3E9F3"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(17), Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 0, 20)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "发现在线主题", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brush("#263347") });
        stack.Children.Add(new TextBlock
        {
            Text = "只在点击主题时，向哔哩哔哩发送该按钮所写的通用关键词；不会发送本地视频文件名、路径或自定义标签。",
            FontSize = 12, Foreground = Brush("#7286A1"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 13)
        });
        var buttons = new WrapPanel();
        foreach (var topic in topics)
        {
            var button = new Button
            {
                Content = "⌕ " + topic.Keyword, ToolTip = topic.Reason,
                Padding = new Thickness(13, 8, 13, 8),
                Margin = new Thickness(0, 0, 9, 9),
                Background = Brush("#EDF3FF"), Foreground = Brush("#405DAD"),
                BorderBrush = Brush("#D7E3FF"), BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            var keyword = topic.Keyword;
            button.Click += (_, _) =>
            {
                onlineUrl = "https://search.bilibili.com/all?keyword=" + Uri.EscapeDataString(keyword);
                ShowView("online");
            };
            buttons.Children.Add(button);
        }
        stack.Children.Add(buttons);
        shell.Child = stack;
        RecommendationPanel.Children.Add(shell);
    }

    void RenderRecommendationCards()
    {
        if (recommendationCardsSection is not null)
            RecommendationPanel.Children.Remove(recommendationCardsSection);
        var section = new StackPanel();
        recommendationCardsSection = section;
        var pageCount = Math.Max(1, (rankedRecommendations.Count + RecommendationPageSize - 1) / RecommendationPageSize);
        recommendationPage = Math.Clamp(recommendationPage, 0, pageCount - 1);
        var header = new DockPanel { Margin = new Thickness(0, 2, 0, 5) };
        var pager = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var previous = new Button
        {
            Content = "上一批", Padding = new Thickness(10, 5, 10, 5),
            Background = Brushes.White, BorderBrush = Brush("#D7E3FF"),
            Foreground = Brush("#405DAD"), IsEnabled = recommendationPage > 0,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        previous.Click += (_, _) => { recommendationPage--; RenderRecommendationCards(); };
        pager.Children.Add(previous);
        pager.Children.Add(new TextBlock
        {
            Text = $"{recommendationPage + 1} / {pageCount}",
            Foreground = Brush("#7286A1"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        });
        var next = new Button
        {
            Content = "换一批", Padding = new Thickness(10, 5, 10, 5),
            Background = Brush("#EDF3FF"), BorderBrush = Brush("#D7E3FF"),
            Foreground = Brush("#405DAD"), IsEnabled = recommendationPage + 1 < pageCount,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        next.Click += (_, _) => { recommendationPage++; RenderRecommendationCards(); };
        pager.Children.Add(next);
        DockPanel.SetDock(pager, Dock.Right);
        header.Children.Add(pager);
        header.Children.Add(new TextBlock
        {
            Text = "本地推荐", FontSize = 17, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#263347"), VerticalAlignment = VerticalAlignment.Center
        });
        section.Children.Add(header);
        section.Children.Add(new TextBlock
        {
            Text = "按兴趣、观看状态和分类挑选；每批尽量呈现不同分类。推荐会随你的收藏与观看记录变化。",
            FontSize = 12, Foreground = Brush("#7286A1"), Margin = new Thickness(0, 0, 0, 14)
        });
        if (rankedRecommendations.Count == 0)
        {
            section.Children.Add(new TextBlock { Text = "还没有可推荐的视频。", Foreground = Brush("#7286A1") });
            RecommendationPanel.Children.Add(section);
            return;
        }

        var cards = new WrapPanel();
        foreach (var recommendation in rankedRecommendations.Skip(recommendationPage * RecommendationPageSize).Take(RecommendationPageSize))
        {
            if (!recommendationVideos.TryGetValue(recommendation.VideoId, out var video)) continue;
            var card = new Border
            {
                Width = 251, MinHeight = 150, Margin = new Thickness(0, 0, 14, 14),
                Background = Brushes.White, BorderBrush = Brush("#E2E9F4"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14)
            };
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = video.DisplayTitle, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#263347"), TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 38, TextWrapping = TextWrapping.Wrap,
                ToolTip = video.DisplayTitle
            });
            content.Children.Add(new TextBlock
            {
                Text = video.WatchStatus + (video.Favorite ? "  ·  已收藏" : ""),
                FontSize = 11, Foreground = Brush("#8390A3"), Margin = new Thickness(0, 8, 0, 0)
            });
            content.Children.Add(new TextBlock
            {
                Text = recommendation.Reason, FontSize = 11, Foreground = Brush("#4F68AD"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 9, 0, 11), ToolTip = recommendation.Reason
            });
            var play = new Button
            {
                Content = "▶ 播放", Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Background = Brush("#EDF3FF"), Foreground = Brush("#405DAD"),
                BorderBrush = Brush("#D7E3FF"), BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            play.Click += (_, _) => LaunchVideo(video);
            content.Children.Add(play);
            card.Child = content;
            cards.Children.Add(card);
        }
        section.Children.Add(cards);
        RecommendationPanel.Children.Add(section);
    }

    private static SolidColorBrush Brush(string color) => new(ColorOf(color));
    private static System.Windows.Media.Color ColorOf(string color) => (System.Windows.Media.Color)ColorConverter.ConvertFromString(color);
}

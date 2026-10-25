using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Binding = System.Windows.Data.Binding;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    private const int RecommendationPageSize = 16;
    private static readonly Regex RecommendationCodeOnly = new(
        @"^(?:(?:VID|VIDEO|CLIP|MOVIE|FILE)[\s._-]*)?\d{2,}(?:[\s._-]\d+)*$|^(?:FC2[\s._-]*PPV[\s._-]*)?\d{5,}(?:[\s._-]\d+)*$|^[A-Z]{1,12}[\s._-]?\d{2,10}(?:[\s._-]\d+)*$|^FC2[\s._-]*PPV[\s._-]*\d+(?:[\s._-]\d+)*$|^\d[\d\s._-]{8,}(?:\s+(?:new|final|copy|fixed|edited))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RecommendationLeadingCode = new(
        @"^(?<code>(?:FC2[\s._-]*PPV[\s._-]*\d+(?:[\s._-]\d+)*|[A-Z]{1,12}[\s._-]?\d{2,10}(?:[\s._-]\d+)*))[\s._:：-]+(?<name>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private int recommendationVersion;
    private int recommendationPage;
    private int recommendationVisiblePosterCount;
    private IReadOnlyList<LocalRecommendation> rankedRecommendations = [];
    private Dictionary<string, VideoItem> recommendationVideos = new(StringComparer.OrdinalIgnoreCase);
    private StackPanel? recommendationCardsSection;

    private RecommendationOptions CurrentRecommendationOptions() => new(
        config.RecommendationUseFavorites,
        config.RecommendationUseWatchStatus,
        config.RecommendationUseCategories,
        config.RecommendationUseTags,
        config.RecommendationOnlineTopics,
        new HashSet<string>(config.RecommendationLessLikeVideoIds, StringComparer.OrdinalIgnoreCase));

    private sealed record RecommendationCardTitle(string Name, string Source, string? Code);

    private static string? ReadableRecommendationTitle(string? value, string? code)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var title = AdditionalScrapers.CleanTitle(value).Trim();
        if (!string.IsNullOrWhiteSpace(code) && title.StartsWith(code, StringComparison.OrdinalIgnoreCase))
            title = title[code.Length..].Trim(' ', '-', '_', '.', ':', '：', '·');
        return title.Length >= 2 && !RecommendationCodeOnly.IsMatch(title) && !ScrapeSearchHints.IsWeakTitle(title)
            ? title : null;
    }

    private static string? RecommendationFilenameCode(string title)
    {
        title = title.Trim();
        if (RecommendationCodeOnly.IsMatch(title)) return title;
        var match = RecommendationLeadingCode.Match(title);
        return match.Success ? match.Groups["code"].Value : null;
    }

    private RecommendationCardTitle RecommendationTitle(VideoItem video)
    {
        if (config.DisplayTitleOverrides.TryGetValue(video.Id, out var manualTitle)
            && !string.IsNullOrWhiteSpace(manualTitle))
            return new RecommendationCardTitle(manualTitle, "手动番名",
                video.Code ?? RecommendationFilenameCode(video.Title));
        metadata.TryGetValue(video.Id, out var accepted);
        var acceptedByHuman = config.AiReviews.TryGetValue(video.Id, out var review) && review.Status == "approved";
        var verified = accepted is not null && (accepted.DetailIdentityVerified || acceptedByHuman);
        var code = !string.IsNullOrWhiteSpace(accepted?.Code) ? accepted.Code.Trim()
            : !string.IsNullOrWhiteSpace(video.Code) ? video.Code.Trim()
            : RecommendationFilenameCode(video.Title);
        if (verified)
        {
            var title = ReadableRecommendationTitle(accepted!.Title, code)
                ?? ReadableRecommendationTitle(accepted.OriginalTitle, code);
            if (title is not null)
                return new RecommendationCardTitle(title, "已核实 · " + accepted.Source, code);
        }

        var fileTitle = ReadableRecommendationTitle(video.Title, code);
        if (fileTitle is not null) return new RecommendationCardTitle(fileTitle, "文件名", code);
        var folderTitle = ReadableRecommendationTitle(
            ScrapeSearchHints.FindWorkTitleHint(video.Path, config.Folders,
                assumeWeakFilename: RecommendationCodeOnly.IsMatch(video.Title.Trim())), code);
        if (folderTitle is not null && code is not null && folderTitle.Length <= 4
            && folderTitle.All(ch => ch is >= 'A' and <= 'Z')
            && code.Contains(folderTitle, StringComparison.OrdinalIgnoreCase))
            folderTitle = null;
        if (folderTitle is not null) return new RecommendationCardTitle(folderTitle, "目录推测 · 待核实", code);
        return new RecommendationCardTitle("片名待补充", "尚无可靠片名", code);
    }

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
        var options = CurrentRecommendationOptions();
        try
        {
            var result = await Task.Run(() => (
                Local: RecommendationEngine.Rank(snapshot, snapshot.Length, options),
                Topics: RecommendationEngine.BuildBilibiliTopics(snapshot, 6, options)));
            if (version != recommendationVersion || view != "recommendations") return;
            rankedRecommendations = result.Local;
            recommendationVideos = videos.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            RecommendationPanel.Children.Clear();
            CountText.Text = $"从 {snapshot.Length:N0} 部视频中挑选";
            RenderRecommendationIntro(snapshot.Length);
            RenderRecommendationSettings();
            if (options.EnableOnlineTopics) RenderRecommendationTopics(result.Topics);
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

    private void RenderRecommendationSettings()
    {
        var shell = new Border
        {
            Background = Brushes.White, BorderBrush = Brush("#E3E9F3"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(17), Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 0, 16)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "推荐依据", FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#263347")
        });
        stack.Children.Add(new TextBlock
        {
            Text = "选择哪些本地信息参与排序。减少此类会隐藏该视频，并降低同类本地视频和在线主题的优先级。",
            FontSize = 12, Foreground = Brush("#7286A1"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 10)
        });
        var switches = new WrapPanel();
        void AddSwitch(string label, Func<bool> read, Action<bool> write)
        {
            var box = new System.Windows.Controls.CheckBox
            {
                Content = label, IsChecked = read(), Margin = new Thickness(0, 0, 18, 8),
                Foreground = Brush("#405D6D"), Cursor = System.Windows.Input.Cursors.Hand
            };
            box.Click += (_, _) =>
            {
                var previous = read();
                write(box.IsChecked == true);
                try { SaveConfig(); RenderRecommendations(); }
                catch (Exception ex)
                {
                    write(previous);
                    box.IsChecked = previous;
                    MessageBox.Show(this, "推荐设置未保存：" + ex.Message, "保存失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            switches.Children.Add(box);
        }
        AddSwitch("收藏", () => config.RecommendationUseFavorites, value => config.RecommendationUseFavorites = value);
        AddSwitch("观看状态", () => config.RecommendationUseWatchStatus, value => config.RecommendationUseWatchStatus = value);
        AddSwitch("分类", () => config.RecommendationUseCategories, value => config.RecommendationUseCategories = value);
        AddSwitch("标签", () => config.RecommendationUseTags, value => config.RecommendationUseTags = value);
        AddSwitch("在线主题", () => config.RecommendationOnlineTopics, value => config.RecommendationOnlineTopics = value);
        stack.Children.Add(switches);
        if (config.RecommendationLessLikeVideoIds.Count > 0)
        {
            var restore = new Button
            {
                Content = $"恢复减少此类（{config.RecommendationLessLikeVideoIds.Count}）",
                Padding = new Thickness(10, 5, 10, 5), Background = Brush("#EDF3FF"),
                Foreground = Brush("#405DAD"), BorderBrush = Brush("#D7E3FF"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand
            };
            restore.Click += (_, _) =>
            {
                var previous = config.RecommendationLessLikeVideoIds.ToList();
                config.RecommendationLessLikeVideoIds.Clear();
                try { SaveConfig(); RenderRecommendations(); }
                catch (Exception ex)
                {
                    config.RecommendationLessLikeVideoIds = previous;
                    MessageBox.Show(this, "推荐设置未保存：" + ex.Message, "保存失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            stack.Children.Add(restore);
        }
        shell.Child = stack;
        RecommendationPanel.Children.Add(shell);
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
        var visibleVideos = new List<VideoItem>(RecommendationPageSize);
        foreach (var recommendation in rankedRecommendations.Skip(recommendationPage * RecommendationPageSize).Take(RecommendationPageSize))
        {
            if (!recommendationVideos.TryGetValue(recommendation.VideoId, out var video)) continue;
            visibleVideos.Add(video);
            var title = RecommendationTitle(video);
            var card = new Border
            {
                Width = 260, MinHeight = 290, Margin = new Thickness(0, 0, 14, 14),
                Background = Brushes.White, BorderBrush = Brush("#E2E9F4"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
                Padding = new Thickness(12)
            };
            var content = new StackPanel();
            var poster = new Grid { Height = 142, ClipToBounds = true };
            poster.Children.Add(new Border { Background = Brush("#E7EDFA"), CornerRadius = new CornerRadius(11) });
            poster.Children.Add(new TextBlock
            {
                Text = "暂无封面", FontSize = 12, Foreground = Brush("#6F83A6"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            var image = new Image { Stretch = Stretch.UniformToFill, DataContext = video };
            image.SetBinding(Image.SourceProperty, new Binding(nameof(VideoItem.PosterSource)));
            poster.Children.Add(image);
            var openImage = new Button
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "查看封面大图", Content = "",
            };
            openImage.Click += async (_, _) => await OpenRecommendationImagePreviewAsync(video);
            poster.Children.Add(openImage);
            content.Children.Add(new Border
            {
                Child = poster, CornerRadius = new CornerRadius(11), ClipToBounds = true,
                Margin = new Thickness(0, 0, 0, 10)
            });
            content.Children.Add(new TextBlock
            {
                Text = title.Name, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#263347"), TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 39, TextWrapping = TextWrapping.Wrap,
                ToolTip = title.Name
            });
            content.Children.Add(new TextBlock
            {
                Text = title.Source + (title.Code is null ? "" : "  ·  编号 " + title.Code),
                FontSize = 10, Foreground = Brush("#8390A3"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = title.Source + (title.Code is null ? "" : "\n编号：" + title.Code),
                Margin = new Thickness(0, 4, 0, 0)
            });
            content.Children.Add(new TextBlock
            {
                Text = video.WatchStatus + (video.Favorite ? "  ·  已收藏" : ""),
                FontSize = 11, Foreground = Brush("#8390A3"), Margin = new Thickness(0, 5, 0, 0)
            });
            content.Children.Add(new TextBlock
            {
                Text = recommendation.Reason, FontSize = 11, Foreground = Brush("#4F68AD"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 7, 0, 10), ToolTip = recommendation.Reason
            });
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var imagePreview = new Button
            {
                Content = "图片", Padding = new Thickness(7, 5, 7, 5),
                Background = Brushes.White, Foreground = Brush("#405DAD"),
                BorderBrush = Brush("#D7E3FF"), BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 5, 0), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "放大查看封面或视频缩略图"
            };
            imagePreview.Click += async (_, _) => await OpenRecommendationImagePreviewAsync(video);
            actions.Children.Add(imagePreview);
            var videoPreview = new Button
            {
                Content = "视频预览", Padding = new Thickness(7, 5, 7, 5),
                Background = Brushes.White, Foreground = Brush("#405DAD"),
                BorderBrush = Brush("#D7E3FF"), BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 5, 0), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "在小窗口中按需预览视频"
            };
            videoPreview.Click += (_, _) => OpenRecommendationVideoPreview(video);
            actions.Children.Add(videoPreview);
            var play = new Button
            {
                Content = "▶ 播放", Padding = new Thickness(7, 5, 7, 5),
                Background = Brush("#EDF3FF"), Foreground = Brush("#405DAD"),
                BorderBrush = Brush("#D7E3FF"), BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            play.Click += (_, _) => LaunchVideo(video);
            actions.Children.Add(play);
            content.Children.Add(actions);
            var feedback = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 7, 0, 0)
            };
            var why = new Button
            {
                Content = "为什么推荐", Padding = new Thickness(7, 4, 7, 4),
                Background = Brushes.Transparent, Foreground = Brush("#5970AE"),
                BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand
            };
            why.Click += (_, _) => MessageBox.Show(this,
                recommendation.Reason + "\n\n排序仅在本机进行；关闭上方的信号可调整推荐依据。",
                "推荐依据", MessageBoxButton.OK, MessageBoxImage.Information);
            feedback.Children.Add(why);
            var lessLike = new Button
            {
                Content = "减少此类", Padding = new Thickness(7, 4, 7, 4),
                Background = Brushes.Transparent, Foreground = Brush("#8D6480"),
                BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "隐藏本视频，并降低同标签、同分类视频及在线主题的排序；可在推荐依据中恢复"
            };
            lessLike.Click += (_, _) =>
            {
                if (config.RecommendationLessLikeVideoIds.Contains(video.Id, StringComparer.OrdinalIgnoreCase)) return;
                config.RecommendationLessLikeVideoIds.Add(video.Id);
                try { SaveConfig(); RenderRecommendations(); }
                catch (Exception ex)
                {
                    config.RecommendationLessLikeVideoIds.RemoveAll(id => id.Equals(video.Id, StringComparison.OrdinalIgnoreCase));
                    MessageBox.Show(this, "推荐反馈未保存：" + ex.Message, "保存失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            feedback.Children.Add(lessLike);
            content.Children.Add(feedback);
            var editActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var editTitle = new Button
            {
                Content = "✎ 编辑番名", Padding = new Thickness(7, 4, 7, 4),
                Background = Brushes.Transparent, Foreground = Brush("#5970AE"),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            editTitle.Click += (_, _) => EditVideoDisplayTitle(video);
            editActions.Children.Add(editTitle);
            if (config.DisplayTitleOverrides.ContainsKey(video.Id))
            {
                var reset = new Button
                {
                    Content = "恢复自动名称", Padding = new Thickness(7, 4, 7, 4),
                    Background = Brushes.Transparent, Foreground = Brush("#8390A3"),
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                reset.Click += (_, _) => ClearVideoDisplayTitle(video);
                editActions.Children.Add(reset);
            }
            content.Children.Add(editActions);
            card.Child = content;
            cards.Children.Add(card);
        }
        section.Children.Add(cards);
        RecommendationPanel.Children.Add(section);
        // The ordinary card worker already checks sidecars and the shared
        // thumbnail cache, decodes off the UI thread, and generates missing
        // frames with FFmpeg. Only this visible batch is submitted.
        recommendationVisiblePosterCount = visibleVideos.Count;
        _ = GeneratePostersAsync(visibleVideos, ++posterVersion);
    }

    private void EditVideoDisplayTitle(VideoItem video)
    {
        var suggested = config.DisplayTitleOverrides.GetValueOrDefault(video.Id)
            ?? RecommendationTitle(video).Name;
        if (suggested == "片名待补充") suggested = "";
        var title = PromptText("编辑显示名 / 番名", "仅修改本地显示名，不改搜刮资料。", suggested);
        if (title is null) return;
        if (title.Length == 0) { ClearVideoDisplayTitle(video); return; }
        if (title.Length > 180 || title.Any(char.IsControl))
        {
            MessageBox.Show(this, "番名不能超过 180 个字符，也不能包含控制字符。", "番名无效",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var previous = config.DisplayTitleOverrides.GetValueOrDefault(video.Id);
        try
        {
            config.DisplayTitleOverrides[video.Id] = title;
            SaveConfig();
        }
        catch (Exception ex)
        {
            if (previous is null) config.DisplayTitleOverrides.Remove(video.Id);
            else config.DisplayTitleOverrides[video.Id] = previous;
            MessageBox.Show(this, "显示名未保存：" + ex.Message, "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        video.DisplayTitle = title;
        if (view == "recommendations") RenderRecommendationCards();
        else RenderCards();
        Log("已设置本地显示名：" + title);
    }

    private void ClearVideoDisplayTitle(VideoItem video)
    {
        if (!config.DisplayTitleOverrides.Remove(video.Id, out var previous)) return;
        try { SaveConfig(); }
        catch (Exception ex)
        {
            config.DisplayTitleOverrides[video.Id] = previous;
            MessageBox.Show(this, "显示名未恢复：" + ex.Message, "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (metadata.TryGetValue(video.Id, out var record)) ApplyMetadata(video, record);
        else video.DisplayTitle = video.Title;
        if (view == "recommendations") RenderRecommendationCards();
        else RenderCards();
        Log("已恢复自动显示名：" + video.DisplayTitle);
    }

    private async Task OpenRecommendationImagePreviewAsync(VideoItem video)
    {
        var cachedThumbnail = Path.Combine(ThumbDir, video.Id + ".jpg");
        var path = video.Poster is { Length: > 0 } && File.Exists(video.Poster)
            ? video.Poster : File.Exists(cachedThumbnail) ? cachedThumbnail : null;
        if (path is null)
        {
            MessageBox.Show(this, "尚无可预览的封面。正在后台准备缩略图时，请稍后再试。", "图片预览",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BitmapImage? bitmap = null;
        try
        {
            bitmap = await Task.Run(() =>
            {
                var loaded = new BitmapImage();
                loaded.BeginInit();
                loaded.UriSource = new Uri(path, UriKind.Absolute);
                loaded.CacheOption = BitmapCacheOption.OnLoad;
                loaded.DecodePixelWidth = 1400;
                loaded.EndInit();
                loaded.Freeze();
                return loaded;
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法读取图片：" + ex.Message, "图片预览",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (isClosing || Dispatcher.HasShutdownStarted) return;
        var dialog = new Window
        {
            Title = "图片预览 · " + RecommendationTitle(video).Name,
            Owner = this, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 920, Height = 650, MinWidth = 420, MinHeight = 320,
            Background = Brush("#172238")
        };
        dialog.Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Image
            {
                Source = bitmap, Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        dialog.ShowDialog();
    }

    private void OpenRecommendationVideoPreview(VideoItem video)
    {
        if (!File.Exists(video.Path))
        {
            MessageBox.Show(this, "视频文件已不存在，请先重新扫描视频库。", "视频预览",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Window
        {
            Title = "视频预览 · " + RecommendationTitle(video).Name,
            Owner = this, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 900, Height = 590, MinWidth = 560, MinHeight = 380,
            Background = Brush("#151D2C")
        };
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var status = new TextBlock
        {
            Text = "正在打开预览…", Foreground = Brush("#D5DEF1"),
            Margin = new Thickness(0, 0, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis
        };
        root.Children.Add(status);
        var media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.Uniform, ScrubbingEnabled = true,
            Volume = .7
        };
        Grid.SetRow(media, 1);
        root.Children.Add(media);

        var controls = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var seek = new Slider { Minimum = 0, Maximum = 1, Value = 0, Height = 22 };
        controls.Children.Add(seek);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var pause = new Button { Content = "暂停", Width = 65, Height = 31, Margin = new Thickness(0, 0, 8, 0) };
        pause.Click += (_, _) =>
        {
            if (pause.Content as string == "暂停") { media.Pause(); pause.Content = "继续"; }
            else { media.Play(); pause.Content = "暂停"; }
        };
        buttons.Children.Add(pause);
        var back = new Button { Content = "-10 秒", Width = 72, Height = 31, Margin = new Thickness(0, 0, 8, 0) };
        back.Click += (_, _) => media.Position = media.Position > TimeSpan.FromSeconds(10)
            ? media.Position - TimeSpan.FromSeconds(10) : TimeSpan.Zero;
        buttons.Children.Add(back);
        var forward = new Button { Content = "+10 秒", Width = 72, Height = 31, Margin = new Thickness(0, 0, 8, 0) };
        forward.Click += (_, _) => media.Position += TimeSpan.FromSeconds(10);
        buttons.Children.Add(forward);
        var time = new TextBlock
        {
            Text = "00:00 / --:--", Foreground = Brush("#D5DEF1"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0)
        };
        buttons.Children.Add(time);
        var external = new Button { Content = "用播放器打开", Height = 31, Padding = new Thickness(10, 0, 10, 0) };
        external.Click += (_, _) => LaunchVideo(video);
        buttons.Children.Add(external);
        controls.Children.Add(buttons);
        Grid.SetRow(controls, 2);
        root.Children.Add(controls);
        dialog.Content = root;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        var updatingSeek = false;
        timer.Tick += (_, _) =>
        {
            if (!media.NaturalDuration.HasTimeSpan) return;
            var duration = media.NaturalDuration.TimeSpan;
            updatingSeek = true;
            seek.Maximum = Math.Max(1, duration.TotalSeconds);
            seek.Value = Math.Clamp(media.Position.TotalSeconds, 0, seek.Maximum);
            updatingSeek = false;
            time.Text = $"{media.Position:mm\\:ss} / {duration:mm\\:ss}";
        };
        seek.ValueChanged += (_, _) =>
        {
            if (!updatingSeek && media.NaturalDuration.HasTimeSpan)
                media.Position = TimeSpan.FromSeconds(seek.Value);
        };
        media.MediaOpened += (_, _) => { status.Text = "本地视频预览"; timer.Start(); };
        media.MediaFailed += (_, args) =>
        {
            timer.Stop();
            status.Text = "此视频格式无法在预览窗解码，可用播放器打开。";
            pause.IsEnabled = false;
            seek.IsEnabled = false;
            Log("视频预览解码失败：" + args.ErrorException?.Message);
        };
        media.MediaEnded += (_, _) => { media.Stop(); pause.Content = "继续"; };
        dialog.Loaded += (_, _) =>
        {
            try { media.Source = new Uri(video.Path, UriKind.Absolute); media.Play(); }
            catch (Exception ex)
            {
                status.Text = "预览打开失败，可用播放器打开：" + ex.Message;
                pause.IsEnabled = false;
            }
        };
        dialog.Closed += (_, _) =>
        {
            timer.Stop();
            media.Stop();
            media.Source = null;
        };
        dialog.ShowDialog();
    }

    internal async Task<string> VerifyRecommendationsOffscreenAsync()
    {
        if (ReadableRecommendationTitle("FC2-PPV-4739552-01", "FC2-PPV-4739552-01") is not null
            || ReadableRecommendationTitle("VID_20241016225600629", null) is not null
            || ReadableRecommendationTitle("FC2-PPV-4739552-01 星光下的故事", "FC2-PPV-4739552-01") != "星光下的故事"
            || RecommendationFilenameCode("E002. Teen Big But... ") != "E002"
            || ReadableRecommendationTitle("E002. Teen Big But...", "E002") != "Teen Big But"
            || ReadableRecommendationTitle("X20", "X20") is not null)
            throw new InvalidOperationException("纯编号与编号加番名的识别规则不正确");
        var baseline = new LibraryConfig();
        var edited = CloneLibraryConfig(baseline);
        edited.DisplayTitleOverrides["diagnostic-video"] = "人工填写的番名";
        edited.RecommendationUseTags = false;
        edited.RecommendationLessLikeVideoIds.Add("diagnostic-video");
        var merged = MergeLibraryConfig(baseline, edited, CloneLibraryConfig(baseline));
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<LibraryConfig>(
            System.Text.Json.JsonSerializer.Serialize(merged))
            ?? throw new InvalidOperationException("显示名配置无法反序列化");
        if (roundTrip.DisplayTitleOverrides.GetValueOrDefault("diagnostic-video") != "人工填写的番名")
            throw new InvalidOperationException("手动显示名未能合并或持久化");
        if (roundTrip.RecommendationUseTags ||
            !roundTrip.RecommendationLessLikeVideoIds.Contains("diagnostic-video", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("推荐依据与减少此类未能合并或持久化");
        var fixtureVideo = new VideoItem
        {
            Id = "diagnostic-video", Path = "C:\\diagnostic.mp4", Title = "FC2-PPV-4739552-01",
            Folder = "C:\\", Size = 0, Modified = DateTime.MinValue
        };
        var fixtureMetadata = new MovieMetadata { Code = "FC2-PPV-4739552-01", Title = "已审核资料番名" };
        var savedConfig = config;
        try
        {
            config = roundTrip;
            ApplyMetadata(fixtureVideo, fixtureMetadata);
            if (fixtureVideo.DisplayTitle != "人工填写的番名")
                throw new InvalidOperationException("手动显示名未优先于搜刮标题");
            config.DisplayTitleOverrides.Remove(fixtureVideo.Id);
            ApplyMetadata(fixtureVideo, fixtureMetadata);
            if (fixtureVideo.DisplayTitle != "已审核资料番名")
                throw new InvalidOperationException("清除显示名后未恢复搜刮标题");
        }
        finally { config = savedConfig; }

        var started = System.Diagnostics.Stopwatch.StartNew();
        ShowView("recommendations");
        for (var attempt = 0; attempt < 200 && recommendationCardsSection is null; attempt++)
            await Task.Delay(100);
        var section = recommendationCardsSection
            ?? throw new InvalidOperationException("推荐卡片没有在规定时间内生成");
        var preferenceSwitches = RecommendationPanel.Children.OfType<Border>()
            .Select(border => border.Child as StackPanel)
            .Where(stack => stack is not null)
            .SelectMany(stack => stack!.Children.OfType<WrapPanel>())
            .SelectMany(panel => panel.Children.OfType<System.Windows.Controls.CheckBox>())
            .ToArray();
        if (preferenceSwitches.Length != 5)
            throw new InvalidOperationException("推荐页缺少收藏、观看、分类、标签或在线主题开关");
        var cards = section.Children.OfType<WrapPanel>().LastOrDefault()
            ?? throw new InvalidOperationException("推荐卡片列表未创建");
        var expected = Math.Min(RecommendationPageSize, rankedRecommendations.Count);
        if (cards.Children.Count != expected || recommendationVisiblePosterCount != expected)
            throw new InvalidOperationException($"推荐页可见卡片与按需缩略图任务不一致：{cards.Children.Count}/{recommendationVisiblePosterCount}/{expected}");
        foreach (var card in cards.Children.OfType<Border>())
        {
            var stack = card.Child as StackPanel ?? throw new InvalidOperationException("推荐卡片布局无效");
            var poster = (stack.Children[0] as Border)?.Child as Grid
                ?? throw new InvalidOperationException("推荐卡片缺少图片预览");
            if (!poster.Children.OfType<Image>().Any(image => BindingOperations.GetBinding(image, Image.SourceProperty) is not null))
                throw new InvalidOperationException("推荐卡片图片没有绑定按需缩略图");
            if (!stack.Children.OfType<StackPanel>().Any(panel => panel.Children.OfType<Button>().Count() == 3))
                throw new InvalidOperationException("推荐卡片缺少图片、视频预览或播放按钮");
            if (!stack.Children.OfType<StackPanel>().Any(panel =>
                    panel.Children.OfType<Button>().Any(button => Equals(button.Content, "为什么推荐"))
                    && panel.Children.OfType<Button>().Any(button => Equals(button.Content, "减少此类"))))
                throw new InvalidOperationException("推荐卡片缺少推荐依据或减少此类入口");
        }
        var titles = rankedRecommendations.Take(expected)
            .Where(hit => recommendationVideos.ContainsKey(hit.VideoId))
            .Select(hit => RecommendationTitle(recommendationVideos[hit.VideoId])).ToArray();
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        string? previewPath = null;
        try
        {
            var width = (int)Math.Ceiling(RecommendationPanel.ActualWidth);
            var height = (int)Math.Ceiling(RecommendationPanel.ActualHeight);
            if (width is > 100 and <= 4000 && height is > 100 and <= 6000)
            {
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(RecommendationPanel);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                previewPath = Path.Combine(Path.GetTempPath(), $"yingge-recommendations-{Environment.ProcessId}.png");
                using var file = File.Create(previewPath);
                encoder.Save(file);
            }
        }
        catch (Exception ex) { Log("离屏推荐页快照未生成：" + ex.Message); }
        return $"从 {videos.Count:N0} 部视频中生成推荐；只为当前 {expected} 张卡片提交缩略图任务；图片、视频预览及播放入口完整；手动番名重启存储与恢复通过；已核实片名 {titles.Count(item => item.Source.StartsWith("已核实", StringComparison.Ordinal))}，文件名 {titles.Count(item => item.Source == "文件名")}，目录推测 {titles.Count(item => item.Source.StartsWith("目录推测", StringComparison.Ordinal))}，待补充 {titles.Count(item => item.Name == "片名待补充")}；耗时 {started.ElapsedMilliseconds} ms；离屏预览 {previewPath ?? "未生成"}；未修改分类、标签或审核数据";
    }

    private static SolidColorBrush Brush(string color) => new(ColorOf(color));
    private static System.Windows.Media.Color ColorOf(string color) => (System.Windows.Media.Color)ColorConverter.ConvertFromString(color);
}

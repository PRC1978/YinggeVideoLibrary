using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    void ShowMetadataDetailsForVideo(VideoItem video)
    {
        if (metadata.TryGetValue(video.Id, out var saved))
        {
            var review = config.AiReviews.GetValueOrDefault(video.Id);
            ShowMetadataDetails(saved, review?.Confidence, review?.Status, video);
        }
        else if (config.AiReviews.TryGetValue(video.Id, out var candidate) && candidate.Status != "unmatched")
            ShowMetadataDetails(candidate.ToMetadata(), candidate.Confidence, candidate.Status, video);
        else ShowMetadataDetails(new MovieMetadata
        {
            Title = video.Title, Source = "本地视频", DetailNote = "尚未搜刮；可先设置个人评分。"
        }, status: "local-only", video: video);
    }

    void ShowMetadataDetails(MovieMetadata record, int? confidence = null, string? status = null, VideoItem? video = null)
    {
        var dialog = new Window
        {
            Title = "搜刮资料 · " + (string.IsNullOrWhiteSpace(record.Code) ? record.Title : record.Code),
            Owner = this, Width = 780, Height = 650, MinWidth = 550, MinHeight = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 253))
        };
        var root = new DockPanel { Margin = new Thickness(22) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        var roles = new Button { Content = "人物 / 角色档案…", Style = (Style)FindResource("Action") };
        roles.Click += (_, _) => { dialog.Close(); OpenRoleProfiles(video, record); };
        buttons.Children.Add(roles);
        if (Uri.TryCreate(record.SourceUrl, UriKind.Absolute, out var sourceUri) &&
            sourceUri.Scheme == Uri.UriSchemeHttps &&
            (sourceUri.Host == "javdb.com" || sourceUri.Host == "www.javdb.com" ||
             sourceUri.Host == "www.javdatabase.com" || sourceUri.Host == "javdatabase.com" ||
             sourceUri.Host == "bgm.tv" ||
             sourceUri.Host == "www.themoviedb.org" || sourceUri.Host == "www.tvmaze.com" ||
             sourceUri.Host == "www.pornhub.com"))
        {
            var open = new Button { Content = "打开来源页面", Style = (Style)FindResource("Action") };
            open.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(sourceUri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(dialog, "无法打开来源页面：" + ex.Message, "搜刮资料"); }
            };
            buttons.Children.Add(open);
        }
        var close = new Button { Content = "关闭", Width = 90, Style = (Style)FindResource("Primary") };
        close.Click += (_, _) => dialog.Close();
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = record.Title, FontSize = 22, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(35, 49, 74)), TextWrapping = TextWrapping.Wrap });
        var statusText = status switch { "approved" => "已通过", "pending" => "待人工审核", "rejected" => "已否决", "unmatched" => "未找到", "local-only" => "未搜刮", _ => "已保存" };
        panel.Children.Add(new TextBlock { Text = $"{record.Source}  ·  {statusText}" + (confidence is null ? "" : $"  ·  可信度 {confidence}%"),
            Margin = new Thickness(0, 7, 0, 10), Foreground = new SolidColorBrush(Color.FromRgb(78, 107, 187)) });
        var note = string.IsNullOrWhiteSpace(record.DetailNote)
            ? record.DetailFetched ? "已读取来源详情；来源未提供的字段会明确显示。" : "仅有旧版搜刮摘要，尚未读取完整详情；可重新搜刮。"
            : record.DetailNote;
        panel.Children.Add(new TextBlock { Text = note, Margin = new Thickness(0, 0, 0, 12),
            Foreground = record.DetailFetched ? new SolidColorBrush(Color.FromRgb(90, 114, 143)) : new SolidColorBrush(Color.FromRgb(174, 99, 47)),
            TextWrapping = TextWrapping.Wrap });
        var savedRecord = video is not null && metadata.TryGetValue(video.Id, out var accepted) && ReferenceEquals(accepted, record);
        AddMetadataRatings(panel, dialog, record, video, savedRecord);
        void AddSourcedField(string label, string? value, string field)
        {
            MetadataFieldProvenance? origin = null;
            record.FieldProvenance?.TryGetValue(field, out origin);
            AddMetadataRow(panel, label, value, origin, showProvenance: true);
        }
        if (video is not null)
            AddMetadataRow(panel, "本地文件", Path.GetFileName(video.Path));
        if (status == "local-only")
        {
            scroll.Content = panel;
            root.Children.Add(scroll);
            dialog.Content = root;
            dialog.ShowDialog();
            return;
        }
        AddSourcedField("标题", record.Title, nameof(MovieMetadata.Title));
        AddSourcedField("番号 / 编号", record.Code, nameof(MovieMetadata.Code));
        AddSourcedField("原始标题", record.OriginalTitle, nameof(MovieMetadata.OriginalTitle));
        AddSourcedField("发行日期", record.ReleaseDate, nameof(MovieMetadata.ReleaseDate));
        AddSourcedField("时长", record.RuntimeMinutes is > 0 ? $"{record.RuntimeMinutes} 分钟" : null, nameof(MovieMetadata.RuntimeMinutes));
        AddSourcedField("导演", record.Director, nameof(MovieMetadata.Director));
        AddSourcedField("工作室 / 发行", record.Studio, nameof(MovieMetadata.Studio));
        AddSourcedField("片商 / 制作方", record.Maker, nameof(MovieMetadata.Maker));
        AddSourcedField("厂牌", record.Label, nameof(MovieMetadata.Label));
        AddSourcedField("系列", record.Series, nameof(MovieMetadata.Series));
        AddSourcedField("演员", JoinMetadata(record.Actors), nameof(MovieMetadata.Actors));
        AddSourcedField("类型 / 类别", JoinMetadata(record.Genres), nameof(MovieMetadata.Genres));
        AddSourcedField("关键词", JoinMetadata(record.Keywords), nameof(MovieMetadata.Keywords));
        AddSourcedField("地区", record.Country, nameof(MovieMetadata.Country));
        AddSourcedField("语言", record.Language, nameof(MovieMetadata.Language));
        AddSourcedField("来源评分", record.Rating, nameof(MovieMetadata.Rating));
        AddSourcedField("简介", record.Summary, nameof(MovieMetadata.Summary));
        AddSourcedField("封面地址", record.PosterUrl, nameof(MovieMetadata.PosterUrl));
        var verifiedImages = JavDatabaseScraper.HasVerifiedMediaEvidence(record, nameof(MovieMetadata.PreviewImageUrls));
        var verifiedTrailer = JavDatabaseScraper.HasVerifiedMediaEvidence(record, nameof(MovieMetadata.TrailerUrl));
        AddSourcedField("样张图片", verifiedImages ? $"{record.PreviewImageUrls.Count} 张 · 点击展开可逐张打开" : null,
            nameof(MovieMetadata.PreviewImageUrls));
        if (verifiedImages)
        {
            var imageLinks = new Expander
            {
                Header = $"查看样张链接 · {record.PreviewImageUrls.Count} 张",
                Margin = new Thickness(0, 6, 0, 8), Padding = new Thickness(8),
                Background = System.Windows.Media.Brushes.White
            };
            var imageButtons = new WrapPanel();
            for (var index = 0; index < record.PreviewImageUrls.Count; index++)
                AddMediaOpenButton(imageButtons, dialog, $"样张 {index + 1}", record.PreviewImageUrls[index], trailer: false);
            imageLinks.Content = imageButtons;
            panel.Children.Add(imageLinks);
        }
        AddSourcedField("预告视频", verifiedTrailer ? "已找到公开预告链接 · 点击后在浏览器打开" : null,
            nameof(MovieMetadata.TrailerUrl));
        if (verifiedTrailer)
        {
            var trailerButtons = new WrapPanel { Margin = new Thickness(8, 4, 0, 10) };
            AddMediaOpenButton(trailerButtons, dialog, "打开预告链接", record.TrailerUrl!, trailer: true);
            panel.Children.Add(trailerButtons);
        }
        if (record.SourceObservations is { Count: > 0 })
        {
            var sourceCount = record.SourceObservations.Select(item => item.Source)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var conflicting = record.SourceConflictFields ?? [];
            var comparison = new Expander
            {
                Header = $"{(sourceCount > 1 ? "多来源" : "来源")}原始字段对照 · {sourceCount} 个来源" +
                    (sourceCount == 1 ? "" : conflicting.Count == 0 ? " · 无身份字段冲突" : $" · {conflicting.Count} 项需核对"),
                Margin = new Thickness(0, 12, 0, 10), Padding = new Thickness(10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(221, 230, 245)),
                BorderThickness = new Thickness(1), Background = System.Windows.Media.Brushes.White
            };
            var rows = new StackPanel();
            var labels = new Dictionary<string, string>
            {
                [nameof(MovieMetadata.Title)] = "标题", [nameof(MovieMetadata.Code)] = "番号",
                [nameof(MovieMetadata.OriginalTitle)] = "原始标题", [nameof(MovieMetadata.ReleaseDate)] = "发行日期",
                [nameof(MovieMetadata.RuntimeMinutes)] = "时长", [nameof(MovieMetadata.Director)] = "导演",
                [nameof(MovieMetadata.Studio)] = "工作室 / 发行",
                [nameof(MovieMetadata.Maker)] = "片商 / 制作方", [nameof(MovieMetadata.Label)] = "厂牌",
                [nameof(MovieMetadata.Series)] = "系列",
                [nameof(MovieMetadata.Actors)] = "演员", [nameof(MovieMetadata.Genres)] = "类型 / 类别",
                [nameof(MovieMetadata.Keywords)] = "关键词", [nameof(MovieMetadata.Country)] = "地区",
                [nameof(MovieMetadata.Language)] = "语言", [nameof(MovieMetadata.Rating)] = "来源评分",
                [nameof(MovieMetadata.Summary)] = "简介", [nameof(MovieMetadata.PosterUrl)] = "封面地址",
                [nameof(MovieMetadata.PreviewImageUrls)] = "样张图片", [nameof(MovieMetadata.TrailerUrl)] = "预告视频"
            };
            foreach (var group in record.SourceObservations
                .Where(item => item.Field is not (nameof(MovieMetadata.PreviewImageUrls) or nameof(MovieMetadata.TrailerUrl)) ||
                    ReviewFieldApproval.ValidMediaObservation(record, item))
                .GroupBy(item => item.Field, StringComparer.Ordinal))
            {
                var isConflict = conflicting.Contains(group.Key, StringComparer.Ordinal);
                rows.Children.Add(new TextBlock
                {
                    Text = (labels.TryGetValue(group.Key, out var label) ? label : group.Key) +
                        (isConflict ? " · 来源不一致，待人工核对" : ""),
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 11, 0, 4),
                    Foreground = isConflict ? new SolidColorBrush(Color.FromRgb(172, 81, 48))
                        : new SolidColorBrush(Color.FromRgb(46, 65, 99))
                });
                foreach (var observation in group)
                {
                    rows.Children.Add(new TextBlock
                    {
                        Text = $"{observation.Source} · {observation.Stage}：{observation.Value}",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.FromRgb(65, 82, 110)),
                        Margin = new Thickness(8, 0, 0, 2)
                    });
                    rows.Children.Add(new TextBlock
                    {
                        Text = observation.SourceUrl, TextWrapping = TextWrapping.Wrap,
                        FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(125, 139, 165)),
                        Margin = new Thickness(8, 0, 0, 4)
                    });
                }
            }
            comparison.Content = rows;
            panel.Children.Add(comparison);
        }
        AddMetadataRow(panel, "来源链接", record.SourceUrl);
        AddMetadataRow(panel, "搜刮时间", record.ScrapedAt.ToString("yyyy-MM-dd HH:mm"));
        scroll.Content = panel;
        root.Children.Add(scroll);
        dialog.Content = root;
        dialog.ShowDialog();
    }

    static string? JoinMetadata(IEnumerable<string>? values)
    {
        var items = values?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return items is { Count: > 0 } ? string.Join("、", items) : null;
    }

    static void AddMediaOpenButton(WrapPanel parent, Window dialog, string label, string url, bool trailer)
    {
        // Recheck at click time. Reading metadata never downloads or streams
        // media, and a malformed persisted URL must not reach the browser.
        if (trailer ? !JavDatabaseScraper.IsTrustedTrailerUrl(url) : !JavDatabaseScraper.IsTrustedPreviewUrl(url)) return;
        var open = new Button
        {
            Content = label, Style = (Style)dialog.FindResource("Action"),
            Margin = new Thickness(0, 0, 6, 6), ToolTip = url
        };
        open.Click += (_, _) =>
        {
            if (trailer ? !JavDatabaseScraper.IsTrustedTrailerUrl(url) : !JavDatabaseScraper.IsTrustedPreviewUrl(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(dialog, "无法打开链接：" + ex.Message, "搜刮资料"); }
        };
        parent.Children.Add(open);
    }

    static void AddMetadataRow(StackPanel parent, string label, string? value,
        MetadataFieldProvenance? provenance = null, bool showProvenance = false)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 1), Background = System.Windows.Media.Brushes.White };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var heading = new TextBlock { Text = label, Padding = new Thickness(12, 10, 8, 10),
            Foreground = new SolidColorBrush(Color.FromRgb(111, 125, 149)), FontWeight = FontWeights.SemiBold };
        var contentHost = new StackPanel { Margin = new Thickness(8, 9, 12, 9) };
        var content = new TextBlock { Text = string.IsNullOrWhiteSpace(value) ? "来源未提供" : value,
            TextWrapping = TextWrapping.Wrap,
            Foreground = string.IsNullOrWhiteSpace(value) ? new SolidColorBrush(Color.FromRgb(158, 169, 185)) : new SolidColorBrush(Color.FromRgb(42, 56, 81)) };
        contentHost.Children.Add(content);
        if (showProvenance && !string.IsNullOrWhiteSpace(value))
        {
            var attribution = provenance is null ? "旧记录 · 未逐项记录来源"
                : string.IsNullOrWhiteSpace(provenance.Stage) ? provenance.Source : $"{provenance.Source} · {provenance.Stage}";
            contentHost.Children.Add(new TextBlock { Text = attribution, Margin = new Thickness(0, 4, 0, 0),
                FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(117, 136, 173)) });
        }
        Grid.SetColumn(contentHost, 1);
        row.Children.Add(heading);
        row.Children.Add(contentHost);
        parent.Children.Add(row);
    }
}

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
        else MessageBox.Show(this, "这个视频还没有可查看的搜刮资料。", "搜刮资料", MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (Uri.TryCreate(record.SourceUrl, UriKind.Absolute, out var sourceUri) &&
            sourceUri.Scheme == Uri.UriSchemeHttps &&
            (sourceUri.Host == "javdb.com" || sourceUri.Host == "www.javdb.com" ||
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
        var statusText = status switch { "approved" => "已通过", "pending" => "待人工审核", "rejected" => "已否决", "unmatched" => "未找到", _ => "已保存" };
        panel.Children.Add(new TextBlock { Text = $"{record.Source}  ·  {statusText}" + (confidence is null ? "" : $"  ·  可信度 {confidence}%"),
            Margin = new Thickness(0, 7, 0, 10), Foreground = new SolidColorBrush(Color.FromRgb(78, 107, 187)) });
        var note = string.IsNullOrWhiteSpace(record.DetailNote)
            ? record.DetailFetched ? "已读取来源详情；来源未提供的字段会明确显示。" : "仅有旧版搜刮摘要，尚未读取完整详情；可重新搜刮。"
            : record.DetailNote;
        panel.Children.Add(new TextBlock { Text = note, Margin = new Thickness(0, 0, 0, 12),
            Foreground = record.DetailFetched ? new SolidColorBrush(Color.FromRgb(90, 114, 143)) : new SolidColorBrush(Color.FromRgb(174, 99, 47)),
            TextWrapping = TextWrapping.Wrap });
        if (video is not null)
            AddMetadataRow(panel, "本地文件", Path.GetFileName(video.Path));
        AddMetadataRow(panel, "番号 / 编号", record.Code);
        AddMetadataRow(panel, "原始标题", record.OriginalTitle);
        AddMetadataRow(panel, "发行日期", record.ReleaseDate);
        AddMetadataRow(panel, "时长", record.RuntimeMinutes is > 0 ? $"{record.RuntimeMinutes} 分钟" : null);
        AddMetadataRow(panel, "导演", record.Director);
        AddMetadataRow(panel, "制作 / 发行", record.Studio);
        AddMetadataRow(panel, "系列", record.Series);
        AddMetadataRow(panel, "演员", JoinMetadata(record.Actors));
        AddMetadataRow(panel, "类型 / 类别", JoinMetadata(record.Genres));
        AddMetadataRow(panel, "关键词", JoinMetadata(record.Keywords));
        AddMetadataRow(panel, "地区", record.Country);
        AddMetadataRow(panel, "语言", record.Language);
        AddMetadataRow(panel, "来源评分", record.Rating);
        AddMetadataRow(panel, "简介", record.Summary);
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

    static void AddMetadataRow(StackPanel parent, string label, string? value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 1), Background = System.Windows.Media.Brushes.White };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var heading = new TextBlock { Text = label, Padding = new Thickness(12, 10, 8, 10),
            Foreground = new SolidColorBrush(Color.FromRgb(111, 125, 149)), FontWeight = FontWeights.SemiBold };
        var content = new TextBlock { Text = string.IsNullOrWhiteSpace(value) ? "来源未提供" : value,
            Padding = new Thickness(8, 10, 12, 10), TextWrapping = TextWrapping.Wrap,
            Foreground = string.IsNullOrWhiteSpace(value) ? new SolidColorBrush(Color.FromRgb(158, 169, 185)) : new SolidColorBrush(Color.FromRgb(42, 56, 81)) };
        Grid.SetColumn(content, 1);
        row.Children.Add(heading);
        row.Children.Add(content);
        parent.Children.Add(row);
    }
}

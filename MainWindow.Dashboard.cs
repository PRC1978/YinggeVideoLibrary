using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    // A small, local-only dashboard on the recommendation page. No playback or
    // personal file names leave the machine; counts come from the current index.
    void RenderLibraryDashboard()
    {
        var total = videos.Count;
        var watched = 0;
        var favorites = 0;
        var missing = 0;
        var unavailable = 0;
        var withoutCategory = 0;
        foreach (var video in videos)
        {
            if (video.WatchStatus != "未看") watched++;
            if (video.Favorite) favorites++;
            if (video.IsMissing) missing++;
            if (video.IsUnavailable) unavailable++;
            if (video.Category == "未分类") withoutCategory++;
        }
        var shell = new Border
        {
            Background = UiTheme.SurfaceBrush,
            BorderBrush = UiTheme.BorderBrushToken,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 16)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "本地资料概览", FontSize = 16,
            FontWeight = FontWeights.SemiBold, Foreground = UiTheme.TextPrimaryBrush });
        stack.Children.Add(new TextBlock { Text = "基于当前本地索引，打开页面时更新。",
            Foreground = UiTheme.TextTertiaryBrush, Margin = new Thickness(0, 3, 0, 12) });
        var row = new WrapPanel();
        AddStat(row, "视频", total, total);
        AddStat(row, "已看", watched, total);
        AddStat(row, "收藏", favorites, total);
        AddStat(row, "待分类", withoutCategory, total);
        AddStat(row, "不在本地", missing, total);
        AddStat(row, "位置暂不可用", unavailable, total);
        stack.Children.Add(row);
        shell.Child = stack;
        RecommendationPanel.Children.Add(shell);
    }

    static void AddStat(WrapPanel row, string label, int value, int total)
    {
        var card = new Border { Background = UiTheme.SurfaceAltBrush,
            BorderBrush = UiTheme.BorderBrushToken, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12),
            Width = 145, Margin = new Thickness(0, 0, 9, 9) };
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = label, Foreground = UiTheme.TextSecondaryBrush,
            FontSize = 12 });
        content.Children.Add(new TextBlock { Text = value.ToString("N0"),
            Foreground = UiTheme.TextPrimaryBrush, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 22,
            Margin = new Thickness(0, 3, 0, 2) });
        content.Children.Add(new TextBlock { Text = total == 0 ? "0%" : $"{value * 100d / total:0.#}%",
            Foreground = UiTheme.AccentBrush, FontSize = 11 });
        card.Child = content;
        row.Children.Add(card);
    }
}

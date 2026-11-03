using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    void AppendVerifiedWorkComparison(StackPanel panel,
        IReadOnlyList<ScrapeCandidateCache.RankedTitleCandidate> candidates, int selectedIndex)
    {
        var details = candidates.Select(row => row.DetailMetadata ?? row.SearchMetadata).ToList();
        var cluster = VerifiedWorkClusters.Find(details)
            .FirstOrDefault(group => group.Members.Contains(selectedIndex));
        if (cluster is null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "尚无可核对的跨来源作品编号；相似标题不会自动合并。",
                Foreground = UiTheme.TextTertiaryBrush, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            return;
        }
        var sources = string.Join("、", cluster.Members.Select(index => details[index].Source));
        var expander = new Expander
        {
            Header = $"已核对同一作品 · {cluster.SharedId} · {sources}" +
                (cluster.Conflicts.Count == 0 ? "" : $" · {cluster.Conflicts.Count} 项身份字段差异"),
            IsExpanded = true, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(8),
            ToolTip = "仅依据已核对的 IMDb 或同类型 TMDB 编号关联；所有资料仍须人工选用。"
        };
        var rows = new StackPanel();
        rows.Children.Add(new TextBlock
        {
            Text = "以下为来源原值对照。没有自动合并或写入资料；有差异的字段须人工核对。",
            Foreground = UiTheme.TextTertiaryBrush, TextWrapping = TextWrapping.Wrap
        });
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(MovieMetadata.Title)] = "标题", [nameof(MovieMetadata.OriginalTitle)] = "原名",
            [nameof(MovieMetadata.ReleaseDate)] = "发行日期", [nameof(MovieMetadata.RuntimeMinutes)] = "时长",
            [nameof(MovieMetadata.Director)] = "导演", [nameof(MovieMetadata.Studio)] = "制作方",
            [nameof(MovieMetadata.Actors)] = "演员", [nameof(MovieMetadata.Summary)] = "简介",
            [nameof(MovieMetadata.Rating)] = "来源评分"
        };
        foreach (var field in cluster.Fields.GroupBy(item => item.Field, StringComparer.Ordinal))
        {
            var conflict = cluster.Conflicts.Contains(field.Key, StringComparer.Ordinal);
            rows.Children.Add(new TextBlock
            {
                Text = labels.GetValueOrDefault(field.Key, field.Key) +
                    (conflict ? " · 来源不一致" : ""),
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 2),
                Foreground = conflict ? Brushes.DarkOrange : Brushes.DarkSlateBlue
            });
            foreach (var observation in field)
                rows.Children.Add(new TextBlock
                {
                    Text = $"{observation.Source}：{observation.Value}\n{observation.SourceUrl}",
                    TextWrapping = TextWrapping.Wrap, Foreground = UiTheme.TextTertiaryBrush,
                    Margin = new Thickness(8, 0, 0, 4)
                });
        }
        expander.Content = rows;
        panel.Children.Add(expander);
    }
}

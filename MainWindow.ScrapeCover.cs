using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    internal static bool IsOwnedCustomPosterPath(string thumbDir, string videoId, string path)
    {
        try
        {
            var root = Path.GetFullPath(thumbDir) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(full).StartsWith(videoId + "-custom.", StringComparison.OrdinalIgnoreCase) &&
                (File.GetAttributes(root.TrimEnd(Path.DirectorySeparatorChar)) & FileAttributes.ReparsePoint) == 0 &&
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0;
        }
        catch { return false; }
    }
    string? ManagedScrapeCoverPath(MovieMetadata record)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(DataDir, "posters")) + Path.DirectorySeparatorChar;
            var path = string.IsNullOrWhiteSpace(record.Poster) ? "" : Path.GetFullPath(record.Poster);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
            if ((File.GetAttributes(root.TrimEnd(Path.DirectorySeparatorChar)) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            return path;
        }
        catch { return null; }
    }

    Window? BuildScrapeCoverZoom(MovieMetadata record)
    {
        if (ManagedScrapeCoverPath(record) is null) return null;
        var dialog = new Window { Title = "封面预览 · " + record.Source, Owner = this,
            Width = 530, Height = 700, MinWidth = 300, MinHeight = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Content = new ScrollViewer { Content = CreateScrapeCover(record, 440, 600),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return dialog;
    }

    void ShowScrapeCoverZoom(MovieMetadata record) => BuildScrapeCoverZoom(record)?.ShowDialog();

    bool UseScrapeCover(VideoItem video, MovieMetadata record, out string error)
    {
        error = "";
        var path = ManagedScrapeCoverPath(record);
        if (path is null) { error = "请先加载这张封面。"; return false; }
        var hadPrevious = config.CustomPosters.TryGetValue(video.Id, out var previous);
        var previousVisual = video.Poster;
        try
        {
            config.CustomPosters[video.Id] = path;
            SaveConfig();
            video.Poster = path;
            VideoItem.InvalidatePoster(path);
            RenderCards();
            return true;
        }
        catch (Exception ex)
        {
            if (hadPrevious) config.CustomPosters[video.Id] = previous!;
            else config.CustomPosters.Remove(video.Id);
            video.Poster = previousVisual;
            error = "封面未保存：" + ex.Message;
            return false;
        }
    }

    void ShowScrapeCoverComparison(VideoItem video, AiReviewRecord review)
        => BuildScrapeCoverComparison(video, review).ShowDialog();

    Window BuildScrapeCoverComparison(VideoItem video, AiReviewRecord review)
    {
        var current = review.ToMetadata();
        var choices = new List<MovieMetadata> { current };
        foreach (var observation in current.SourceObservations ?? [])
        {
            if (observation.Field != nameof(MovieMetadata.PosterUrl) ||
                string.IsNullOrWhiteSpace(observation.Value)) continue;
            choices.Add(new MovieMetadata { Code = current.Code, Title = current.Title,
                Source = observation.Source, SourceUrl = observation.SourceUrl,
                PosterUrl = observation.Value,
                Poster = string.Equals(current.PosterUrl, observation.Value, StringComparison.OrdinalIgnoreCase)
                    ? current.Poster : null });
        }
        if (review.DecisionSource == "plugin")
            choices.AddRange(PluginCandidateStore.Get(DataDir, video.Id).Select(item => item.Metadata.Clone()));
        var snapshot = ScrapeCandidateCache.LoadTitleCandidates(DataDir, video.Id);
        if (snapshot is not null)
            choices.AddRange(snapshot.Candidates.Where(item => item.DetailStatus == "verified" &&
                    item.DetailMetadata?.DetailIdentityVerified == true)
                .Select(item => item.DetailMetadata!.Clone()));
        choices = choices.Where(item => !string.IsNullOrWhiteSpace(item.PosterUrl) ||
                    ManagedScrapeCoverPath(item) is not null)
            .DistinctBy(item => item.Source + "|" + item.PosterUrl + "|" + item.Poster,
                StringComparer.OrdinalIgnoreCase).Take(24).ToList();
        if (choices.Count == 0) choices.Add(current);

        var dialog = new Window { Title = "比较与选择封面", Owner = this, Width = 760, Height = 570,
            MinWidth = 520, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel { Margin = new Thickness(16) };
        var hint = new TextBlock { Text = "只显示已缓存或来源明确提供的封面。选择封面只改此视频的显示图，不改变搜刮资料或审核结论。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var footer = new TextBlock { Text = "", Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var wrap = new WrapPanel();
        foreach (var candidate in choices)
        {
            var choice = candidate;
            var card = new StackPanel { Width = 155, Margin = new Thickness(5, 2, 5, 12) };
            var preview = new ContentControl { Content = CreateScrapeCover(choice, 140, 185) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(preview, "ScrapeCoverPreview");
            card.Children.Add(preview);
            card.Children.Add(new TextBlock { Text = choice.Source + " · " + choice.Code,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = choice.SourceUrl });
            var zoom = new Button { Content = "放大", Style = (Style)FindResource("Action") };
            System.Windows.Automation.AutomationProperties.SetAutomationId(zoom, "ScrapeCoverZoom");
            zoom.Click += (_, _) => ShowScrapeCoverZoom(choice);
            card.Children.Add(zoom);
            var use = new Button { Content = "用作视频封面", Style = (Style)FindResource("Primary"),
                IsEnabled = ManagedScrapeCoverPath(choice) is not null };
            System.Windows.Automation.AutomationProperties.SetAutomationId(use, "ScrapeCoverUse");
            var load = new Button { Content = "加载封面", Style = (Style)FindResource("Action"),
                IsEnabled = ManagedScrapeCoverPath(choice) is null && !string.IsNullOrWhiteSpace(choice.PosterUrl) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(load, "ScrapeCoverLoad");
            load.Click += async (_, _) =>
            {
                load.IsEnabled = false;
                await AdditionalScrapers.DownloadPosterAsync(choice, video.Id, DataDir);
                preview.Content = CreateScrapeCover(choice, 140, 185);
                use.IsEnabled = ManagedScrapeCoverPath(choice) is not null;
                footer.Text = use.IsEnabled ? "封面已缓存，可选用。" : "这个来源未能提供可下载的封面。";
            };
            card.Children.Add(load);
            use.Click += (_, _) =>
            {
                if (!UseScrapeCover(video, choice, out var error))
                    MessageBox.Show(dialog, error, "选择封面");
                else { footer.Text = "已保存为此视频的封面。"; dialog.Close(); }
            };
            card.Children.Add(use);
            wrap.Children.Add(card);
        }
        root.Children.Add(new ScrollViewer { Content = wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = root;
        return dialog;
    }

    FrameworkElement CreateScrapeCover(MovieMetadata record, double width = 120, double height = 170)
    {
        var frame = new Border
        {
            Width = width, Height = height, Margin = new Thickness(0, 5, 12, 8),
            Background = new SolidColorBrush(Color.FromRgb(232, 237, 247)),
            CornerRadius = new CornerRadius(6), ClipToBounds = true
        };
        try
        {
            // Only open posters managed by this library. A source or plugin must
            // not be able to make the preview read an arbitrary local path.
            var path = ManagedScrapeCoverPath(record);
            if (path is not null)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = (int)Math.Ceiling(width * 2);
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                frame.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
                return frame;
            }
        }
        catch (Exception)
        {
            // Missing or invalid cached covers never block review.
        }
        frame.Child = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(record.PosterUrl) ? "来源未提供封面" : "封面尚未缓存",
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7),
            Foreground = Brushes.SlateGray, FontSize = 11
        };
        return frame;
    }
}

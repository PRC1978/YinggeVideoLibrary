using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Application = System.Windows.Application;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    public int WriteCoverReloadDiagnostic(string destination)
    {
        var expected = Path.Combine(DataDir, "posters", "cover-diagnostic-second.png");
        var passed = AppPaths.IsRedirected &&
            config.CustomPosters.TryGetValue("cover-diagnostic-video", out var chosen) &&
            string.Equals(chosen, expected, StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(destination, JsonSerializer.Serialize(new
        {
            passed, dataDirectoryRedirected = AppPaths.IsRedirected,
            customPosterLoadedOnRestart = passed, realLibraryRead = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    public async Task<int> WriteHiddenCoverDiagnosticAsync(string destination)
    {
        var checks = new Dictionary<string, object?>(StringComparer.Ordinal);
        var folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(folder);
        try
        {
            if (!AppPaths.IsRedirected)
                throw new InvalidOperationException("封面界面验收必须使用 --data-dir 隔离目录。");
            var posterDir = Path.Combine(DataDir, "posters");
            Directory.CreateDirectory(posterDir);
            var firstPath = Path.Combine(posterDir, "cover-diagnostic-first.png");
            var secondPath = Path.Combine(posterDir, "cover-diagnostic-second.png");
            WriteTinyCover(firstPath, 210, 88, 103);
            WriteTinyCover(secondPath, 80, 135, 202);
            var video = new VideoItem { Id = "cover-diagnostic-video", Path = Path.Combine(DataDir, "synthetic.mp4"),
                Title = "合成验收视频", Folder = DataDir, Size = 0, Modified = DateTime.UtcNow };
            var first = new MovieMetadata { Code = "TEST-001", Title = "合成作品", Source = "TMDB",
                Poster = firstPath, PosterUrl = "https://image.tmdb.org/t/p/w500/example.jpg",
                DetailFetched = true, DetailIdentityVerified = true };
            var second = new MovieMetadata { Code = "TEST-001", Title = "合成作品", Source = "Bangumi",
                Poster = secondPath, PosterUrl = "https://lain.bgm.tv/pic/cover/example.jpg",
                DetailFetched = true, DetailIdentityVerified = true };
            ScrapeCandidateCache.SaveTitleCandidates(DataDir, new ScrapeCandidateCache.TitleCandidateSnapshot
            {
                VideoId = video.Id, Query = "合成作品", SearchedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddDays(1),
                Candidates = [new ScrapeCandidateCache.RankedTitleCandidate
                {
                    Rank = 1, Score = 90, SearchMetadata = second.Clone(), DetailMetadata = second.Clone(),
                    DetailStatus = "verified", DetailFetchedUtc = DateTime.UtcNow
                }]
            });
            var review = new AiReviewRecord { VideoId = video.Id, CandidateCode = first.Code,
                CandidateTitle = first.Title, CandidateSource = first.Source,
                CandidatePoster = first.Poster, CandidatePosterUrl = first.PosterUrl,
                CandidateDetails = first.Clone(), Status = "pending" };

            var compare = BuildScrapeCoverComparison(video, review);
            compare.Left = -30000; compare.Top = -30000;
            compare.WindowStartupLocation = WindowStartupLocation.Manual;
            compare.Show();
            await Dispatcher.InvokeAsync(compare.UpdateLayout, DispatcherPriority.Render);
            var previews = FindCoverControls(compare, "ScrapeCoverPreview").OfType<ContentControl>().ToList();
            var useButtons = FindCoverControls(compare, "ScrapeCoverUse").OfType<Button>().ToList();
            var zoomButtons = FindCoverControls(compare, "ScrapeCoverZoom").OfType<Button>().ToList();
            checks["comparisonHasTwoSources"] = previews.Count == 2;
            checks["comparisonShowsDecodedCovers"] = previews.All(control =>
                FindVisuals<Image>(control).Any(image => image.Source is BitmapImage));
            checks["zoomControlsAvailable"] = zoomButtons.Count == 2 && zoomButtons.All(button => button.IsEnabled);
            SaveCoverScreenshot(compare, Path.Combine(folder, "cover-comparison.png"));

            var zoom = BuildScrapeCoverZoom(first);
            checks["zoomWindowCreated"] = zoom is not null;
            if (zoom is not null)
            {
                zoom.Left = -30000; zoom.Top = -30000;
                zoom.WindowStartupLocation = WindowStartupLocation.Manual;
                zoom.Show();
                await Dispatcher.InvokeAsync(zoom.UpdateLayout, DispatcherPriority.Render);
                SaveCoverScreenshot(zoom, Path.Combine(folder, "cover-zoom.png"));
                zoom.Close();
            }
            if (useButtons.Count == 2)
            {
                useButtons[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, useButtons[1]));
                var disk = JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(ConfigPath));
                checks["manualChoicePersisted"] = disk?.CustomPosters.TryGetValue(video.Id, out var chosen) == true &&
                    string.Equals(chosen, secondPath, StringComparison.OrdinalIgnoreCase);
                checks["reviewIdentityUnchanged"] = review.CandidateSource == "TMDB" && review.Status == "pending";
            }
            else compare.Close();
            var candidateDialogChecked = false;
            var candidateDialogHasCover = false;
            var selectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            var selectionAttempts = 0;
            selectionTimer.Tick += (_, _) =>
            {
                selectionAttempts++;
                var selection = Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(window => window.Title == "选择搜刮候选");
                if (selection is null)
                {
                    if (selectionAttempts >= 20) selectionTimer.Stop();
                    return;
                }
                selectionTimer.Stop();
                selection.UpdateLayout();
                candidateDialogChecked = FindCoverControls(selection, "ScrapeCandidateList").Any() &&
                    FindCoverControls(selection, "ScrapeCandidateCoverPanel").Any() &&
                    FindCoverControls(selection, "ScrapeCandidateCoverZoom").Any();
                candidateDialogHasCover = FindVisuals<Image>(selection)
                    .Any(image => image.Source is BitmapImage);
                SaveCoverScreenshot(selection, Path.Combine(folder, "cover-candidate-selection.png"));
                selection.DialogResult = false;
            };
            selectionTimer.Start();
            ChooseScrapeCandidate(video, [(first, 90), (second, 85)]);
            selectionTimer.Stop();
            checks["candidateDialogControlsVisible"] = candidateDialogChecked;
            checks["candidateDialogShowsCover"] = candidateDialogHasCover;
            checks["foreignPathNotPreviewed"] = CreateScrapeCover(new MovieMetadata
            {
                Poster = Path.Combine(folder, "outside.png"), PosterUrl = "https://example.invalid/image.jpg"
            }) is Border { Child: TextBlock };
            checks["realLibraryRead"] = false;
            checks["passed"] = checks.Where(item => item.Value is bool && item.Key != "realLibraryRead")
                .All(item => item.Value is true);
        }
        catch (Exception ex)
        {
            checks["passed"] = false;
            checks["error"] = ex.ToString();
        }
        File.WriteAllText(destination, JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        return checks.TryGetValue("passed", out var passed) && passed is true ? 0 : 1;
    }

    static void WriteTinyCover(string path, byte red, byte green, byte blue)
    {
        var pixels = Enumerable.Repeat(new byte[] { blue, green, red, 255 }, 4)
            .SelectMany(bytes => bytes).ToArray();
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    static IEnumerable<DependencyObject> FindCoverControls(DependencyObject root, string automationId)
        => FindVisuals<DependencyObject>(root).Where(item =>
            AutomationProperties.GetAutomationId(item) == automationId);

    static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in FindVisuals<T>(VisualTreeHelper.GetChild(root, index)))
                yield return child;
    }

    static void SaveCoverScreenshot(Window window, string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}

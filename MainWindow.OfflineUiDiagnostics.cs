using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    internal static void PrepareOfflineUiDiagnostic(string outputDirectory)
    {
        if (!Path.IsPathFullyQualified(outputDirectory) ||
            Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new ArgumentException("Use a new absolute evidence directory");
        InteractiveUiDiagnostics.Prepare(Path.Combine(outputDirectory, "report.json"), 800, 550, UiTheme.Glass);
        // Prepare installs the ephemeral credential provider before fixture
        // creation; keep the requirement explicit for this offline entry too.
        WindowsCredentialStore.EnableIsolatedDiagnostics();
    }

    internal async Task<int> WriteOfflineUiDiagnosticAsync(string outputDirectory)
    {
        var checks = new Dictionary<string, bool>();
        var layouts = new List<object>();
        string? error = null;
        var originalTheme = UiTheme.Current;
        var originalMotion = config.UiMotion;
        var themes = UiTheme.Modes.Select(mode => mode.Id).ToArray();
        string[] pages = ["all", "recent", "favorites", "status", "scrape", "account", "review", "settings"];
        var skippedPages = new Dictionary<string, string>
        {
            ["categories"] = "Graph has a separate acceptance owner; not included in this UI content matrix.",
            ["folders"] = "Asynchronous folder rendering requires a separate completion contract before an offline capture is reliable.",
            ["recommendations"] = "Asynchronous ranking and poster rendering are outside this synchronous offline matrix.",
            ["roles"] = "Profile load normally completes after Loaded; that event is deliberately never raised here.",
            ["plugins"] = "This matrix does not scan application plugin directories or exercise plugin permissions.",
            ["online"] = "WebView2 initialization requires a native window and network-capable browser surface."
        };
        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var nested in Descendants(child)) yield return nested;
        }
        try
        {
            if (!AppPaths.IsRedirected || AppPaths.RedirectError is not null)
                throw new InvalidOperationException("Offline UI requires isolated paths");
            var fixture = JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(Path.Combine(DataDir, "library-index.json")))
                ?? throw new InvalidDataException("Missing fixture index");
            videos = ItemsFromIndex(fixture, out var invalidCount);
            ApplySavedFields(videos);
            foreach (var video in videos) VideoItem.PreloadPoster(video.Poster);
            checks["fixtureHasVideosAndReviews"] = videos.Count == 12 && invalidCount == 0 && config.AiReviews.Count == 4;
            config.UiMotion = UiMotion.Smooth;
            var root = Content as FrameworkElement ?? throw new InvalidOperationException("Missing content root");
            foreach (var theme in themes)
            {
                UiTheme.Apply(theme, UiColorMode.Native);
                ApplyInterfaceMode();
                foreach (var (width, height) in new[] { (800, 550), (1240, 780), (1600, 1000) })
                foreach (var page in pages)
                {
                    Width = width; Height = height;
                    ShowView(page);
                    root.Measure(new Size(width, height));
                    root.Arrange(new Rect(0, 0, width, height));
                    root.UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    var key = $"{theme}.{width}x{height}.{page}";
                    checks[key + ".noNativeWindow"] = PresentationSource.FromVisual(this) is null;
                    checks[key + ".contentViewport"] = Math.Abs(root.ActualWidth - width) < 1 && Math.Abs(root.ActualHeight - height) < 1;
                    var buttonData = new List<object>();
                    if (page == "scrape")
                    {
                        var failure = Descendants(ScrapePanel).OfType<Button>()
                            .FirstOrDefault(button => Equals(button.Content, "查看失败原因"));
                        checks[key + ".failureButtonExists"] = failure is not null;
                        if (failure is not null)
                        {
                            var bounds = failure.TransformToAncestor(root)
                                .TransformBounds(new Rect(new Point(), failure.RenderSize));
                            var horizontalFits = bounds.Width > 0 && bounds.Left >= -1 && bounds.Right <= width + 1;
                            var verticalFits = bounds.Height > 0 && bounds.Top >= -1 && bounds.Bottom <= height + 1;
                            checks[key + ".failureButtonHorizontalFits"] = horizontalFits;
                            checks[key + ".failureButtonInViewport"] = verticalFits;
                            checks[key + ".failureButtonTabPolicy"] = failure.Focusable && KeyboardNavigation.GetIsTabStop(failure);
                            buttonData.Add(new { name = "查看失败原因", bounds.X, bounds.Y, bounds.Width, bounds.Height,
                                horizontalFits, verticalFits, failure.Focusable, tabStop = KeyboardNavigation.GetIsTabStop(failure) });
                        }
                    }
                    var background = UiThemeState.Instance.PopupSurfaceBrush.Color;
                    var foreground = UiThemeState.Instance.TextPrimaryBrush.Color;
                    var contrast = ThemeContrast(OpaqueOver(foreground, background), background);
                    checks[key + ".popupOpaque"] = background.A == 255;
                    checks[key + ".popupTextContrast"] = contrast >= 4.5;
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var pixels = new byte[width * height * 4];
                    bitmap.CopyPixels(pixels, width * 4, 0);
                    var titleBounds = PageTitle.TransformToAncestor(root)
                        .TransformBounds(new Rect(new Point(), PageTitle.RenderSize));
                    var previousTitleVisibility = PageTitle.Visibility;
                    RenderTargetBitmap withoutTitle;
                    try
                    {
                        // Hidden preserves geometry: differences inside these
                        // bounds prove that the production heading actually
                        // painted, rather than accepting a plain background.
                        PageTitle.Visibility = Visibility.Hidden;
                        root.UpdateLayout();
                        withoutTitle = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                        withoutTitle.Render(root);
                    }
                    finally { PageTitle.Visibility = previousTitleVisibility; root.UpdateLayout(); }
                    var baseline = new byte[pixels.Length];
                    withoutTitle.CopyPixels(baseline, width * 4, 0);
                    var titleChangedPixels = 0;
                    var opaquePixels = 0;
                    var distinctPixels = new HashSet<int>();
                    for (var offset = 0; offset < pixels.Length; offset += 4)
                    {
                        if (pixels[offset + 3] > 0) opaquePixels++;
                        distinctPixels.Add(BitConverter.ToInt32(pixels, offset));
                        var x = (offset / 4) % width;
                        var y = (offset / 4) / width;
                        if (x >= titleBounds.Left && x < titleBounds.Right &&
                            y >= titleBounds.Top && y < titleBounds.Bottom &&
                            BitConverter.ToInt32(pixels, offset) != BitConverter.ToInt32(baseline, offset))
                            titleChangedPixels++;
                    }
                    checks[key + ".renderHasNonTransparentPixels"] = opaquePixels > 0;
                    checks[key + ".productionTitlePainted"] = titleChangedPixels >= 8;
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var imageFile = $"{theme}-{width}x{height}-{page}-content.png";
                    using (var stream = File.Create(Path.Combine(outputDirectory, imageFile))) encoder.Save(stream);
                    layouts.Add(new { theme, width, height, page, imageFile, root.ActualWidth, root.ActualHeight,
                        windowActualWidth = ActualWidth, windowActualHeight = ActualHeight,
                        title = PageTitle.Text,
                        titleBounds = new { titleBounds.X, titleBounds.Y, titleBounds.Width, titleBounds.Height },
                        titleChangedPixels, opaquePixels, distinctPixelCount = distinctPixels.Count,
                        buttons = buttonData, popupBackground = background.ToString(), popupText = foreground.ToString(), contrast,
                        reviewRows = page == "review" ? ReviewQueuePanel.Children.Count : (int?)null });
                }
            }
            checks["completeDeclaredContentMatrix"] = layouts.Count == themes.Length * 3 * pages.Length;
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally { config.UiMotion = originalMotion; UiTheme.Apply(originalTheme); }
        var passed = error is null && checks.Count > 0 && checks.Values.All(value => value);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "report.json"), JsonSerializer.Serialize(new
        {
            passed, realLibraryRead = false, nativeWindowShown = false, inputInjected = false,
            scope = "Offline production content Measure/Arrange/RenderTargetBitmap only. No native window, popup opening, OS chrome, or actual keyboard task validation.",
            keyboardPhysicalValidation = "not-performed", themeCount = themes.Length, themes,
            pages, skippedPages, declaredCaptureCount = themes.Length * 3 * pages.Length,
            checks, layouts, error
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    /// <summary>Screenshot every interface mode at the three acceptance window
    /// sizes, covering the pages that differ per mode (library, scrape, review,
    /// settings). Runs fully offscreen against an isolated --data-dir library.
    /// Also asserts a few theme invariants so regressions fail loudly instead
    /// of shipping an unreadable mode.</summary>
    internal async Task<int> WriteHiddenThemeDiagnosticAsync(string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var report = new List<object>();
            foreach (var (mode, label, _) in UiTheme.Modes)
            {
                UiTheme.Apply(mode);
                ApplyInterfaceMode();
                foreach (var (width, height) in new[] { (800, 550), (1240, 780), (1600, 1000) })
                {
                    Width = width;
                    Height = height;
                    // The review page only exists outside classic mode; the
                    // status page only exists in classic mode.
                    var pages = UiTheme.IsClassicMode
                        ? new[] { "all", "scrape", "status", "settings" }
                        : new[] { "all", "scrape", "review", "roles", "settings" };
                    foreach (var page in pages)
                    {
                        ShowView(page);
                        LibraryScroll.ScrollToTop();
                        UpdateLayout();
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        UpdateLayout();
                        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(this);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(outputDirectory, $"{mode}-{width}x{height}-{page}.png"));
                        encoder.Save(output);
                        report.Add(new
                        {
                            mode, width, height, page,
                            pageBackground = UiTheme.Brush(UiTheme.PageBackground).Color.ToString(),
                            textPrimary = UiTheme.Brush(UiTheme.TextPrimary).Color.ToString(),
                            reviewVisible = ReviewPanel.Visibility == Visibility.Visible,
                            rolesVisible = RolesPanel.Visibility == Visibility.Visible,
                            statusVisible = StatusPanel.Visibility == Visibility.Visible,
                            libraryNavVisible = LibraryNav.Visibility == Visibility.Visible,
                            statusNavVisible = StatusNav.Visibility == Visibility.Visible,
                            reviewNavVisible = ReviewNav.Visibility == Visibility.Visible,
                            pluginsNavVisible = PluginsNav.Visibility == Visibility.Visible,
                        });
                    }
                }
            }
            // Invariants: classic mode must keep the original nav set and the
            // status page; redesigned modes must expose review + plugins nav.
            UiTheme.Apply(UiTheme.Classic);
            ApplyInterfaceMode();
            if (ReviewNav.Visibility != Visibility.Collapsed || PluginsNav.Visibility != Visibility.Collapsed
                || StatusNav.Visibility != Visibility.Visible || AllNav.Visibility != Visibility.Visible)
                throw new InvalidOperationException("原版界面的导航集合被改变");
            UiTheme.Apply(UiTheme.Tech);
            ApplyInterfaceMode();
            if (ReviewNav.Visibility != Visibility.Visible || PluginsNav.Visibility != Visibility.Visible
                || LibraryNav.Visibility != Visibility.Visible || StatusNav.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("新版界面的导航集合不正确");
            // Probe why implicit stock-control styles do or do not land.
            var combo = ScrapeSourceBox;
            var probe = new
            {
                comboBackground = combo.Background?.ToString(),
                comboBackgroundSource = DependencyPropertyHelper.GetValueSource(combo, System.Windows.Controls.ComboBox.BackgroundProperty).BaseValueSource.ToString(),
                comboTemplate = combo.Template?.GetType().FullName,
                comboStyleIsNull = combo.Style is null,
                appResourceComboStyle = TryFindResource(typeof(System.Windows.Controls.ComboBox))?.ToString(),
            };
            File.WriteAllText(Path.Combine(outputDirectory, "theme-layout.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(outputDirectory, "style-probe.json"),
                JsonSerializer.Serialize(probe, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, "error.txt"), ex.ToString());
            return 1;
        }
    }
}

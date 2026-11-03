using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
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
            var root = Path.GetFullPath(Path.Combine(DataDir, "posters")) + Path.DirectorySeparatorChar;
            var path = string.IsNullOrWhiteSpace(record.Poster) ? "" : Path.GetFullPath(record.Poster);
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = (int)Math.Ceiling(width * 2);
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                frame.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
                return frame;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
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

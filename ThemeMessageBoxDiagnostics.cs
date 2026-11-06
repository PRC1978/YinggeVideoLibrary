using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace YinggeVideoLibrary;

internal static class ThemeMessageBoxDiagnostics
{
    internal static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var oldShutdown = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var owner = new Window
        {
            Title = "对话框验收容器", Left = -30000, Top = -30000,
            Width = 540, Height = 350, ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            owner.Show();
            var results = new List<object>();
            foreach (var (theme, colorMode, buttons, clickLabel, expected) in new[]
            {
                (UiTheme.Classic, UiColorMode.Native, MessageBoxButton.OK, "确定", MessageBoxResult.OK),
                (UiTheme.Cinema, UiColorMode.Native, MessageBoxButton.YesNo, "否", MessageBoxResult.No),
                (UiTheme.Glass, UiColorMode.Native, MessageBoxButton.YesNoCancel, "取消", MessageBoxResult.Cancel)
            })
            {
                UiTheme.Apply(theme, colorMode);
                var captured = false;
                var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
                { Interval = TimeSpan.FromMilliseconds(120) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    var dialog = Application.Current.Windows.Cast<Window>()
                        .FirstOrDefault(window => window != owner && window.IsVisible && window.Title == "隔离报错验收");
                    if (dialog is null) return;
                    dialog.UpdateLayout();
                    var width = Math.Max(1, (int)Math.Ceiling(dialog.ActualWidth));
                    var height = Math.Max(1, (int)Math.Ceiling(dialog.ActualHeight));
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(dialog);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(Path.Combine(outputDirectory, $"{theme}-dialog.png")))
                        encoder.Save(file);
                    captured = true;
                    var target = Descendants<Button>(dialog).FirstOrDefault(button =>
                        string.Equals(button.Content?.ToString(), clickLabel, StringComparison.Ordinal));
                    target?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, target));
                };
                timer.Start();
                var actual = ThemeMessageBox.Show(owner,
                    "角色档案无法读取；原文件已保留，请检查数据目录或恢复备份。",
                    "隔离报错验收", buttons, MessageBoxImage.Warning);
                timer.Stop();
                results.Add(new { theme, captured, expected = expected.ToString(), actual = actual.ToString(),
                    passed = captured && actual == expected });
            }
            var passed = results.All(result => (bool)result.GetType().GetProperty("passed")!.GetValue(result)!);
            File.WriteAllText(Path.Combine(outputDirectory, "dialog-results.json"),
                JsonSerializer.Serialize(new { passed, results }, new JsonSerializerOptions { WriteIndented = true }));
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "dialog-results.json"),
                JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
            return 1;
        }
        finally
        {
            owner.Close();
            Application.Current.ShutdownMode = oldShutdown;
        }
    }

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}

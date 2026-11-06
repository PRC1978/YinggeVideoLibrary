using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MessageBox = YinggeVideoLibrary.ThemeMessageBox;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Clipboard = System.Windows.Clipboard;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    const long AvScanMaxImageBytes = 8 * 1024 * 1024;

    async void OpenAvScanHandoff(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VideoItem video) return;
        if (!AvScanEligibility.CanOffer(video, metadata.GetValueOrDefault(video.Id))) return;
        if (!File.Exists(video.Path))
        {
            MessageBox.Show(this, "视频文件当前不可访问，请连接所在磁盘后重试。", "AVScan 搜图",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string? generated = null;
        try
        {
            var ffmpeg = FindFfmpeg();
            if (ffmpeg is not null)
                generated = await Task.Run(() => GenerateAvScanFrame(video.Path, ffmpeg));
            var entered = ShowAvScanHandoffDialog(generated);
            if (entered is not null) await ScrapeVideoWithCodeAsync(video, entered);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "识图准备失败：" + ex.Message, "AVScan 搜图",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (generated is not null) try { File.Delete(generated); } catch { }
        }
    }

    static string? GenerateAvScanFrame(string videoPath, string ffmpeg)
    {
        var directory = Path.Combine(DataDir, "avscan-frames");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".jpg");
        foreach (var timestamp in new[] { "00:00:30", "00:00:10", "00:00:01" })
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = false, RedirectStandardOutput = false
                };
                foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-ss", timestamp,
                    "-i", videoPath, "-frames:v", "1", "-vf", "scale=960:-2", "-q:v", "4", "-y", destination })
                    process.StartInfo.ArgumentList.Add(argument);
                process.Start();
                if (!process.WaitForExit(15000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                    continue;
                }
                if (process.ExitCode == 0 && File.Exists(destination) &&
                    new FileInfo(destination).Length is > 0 and <= AvScanMaxImageBytes)
                    return destination;
            }
            catch { /* A failed seek may succeed at an earlier frame. */ }
            try { File.Delete(destination); } catch { }
        }
        return null;
    }

    string? ShowAvScanHandoffDialog(string? generated)
    {
        var dialog = new Window
        {
            Title = "AVScan 以图搜番号", Owner = this, Width = 690, Height = 650,
            MinWidth = 480, MinHeight = 450, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = UiTheme.SurfaceBrush, Foreground = UiTheme.TextPrimaryBrush
        };
        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(new TextBlock { Text = "先核对截图，再由你上传", FontSize = 19,
            FontWeight = FontWeights.SemiBold, Foreground = UiTheme.TextPrimaryBrush });
        body.Children.Add(new TextBlock
        {
            Text = "AVScan 是第三方网站。点击“打开网站”后，请在网页中自行选择这张截图；程序不会自动上传视频或图片。识图结果仅作为线索，输入番号后仍走现有资料搜刮与审核。",
            TextWrapping = TextWrapping.Wrap, Foreground = UiTheme.TextSecondaryBrush,
            Margin = new Thickness(0, 8, 0, 12)
        });
        var preview = new Image { Height = 260, Stretch = Stretch.Uniform };
        var pathBox = new TextBox { IsReadOnly = true, Text = generated ?? "尚无截图：请选一张本地 JPG、PNG 或 WEBP 图片",
            Margin = new Thickness(0, 10, 0, 8) };
        void Display(string? path)
        {
            preview.Source = null;
            if (path is null) return;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            preview.Source = image;
            pathBox.Text = path;
        }
        try { Display(generated); } catch { generated = null; }
        body.Children.Add(preview);
        body.Children.Add(pathBox);
        string? selected = generated;
        var fileActions = new WrapPanel();
        Button Action(string label, Action callback)
        {
            var button = new Button { Content = label, Style = (Style)FindResource("Action"),
                Margin = new Thickness(0, 0, 8, 8) };
            button.Click += (_, _) =>
            {
                try { callback(); }
                catch (Exception ex)
                {
                    MessageBox.Show(dialog, "操作未完成：" + ex.Message, "AVScan 搜图",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            fileActions.Children.Add(button);
            return button;
        }
        Action("选择其他截图", () =>
        {
            var picker = new OpenFileDialog { Filter = "图片|*.jpg;*.jpeg;*.png;*.webp", CheckFileExists = true };
            if (picker.ShowDialog(dialog) != true) return;
            if (new FileInfo(picker.FileName).Length > AvScanMaxImageBytes)
            {
                MessageBox.Show(dialog, "AVScan 只接受不超过 8 MB 的图片。", "AVScan 搜图");
                return;
            }
            try { Display(picker.FileName); selected = picker.FileName; }
            catch (Exception ex) { MessageBox.Show(dialog, "图片无法预览：" + ex.Message, "AVScan 搜图"); }
        });
        Action("复制截图路径", () =>
        {
            if (selected is not null) Clipboard.SetText(selected);
        });
        Action("定位截图", () =>
        {
            if (selected is null) return;
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select," + selected);
            Process.Start(start);
        });
        Action("打开 AVScan 网站", () =>
        {
            Process.Start(new ProcessStartInfo("https://avscan.cc/") { UseShellExecute = true });
        });
        body.Children.Add(fileActions);
        body.Children.Add(new TextBlock { Text = "网站找到的番号（仅填番号，不填网址）：",
            Foreground = UiTheme.TextPrimaryBrush, Margin = new Thickness(0, 10, 0, 5) });
        var codeBox = new TextBox { Height = 34, ToolTip = "例如 ABC-123" };
        body.Children.Add(codeBox);
        string? result = null;
        var search = new Button { Content = "用此番号重新搜刮", Style = (Style)FindResource("Action"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        search.Click += (_, _) =>
        {
            var value = codeBox.Text.Trim();
            var parsed = JavDbScraper.GuessCodes(value);
            if (parsed.Count != 1 || !ScrapeCodeIdentity.Same(value, parsed[0]))
            {
                MessageBox.Show(dialog, "请只输入一个可识别的完整番号。", "AVScan 搜图");
                return;
            }
            result = parsed[0];
            dialog.DialogResult = true;
        };
        body.Children.Add(search);
        dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return dialog.ShowDialog() == true ? result : null;
    }
}

using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using FontFamily = System.Windows.Media.FontFamily;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    static string ReadEmbeddedNotice(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException("发布程序缺少许可或第三方声明文本：" + resourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static string ReadEmbeddedLicense() => ReadEmbeddedNotice("YinggeVideoLibrary.LICENSE");

    internal static string ReadEmbeddedWebView2License() =>
        ReadEmbeddedNotice("YinggeVideoLibrary.ThirdParty.WebView2.LICENSE");

    internal static string ReadEmbeddedWebView2Notice() =>
        ReadEmbeddedNotice("YinggeVideoLibrary.ThirdParty.WebView2.NOTICE");

    internal static string ReadEmbeddedDotNetCoreRuntimeLicense() =>
        ReadEmbeddedNotice("YinggeVideoLibrary.ThirdParty.NETCoreRuntime.LICENSE");

    internal static string ReadEmbeddedDotNetCoreRuntimeNotices() =>
        ReadEmbeddedNotice("YinggeVideoLibrary.ThirdParty.NETCoreRuntime.NOTICES");

    internal static string ReadEmbeddedWindowsDesktopRuntimeLicense() =>
        ReadEmbeddedNotice("YinggeVideoLibrary.ThirdParty.WindowsDesktopRuntime.LICENSE");

    FrameworkElement BuildLicenseSettingsEntry()
    {
        var row = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        row.Children.Add(new TextBlock
        {
            Text = "开源许可", FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 7)
        });
        var button = new Button
        {
            Content = "查看开源许可与第三方声明", Style = (Style)FindResource("Action"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        button.Click += (_, _) => ShowLicenseDialog();
        row.Children.Add(button);
        return row;
    }

    void ShowLicenseDialog()
    {
        (string Label, string Text)[] documents;
        try
        {
            documents =
            [
                ("映格视频库 · MIT 许可", ReadEmbeddedLicense()),
                ("Microsoft.Web.WebView2 1.0.3595.46 · 许可", ReadEmbeddedWebView2License()),
                ("Microsoft.Web.WebView2 1.0.3595.46 · 第三方声明", ReadEmbeddedWebView2Notice()),
                (".NET Runtime 9.0.17 · 许可", ReadEmbeddedDotNetCoreRuntimeLicense()),
                (".NET Runtime 9.0.17 · 第三方声明", ReadEmbeddedDotNetCoreRuntimeNotices()),
                ("Windows Desktop Runtime 9.0.17 · 许可", ReadEmbeddedWindowsDesktopRuntimeLicense())
            ];
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "开源许可",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = new Window
        {
            Title = "开源许可与第三方声明", Owner = this,
            Width = 690, Height = 550, MinWidth = 480, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var layout = new DockPanel { Margin = new Thickness(22) };
        var close = new Button
        {
            Content = "关闭", Width = 86, Height = 34,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        close.Click += (_, _) => dialog.Close();
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(close);
        var documentPicker = new ComboBox
        {
            ItemsSource = documents.Select(item => item.Label).ToArray(),
            SelectedIndex = 0, Height = 34,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(documentPicker, Dock.Top);
        layout.Children.Add(documentPicker);
        var text = new TextBox
        {
            Text = documents[0].Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            BorderThickness = new Thickness(0), Background = Brushes.White,
            FontFamily = new FontFamily("Consolas"), FontSize = 13
        };
        documentPicker.SelectionChanged += (_, _) =>
        {
            if (documentPicker.SelectedIndex >= 0 && documentPicker.SelectedIndex < documents.Length)
            {
                text.Text = documents[documentPicker.SelectedIndex].Text;
                text.ScrollToHome();
            }
        };
        layout.Children.Add(text);
        dialog.Content = layout;
        dialog.ShowDialog();
    }
}

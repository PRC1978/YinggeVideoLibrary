using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    FrameworkElement BuildErrorReportSettingsEntry()
    {
        var body = new StackPanel { Margin = new Thickness(0, 16, 0, 8) };
        body.Children.Add(new TextBlock { Text = "报错日志", FontWeight = FontWeights.SemiBold,
            Foreground = UiTheme.TextPrimaryBrush });
        body.Children.Add(new TextBlock
        {
            Text = "只导出最近 100 条错误记录，不包含视频、封面、资料库索引或其他诊断文件。错误文字仍可能含路径或标题；先在本机加密，再决定是否上传。",
            TextWrapping = TextWrapping.Wrap, Foreground = UiTheme.TextSecondaryBrush,
            Margin = new Thickness(0, 4, 0, 8)
        });
        body.Children.Add(new TextBlock { Text = "本地导出无需登录；上传请使用下方的云端账号入口，登录后预览脱敏内容。",
            TextWrapping = TextWrapping.Wrap, Foreground = UiTheme.TextSecondaryBrush,
            Margin = new Thickness(0, 4, 0, 8) });
        var buttons = new WrapPanel();
        var export = new Button { Content = "加密导出日志包", Style = (Style)FindResource("Action") };
        var decrypt = new Button { Content = "在本机解密查看", Style = (Style)FindResource("Action") };
        export.Click += async (_, _) => await ExportErrorReportAsync(export);
        decrypt.Click += async (_, _) => await DecryptErrorReportAsync(decrypt);
        buttons.Children.Add(export);
        buttons.Children.Add(decrypt);
        body.Children.Add(buttons);
        return body;
    }

    async Task DecryptErrorReportAsync(Button button)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择加密日志包", Filter = "映格加密日志包 (*.yglog)|*.yglog",
            CheckFileExists = true, Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;
        var passphrase = PromptErrorReportPassphrase("输入日志加密口令");
        if (passphrase is null) return;
        var output = new Microsoft.Win32.SaveFileDialog
        {
            Title = "在本机保存解密后的日志", Filter = "JSON 文件 (*.json)|*.json",
            DefaultExt = ".json", FileName = "yingge-error-decrypted.json"
        };
        if (output.ShowDialog(this) != true) return;
        button.IsEnabled = false;
        try
        {
            var encrypted = await File.ReadAllBytesAsync(picker.FileName);
            var snapshot = await Task.Run(() => EncryptedErrorReport.Decrypt(encrypted, passphrase));
            await File.WriteAllTextAsync(output.FileName, System.Text.Json.JsonSerializer.Serialize(snapshot));
            MessageBox.Show(this, "日志已在本机解密。该 JSON 是明文，请自行保管；上传入口不会接受它。", "解密完成");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                   or CryptographicException or System.Text.Json.JsonException)
        { MessageBox.Show(this, "解密失败：" + ex.Message, "报错日志", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { button.IsEnabled = true; }
    }

    async Task ExportErrorReportAsync(Button button)
    {
        button.IsEnabled = false;
        try
        {
            var snapshot = await Task.Run(() => EncryptedErrorReport.Capture(DataDir, messages.ToArray()));
            if (!EncryptedErrorReport.IsErrorOnly(snapshot))
            { MessageBox.Show(this, "当前没有可导出的错误记录。", "报错日志"); return; }
            var summary = $"错误记录 {snapshot.RecentMessages.Length} 条。\n\n错误文字可能包含本地路径或标题。仅创建本地加密文件，是否继续？";
            if (MessageBox.Show(this, summary, "预览日志范围", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var passphrase = PromptErrorReportPassphrase("设置日志加密口令");
            if (passphrase is null) return;
            var picker = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存加密日志包", Filter = "映格加密日志包 (*.yglog)|*.yglog",
                DefaultExt = EncryptedErrorReport.Extension,
                FileName = $"yingge-error-{DateTime.Now:yyyyMMdd-HHmmss}.yglog"
            };
            if (picker.ShowDialog(this) != true) return;
            var encrypted = await Task.Run(() => EncryptedErrorReport.Encrypt(snapshot, passphrase));
            await File.WriteAllBytesAsync(picker.FileName, encrypted);
            MessageBox.Show(this, "加密日志包已保存在本机。请保管口令：丢失后无法解密。", "导出完成");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { button.IsEnabled = true; }
    }

    string? PromptErrorReportPassphrase(string title)
    {
        var dialog = new Window
        {
            Title = title, Owner = this, Width = 440, Height = 205,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Background = UiTheme.SurfaceBrush
        };
        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock { Text = "输入至少 12 个字符的口令；不会保存在配置或服务器。",
            TextWrapping = TextWrapping.Wrap, Foreground = UiTheme.TextPrimaryBrush });
        var password = new PasswordBox { Height = 34, Margin = new Thickness(0, 12, 0, 12) };
        body.Children.Add(password);
        var confirm = new Button { Content = "继续", Style = (Style)FindResource("Action"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        confirm.Click += (_, _) =>
        {
            if (password.Password.Length < 12)
            { MessageBox.Show(dialog, "口令至少需要 12 个字符。", title); return; }
            dialog.DialogResult = true;
        };
        body.Children.Add(confirm);
        dialog.Content = body;
        password.Focus();
        var accepted = dialog.ShowDialog() == true;
        var value = accepted ? password.Password : null;
        password.Clear();
        return value;
    }
}

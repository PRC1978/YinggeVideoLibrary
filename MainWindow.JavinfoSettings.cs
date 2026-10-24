using System.IO;
using System.Windows;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    CheckBox? javinfoEnabledBox;
    PasswordBox? javinfoKeyBox;
    TextBox? javinfoDailyLimitBox;

    void InitializeJavinfoSettings()
    {
        if (ScrapePanel.Child is not StackPanel panel) return;
        ScrapeSourceBox.Items.Add(new ComboBoxItem { Content = "Javinfo 聚合来源（手动）" });

        var section = new StackPanel { Margin = new Thickness(0, 8, 0, 18) };
        section.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 14) });
        section.Children.Add(new TextBlock { Text = "Javinfo 聚合来源（可选付费）", FontWeight = FontWeights.SemiBold, FontSize = 16 });
        section.Children.Add(new TextBlock
        {
            Text = "手动搜索时可选择；不会加入“全部来源”或后台全量队列。需自行在 javinfo.dev 获取 API Key。",
            Foreground = System.Windows.Media.Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 12)
        });
        javinfoEnabledBox = new CheckBox { Content = "启用 Javinfo 手动搜刮", IsChecked = config.EnableJavinfo,
            Margin = new Thickness(0, 0, 0, 10) };
        javinfoEnabledBox.Checked += (_, _) => { if (!IsLoaded) return; config.EnableJavinfo = true; SaveConfig(); };
        javinfoEnabledBox.Unchecked += (_, _) => { if (!IsLoaded) return; config.EnableJavinfo = false; SaveConfig(); };
        section.Children.Add(javinfoEnabledBox);
        section.Children.Add(new TextBlock { Text = "Javinfo API Key", FontWeight = FontWeights.SemiBold });
        javinfoKeyBox = new PasswordBox { Password = config.JavinfoKey, Height = 36,
            Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 10), ToolTip = "密钥仅保存在 Windows 凭据管理器" };
        section.Children.Add(javinfoKeyBox);
        section.Children.Add(new TextBlock { Text = "每日最多尝试请求（1–100；包含失败请求）", FontWeight = FontWeights.SemiBold });
        javinfoDailyLimitBox = new TextBox { Text = config.JavinfoDailyRequestLimit.ToString(), Width = 92,
            Height = 34, Padding = new Thickness(8), HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 10) };
        section.Children.Add(javinfoDailyLimitBox);
        var save = new Button { Content = "保存 Javinfo 设置", Style = (Style)FindResource("Action"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        save.Click += (_, _) =>
        {
            if (!int.TryParse(javinfoDailyLimitBox.Text, out var limit) || limit is < 1 or > 100)
            {
                MessageBox.Show(this, "每日请求上限请输入 1 到 100。", "Javinfo 设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var key = javinfoKeyBox.Password.Trim();
            if (key.Length > 0 && !key.StartsWith("jvi_", StringComparison.Ordinal))
            {
                MessageBox.Show(this, "Javinfo API Key 应以 jvi_ 开头。", "Javinfo 设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                WindowsCredentialStore.Write("JavinfoKey", key);
                javinfoCredentialReady = true;
                config.JavinfoKey = key;
                config.JavinfoDailyRequestLimit = limit;
                SaveConfig();
                MessageBox.Show(this, "Javinfo 设置已保存；密钥已存入 Windows 凭据管理器。", "Javinfo 设置");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存 Javinfo 设置失败：" + ex.Message, "Javinfo 设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        section.Children.Add(save);
        section.Children.Add(new TextBlock
        {
            Text = "请求间隔至少 13 秒；每日按 UTC 日期计数。费用以 Javinfo 官方实时价格和账单为准：docs.javinfo.dev/docs/rate-limits",
            Foreground = System.Windows.Media.Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        });
        panel.Children.Insert(panel.Children.IndexOf(TmdbEnabledBox) + 2, section);
    }
}

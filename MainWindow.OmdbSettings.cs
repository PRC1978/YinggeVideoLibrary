using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    void InitializeOmdbSettings(StackPanel panel)
    {
        var section = new StackPanel { Margin = new Thickness(0, 8, 0, 18) };
        section.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 14) });
        section.Children.Add(new TextBlock { Text = "OMDb 电影 / 剧集（可选手动来源）",
            FontWeight = FontWeights.SemiBold, FontSize = 16 });
        section.Children.Add(new TextBlock
        {
            Text = "仅对你单独选择的标题查询。按 IMDb ID 再核对详情，候选只进入人工审核；不会加入全部来源或后台全量队列。免费 API Key 的每日额度由 OMDb 管理。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 12)
        });
        var enabled = new CheckBox { Content = "启用 OMDb 手动搜刮", IsChecked = config.EnableOmdb,
            Margin = new Thickness(0, 0, 0, 10) };
        enabled.Checked += (_, _) => { if (!IsLoaded) return; config.EnableOmdb = true; SaveConfig(); RefreshSourceDiagnostics(); };
        enabled.Unchecked += (_, _) => { if (!IsLoaded) return; config.EnableOmdb = false; SaveConfig(); RefreshSourceDiagnostics(); };
        section.Children.Add(enabled);
        var status = new TextBlock { Text = OmdbScraper.CredentialReady() ? "Windows 凭据中已有 OMDb API Key" : "尚未设置 OMDb API Key",
            Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 5) };
        section.Children.Add(status);
        var keyBox = new PasswordBox { Height = 36, Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8), ToolTip = "留空不会改变已保存的密钥；输入新密钥后点击保存。" };
        section.Children.Add(keyBox);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var save = new Button { Content = "保存 OMDb API Key", Style = (Style)FindResource("Action"),
            Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) =>
        {
            var key = keyBox.Password.Trim();
            if (key.Length is < 4 or > 128 || key.Any(char.IsWhiteSpace))
            {
                MessageBox.Show(this, "请输入有效的 OMDb API Key（4–128 字，不含空格）。", "OMDb 设置",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                WindowsCredentialStore.Write("OmdbKey", key);
                keyBox.Clear();
                status.Text = "Windows 凭据中已有 OMDb API Key";
                RefreshSourceDiagnostics();
                MessageBox.Show(this, "OMDb API Key 已保存在当前 Windows 用户的凭据中。", "OMDb 设置");
            }
            catch
            {
                MessageBox.Show(this, "保存 OMDb API Key 失败，请检查 Windows 凭据管理器。", "OMDb 设置",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        actions.Children.Add(save);
        var remove = new Button { Content = "清除密钥", Style = (Style)FindResource("Action") };
        remove.Click += (_, _) =>
        {
            try
            {
                WindowsCredentialStore.Write("OmdbKey", "");
                keyBox.Clear();
                config.EnableOmdb = false;
                enabled.IsChecked = false;
                SaveConfig();
                status.Text = "尚未设置 OMDb API Key";
                RefreshSourceDiagnostics();
            }
            catch
            {
                MessageBox.Show(this, "清除 OMDb API Key 失败，请检查 Windows 凭据管理器。", "OMDb 设置",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        actions.Children.Add(remove);
        section.Children.Add(actions);
        section.Children.Add(new TextBlock { Text = "申请与用量说明：omdbapi.com/apikey.aspx",
            Foreground = Brushes.SlateGray, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Insert(panel.Children.IndexOf(TmdbEnabledBox) + 3, section);
    }
}

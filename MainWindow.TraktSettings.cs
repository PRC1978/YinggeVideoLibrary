using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel = System.Windows.Controls.StackPanel;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    void InitializeTraktSettings(StackPanel panel)
    {
        var section = new StackPanel { Margin = new Thickness(0, 8, 0, 18) };
        section.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 14) });
        section.Children.Add(new TextBlock { Text = "Trakt 电影 / 剧集（手动来源）",
            FontWeight = FontWeights.SemiBold, FontSize = 16 });
        section.Children.Add(new TextBlock
        {
            Text = "用你注册的 Trakt Client ID 搜索单部电影或剧集。候选按 Trakt ID、IMDb/TMDB 编号、标题和年份核对后进入人工审核。Trakt 不参加全部来源与后台全量队列。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 10)
        });
        var enabled = new CheckBox { Content = "启用 Trakt 手动搜刮", IsChecked = config.EnableTrakt,
            Margin = new Thickness(0, 0, 0, 10) };
        enabled.Checked += (_, _) => { if (!IsLoaded) return; config.EnableTrakt = true; SaveConfig(); RefreshSourceDiagnostics(); };
        enabled.Unchecked += (_, _) => { if (!IsLoaded) return; config.EnableTrakt = false; SaveConfig(); RefreshSourceDiagnostics(); };
        section.Children.Add(enabled);
        var status = new TextBlock
        {
            Text = TraktScraper.CredentialReady() ? "Windows 凭据中已有 Trakt Client ID" : "尚未设置 Trakt Client ID",
            Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 5)
        };
        section.Children.Add(status);
        var keyBox = new PasswordBox { Height = 36, Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8), ToolTip = "留空不会改变已保存的 Client ID。" };
        section.Children.Add(keyBox);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var save = new Button { Content = "保存 Trakt Client ID", Style = (Style)FindResource("Action"),
            Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) =>
        {
            var clientId = keyBox.Password.Trim();
            if (!TraktScraper.ValidClientId(clientId))
            {
                MessageBox.Show(this, "请输入有效的 Trakt Client ID（24–128 位字母、数字或 - _）。",
                    "Trakt 设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                WindowsCredentialStore.Write("TraktClientId", clientId);
                keyBox.Clear();
                status.Text = "Windows 凭据中已有 Trakt Client ID";
                RefreshSourceDiagnostics();
                MessageBox.Show(this, "Trakt Client ID 已保存在当前 Windows 用户的凭据中。",
                    "Trakt 设置");
            }
            catch
            {
                MessageBox.Show(this, "保存 Trakt Client ID 失败，请检查 Windows 凭据管理器。",
                    "Trakt 设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        actions.Children.Add(save);
        var remove = new Button { Content = "清除 Client ID", Style = (Style)FindResource("Action") };
        remove.Click += (_, _) =>
        {
            try
            {
                WindowsCredentialStore.Write("TraktClientId", "");
                keyBox.Clear();
                config.EnableTrakt = false;
                enabled.IsChecked = false;
                SaveConfig();
                status.Text = "尚未设置 Trakt Client ID";
                RefreshSourceDiagnostics();
            }
            catch
            {
                MessageBox.Show(this, "清除 Trakt Client ID 失败，请检查 Windows 凭据管理器。",
                    "Trakt 设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        actions.Children.Add(remove);
        section.Children.Add(actions);
        section.Children.Add(new TextBlock
        {
            Text = "申请地址：developer.trakt.tv/apps。需验证 GitHub 账号；应用内会标明 Trakt 评分出处。官方禁止目录批量采集。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        section.Children.Add(new TextBlock
        {
            Text = "映格视频库 uses Trakt but is not endorsed or certified by Trakt.",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        panel.Children.Insert(panel.Children.IndexOf(TmdbEnabledBox) + 4, section);
    }
}

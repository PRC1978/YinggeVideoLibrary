using System.IO;
using System.Windows;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
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
        AppendManualSourceOptions(ScrapeSourceBox);

        var section = new StackPanel { Margin = new Thickness(0, 8, 0, 18) };
        section.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 14) });
        section.Children.Add(new TextBlock { Text = "Javinfo 聚合来源（可选付费）", FontWeight = FontWeights.SemiBold, FontSize = 16 });
        section.Children.Add(new TextBlock
        {
            Text = "仅作为可选付费的手动备用来源。必须先核对当前视频的明确番号和日文作品原名；纯汉字原名须由你确认。只按精确番号查询，不加入“全部来源”或后台全量队列。",
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
        InitializeOmdbSettings(panel);
        InitializeTraktSettings(panel);
    }

    internal static void AppendManualSourceOptions(ComboBox sourceBox)
    {
        sourceBox.Items.Add(new ComboBoxItem { Content = "Javinfo 聚合来源（手动）" });
        sourceBox.Items.Add(new ComboBoxItem { Content = "Wikidata 电影 / 剧集 / 动画" });
        sourceBox.Items.Add(new ComboBoxItem { Content = "OMDb 电影 / 剧集（手动）" });
        sourceBox.Items.Add(new ComboBoxItem { Content = "Kitsu 动画" });
        sourceBox.Items.Add(new ComboBoxItem { Content = "Tenrai 动画（MyAnimeList）" });
        sourceBox.Items.Add(new ComboBoxItem { Content = "Trakt 电影 / 剧集（手动）" });
    }

    JavinfoSearchEvidence? PromptJavinfoEvidence(VideoItem video)
    {
        metadata.TryGetValue(video.Id, out var saved);
        var filenameCode = JavinfoSearchEvidence.CanonicalCode(JavDbScraper.GuessCode(video.Title));
        var storedCode = JavinfoSearchEvidence.CanonicalCode(video.Code)
            ?? JavinfoSearchEvidence.CanonicalCode(saved?.Code);
        var code = storedCode ?? filenameCode ?? "";
        var proposedTitle = JavinfoSearchEvidence.SuggestedJapaneseTitle(saved?.OriginalTitle)
            ?? JavinfoSearchEvidence.SuggestedJapaneseTitle(video.Title)
            ?? JavinfoSearchEvidence.SuggestedJapaneseTitle(saved?.Title) ?? "";
        var dialog = new Window { Title = "Javinfo 手动搜刮 · 核对付费查询", Owner = this,
            Width = 580, Height = 430, MinWidth = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "只在番号与日文作品原名都明确时查询。Javinfo 是付费来源，每次请求可能消耗额度。",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.SlateGray,
            Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "作品番号", FontWeight = FontWeights.SemiBold });
        var codeBox = new TextBox { Text = code, Height = 34, Margin = new Thickness(0, 5, 0, 12),
            ToolTip = "例如 ABC-123；不能填写模糊文件序号" };
        panel.Children.Add(codeBox);
        panel.Children.Add(new TextBlock { Text = "日文作品原名", FontWeight = FontWeights.SemiBold });
        var titleBox = new TextBox { Text = proposedTitle, Height = 34, Margin = new Thickness(0, 5, 0, 9),
            ToolTip = "填写已核对的日文原名；请勿填写中文译名或人物目录名称" };
        panel.Children.Add(titleBox);
        var confirmHan = new CheckBox { Content = "纯汉字标题：我已核对这是日文原名且与此番号对应",
            Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(confirmHan);
        var confirmMatch = new CheckBox { Content = "我已核对上方番号与日文原名属于当前视频",
            Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(confirmMatch);
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap, MinHeight = 34 };
        panel.Children.Add(error);
        JavinfoSearchEvidence? evidence = null;
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Width = 76, Style = (Style)FindResource("Action"),
            Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        actions.Children.Add(cancel);
        var search = new Button { Content = "按番号搜索", Width = 112, Style = (Style)FindResource("Primary") };
        search.Click += (_, _) =>
        {
            try
            {
                if (confirmMatch.IsChecked != true)
                    throw new InvalidOperationException("请先核对当前视频的番号和日文原名；没有发出付费请求。");
                if (storedCode is not null && filenameCode is not null &&
                    !storedCode.Equals(filenameCode, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("视频已存番号与文件名番号不一致，请先核对或修正；没有发出付费请求。");
                evidence = JavinfoSearchEvidence.Create(codeBox.Text, titleBox.Text,
                    confirmHan.IsChecked == true, storedCode ?? filenameCode);
                dialog.DialogResult = true;
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        actions.Children.Add(search);
        panel.Children.Add(actions);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => codeBox.Focus();
        return dialog.ShowDialog() == true ? evidence : null;
    }
}

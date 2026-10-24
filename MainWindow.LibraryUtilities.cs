using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using StackPanel = System.Windows.Controls.StackPanel;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    void InitializeLibraryUtilities()
    {
        if (SettingsPanel.Child is not StackPanel panel) return;
        var button = new Button { Content = "查找重复视频", Style = (Style)FindResource("Action"), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        button.Click += async (_, _) => await FindDuplicateVideosAsync(button);
        panel.Children.Insert(Math.Max(0, panel.Children.Count - 2), button);
    }

    async Task FindDuplicateVideosAsync(Button button)
    {
        button.IsEnabled = false;
        TaskStatusText.Text = "正在后台比对可能重复的视频…";
        try
        {
            var candidates = videos.GroupBy(item => item.Size).Where(group => group.Key > 0 && group.Count() > 1).SelectMany(group => group).ToList();
            var duplicateGroups = await Task.Run(async () =>
            {
                var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in candidates)
                {
                    if (isClosing) break;
                    try
                    {
                        await using var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
                        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                        var key = item.Size + ":" + digest;
                        if (!found.TryGetValue(key, out var paths)) found[key] = paths = [];
                        paths.Add(item.Path);
                    }
                    catch { /* A moved or locked file cannot be compared now. */ }
                }
                return found.Values.Where(paths => paths.Count > 1).ToList();
            });
            var summary = duplicateGroups.Count == 0 ? "未发现内容完全相同的视频。" : string.Join("\n\n", duplicateGroups.Take(12).Select((group, index) => $"第 {index + 1} 组（{group.Count} 部）：\n" + string.Join("\n", group.Take(5))));
            MessageBox.Show(this, $"按文件大小和 SHA-256 完整内容识别，找到 {duplicateGroups.Count} 组。\n\n{summary}" + (duplicateGroups.Count > 12 ? "\n\n仅显示前 12 组。" : ""), "重复视频");
            TaskStatusText.Text = $"重复检测完成 · {duplicateGroups.Count} 组";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "重复视频", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { button.IsEnabled = true; }
    }

    void AddSavedFilterMenu(System.Windows.Controls.ContextMenu menu)
    {
        var save = new MenuItem { Header = "保存当前智能筛选…", IsEnabled = !string.IsNullOrWhiteSpace(SearchBox.Text) };
        save.Click += (_, _) =>
        {
            var name = PromptText("保存智能筛选", "名称：", SearchBox.Text.Trim());
            if (string.IsNullOrWhiteSpace(name)) return;
            config.SmartFilters[name.Trim()] = SearchBox.Text.Trim(); SaveConfig();
        };
        menu.Items.Add(save);
        var submenu = new MenuItem { Header = "已保存的智能筛选", IsEnabled = config.SmartFilters.Count > 0 };
        foreach (var pair in config.SmartFilters.OrderBy(pair => pair.Key))
        {
            var item = new MenuItem { Header = pair.Key, ToolTip = pair.Value };
            item.Click += (_, _) => { SearchBox.Text = pair.Value; SearchBox.Focus(); };
            submenu.Items.Add(item);
        }
        menu.Items.Add(submenu);
        var remove = new MenuItem { Header = "删除已保存筛选…", IsEnabled = config.SmartFilters.Count > 0 };
        remove.Click += (_, _) =>
        {
            var name = PromptText("删除智能筛选", "输入要删除的名称：", "");
            if (name is not null && config.SmartFilters.Remove(name.Trim())) SaveConfig();
        };
        menu.Items.Add(remove);
        menu.Items.Add(new Separator());
    }
}

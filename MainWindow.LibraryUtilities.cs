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
        button.Click += (_, _) => FindDuplicateVideos(button);
        panel.Children.Insert(Math.Max(0, panel.Children.Count - 2), button);
        var siteListActions = new WrapPanel { Name = "SiteListImportActions", Margin = new Thickness(0, 8, 0, 8) };
        var import = new Button { Content = "导入站点收藏清单…", Style = (Style)FindResource("Action"),
            Margin = new Thickness(0, 0, 8, 0) };
        import.Click += OpenSiteListImport;
        siteListActions.Children.Add(import);
        var browse = new Button { Content = "查看已导入清单…", Style = (Style)FindResource("Action") };
        browse.Click += OpenSavedSiteLists;
        siteListActions.Children.Add(browse);
        panel.Children.Insert(Math.Max(0, panel.Children.Count - 2), siteListActions);
        var roles = new Button { Content = "人物与角色档案…", Style = (Style)FindResource("Action"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        roles.Click += (_, _) => OpenRoleProfiles();
        panel.Children.Insert(Math.Max(0, panel.Children.Count - 2), roles);
    }

    void FindDuplicateVideos(Button button)
    {
        button.IsEnabled = false;
        try
        {
            var candidateSizes = videos.Where(video => video.Size > 0)
                .GroupBy(video => video.Size).Where(group => group.Count() > 1)
                .Select(group => group.Key).ToHashSet();
            var snapshot = videos.Where(video => candidateSizes.Contains(video.Size))
                .Select(video => new DuplicateVideoInput(video.Id, video.Path,
                video.DisplayTitle, video.Size,
                video.CategoryList.Count > 0 ? video.CategoryList.ToArray() : [video.Category],
                video.Tags.ToArray(), metadata.TryGetValue(video.Id, out var details)
                    ? DuplicatePlatformRatings(details) : "")).ToArray();
            var browser = new DuplicateVideosWindow(snapshot, DataDir) { Owner = this };
            browser.ShowDialog();
            foreach (var transaction in browser.CompletedTransactions)
            {
                ApplyDuplicateLocalState(config, transaction.KeepId, transaction.After);
                ApplyDuplicateLocalState(configBaseline, transaction.KeepId, transaction.After);
                var affected = videos.FirstOrDefault(video => video.Id.Equals(transaction.KeepId,
                    StringComparison.OrdinalIgnoreCase) && video.Path.Equals(transaction.KeepPath,
                    StringComparison.OrdinalIgnoreCase));
                if (affected is null) continue;
                affected.CategoryList.Clear();
                affected.CategoryList.AddRange(transaction.After.VisibleCategories.Where(value => value != "未分类"));
                affected.Category = affected.CategoryList.FirstOrDefault() ?? "未分类";
                affected.Tags.Clear();
                affected.Tags.AddRange(transaction.After.VisibleTags);
                affected.RefreshTags();
            }
            if (browser.CompletedTransactions.Count > 0)
            {
                categoryDataVersion++;
                RenderCards();
            }
            if (browser.ScanResult is { } result)
                TaskStatusText.Text = $"重复检测完成 · {result.Groups.Count:N0} 组 · {result.Skipped:N0} 部跳过";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "重复视频", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { button.IsEnabled = true; }
    }

    static string DuplicatePlatformRatings(MovieMetadata details)
    {
        var values = RatingAggregation.DistinctValidRatings(details.PlatformRatings);
        if (values.Count == 0 && RatingAggregation.FromMetadata(details) is { } single)
            values.Add(single);
        return string.Join("；", values.Select(value =>
            $"{value.Platform} {value.Value / value.Maximum * 10d:0.#}/10"));
    }

    static void ApplyDuplicateLocalState(LibraryConfig target, string id, DuplicateLocalState state)
    {
        if (state.PrimaryCategory is null) target.Categories.Remove(id);
        else target.Categories[id] = state.PrimaryCategory;
        if (state.CategoryPaths is null) target.MultiCategories.Remove(id);
        else target.MultiCategories[id] = state.CategoryPaths.ToList();
        if (state.Tags is null) target.Tags.Remove(id);
        else target.Tags[id] = state.Tags.ToList();
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

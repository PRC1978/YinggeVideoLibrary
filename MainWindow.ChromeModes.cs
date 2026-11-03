using System.Windows;
using System.Windows.Controls;
using Panel = System.Windows.Controls.Panel;
using Button = System.Windows.Controls.Button;

namespace YinggeVideoLibrary;

/// <summary>
/// Mode-aware chrome: the classic mode keeps the original sidebar, status page
/// and standalone role window untouched; the four redesigned modes get the new
/// information architecture (library segments, first-class review page,
/// embedded role page, plugins as a real nav item, status cards relocated by
/// purpose). All of this only re-parents existing controls — business logic,
/// data and handlers are shared by every mode.
/// </summary>
public partial class MainWindow
{
    WrapPanel? librarySegments;
    RoleProfilesView? embeddedRoles;

    /// <summary>Apply the current interface mode's chrome. Called once during
    /// startup (after the scrape groups exist) and on every mode switch.</summary>
    void ApplyInterfaceMode()
    {
        var classic = UiTheme.IsClassicMode;
        if (SidebarColumn.Width.Value > 28)
            SidebarColumn.Width = new GridLength(UiTheme.Num(UiTheme.SidebarWidth));
        LibraryNav.Visibility = classic ? Visibility.Collapsed : Visibility.Visible;
        AllNav.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        RecentNav.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        FavoriteNav.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        StatusNav.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        ReviewNav.Visibility = classic ? Visibility.Collapsed : Visibility.Visible;
        PluginsNav.Visibility = classic ? Visibility.Collapsed : Visibility.Visible;
        ArrangeScrapeGroups();
        RelocateStatusCards();
        if (ReviewQueuePanel.Parent is ScrollViewer reviewScroll)
            ConfigureReviewQueueScrolling(reviewScroll);
        // The sphere graph caches a rendered bitmap in the palette active at
        // construction time. Rebuild it after a theme switch so links and
        // labels never retain the previous mode's colors.
        lastCategoryRenderVersion = -1;
        // Views that only exist in one mode fall back to a page that does.
        if (classic && view is "review") view = "scrape";
        else if (classic && view is "roles") view = "all";
        else if (!classic && view is "status") view = "all";
        ShowView(view);
    }

    void NavLibrary(object sender, RoutedEventArgs e) => ShowView("all");
    void NavReview(object sender, RoutedEventArgs e) => ShowView("review");
    void NavPlugins(object sender, RoutedEventArgs e) => ShowView("plugins");

    /// <summary>In-page segment switcher for the redesigned library page:
    /// 全部 / 最近 / 收藏 stay one click apart without three sidebar entries.</summary>
    void EnsureLibrarySegments()
    {
        if (librarySegments is not null) return;
        librarySegments = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var (id, label) in new[] { ("all", "全部"), ("recent", "最近"), ("favorites", "收藏") })
        {
            var button = new System.Windows.Controls.Button
            {
                Content = label, Style = (Style)FindResource("Action"), Tag = id,
                MinWidth = 72
            };
            button.Click += (_, _) => ShowView(id);
            librarySegments.Children.Add(button);
        }
        // Insert right under the section header of the library page.
        if (Cards.Parent is Panel host) host.Children.Insert(1, librarySegments);
    }

    void UpdateLibrarySegments()
    {
        if (librarySegments is null) return;
        librarySegments.Visibility = !UiTheme.IsClassicMode && view is "all" or "recent" or "favorites"
            ? Visibility.Visible : Visibility.Collapsed;
        foreach (var child in librarySegments.Children.OfType<System.Windows.Controls.Button>())
        {
            var active = child.Tag as string == view;
            child.SetBinding(System.Windows.Controls.Button.BackgroundProperty,
                new System.Windows.Data.Binding(active ? "SelectionBackgroundBrush" : "SurfaceBrush")
                { Source = UiThemeState.Instance });
            child.SetBinding(System.Windows.Controls.Button.ForegroundProperty,
                new System.Windows.Data.Binding(active ? "SelectionForegroundBrush" : "TextSecondaryBrush")
                { Source = UiThemeState.Instance });
        }
    }

    /// <summary>The status page only exists in classic mode. In the redesigned
    /// modes its three cards live by purpose: 本地文件检查 on the folder page,
    /// 后台任务 and 运行消息 on the scrape tasks page.</summary>
    void RelocateStatusCards()
    {
        DetachFromParent(MissingFilesPanel);
        DetachFromParent(BackgroundTasksCard);
        DetachFromParent(RunLogCard);
        if (UiTheme.IsClassicMode)
        {
            StatusPanel.Children.Add(MissingFilesPanel);
            StatusPanel.Children.Add(BackgroundTasksCard);
            StatusPanel.Children.Add(RunLogCard);
            return;
        }
        scrapeTasksGroup?.Children.Add(BackgroundTasksCard);
        scrapeTasksGroup?.Children.Add(RunLogCard);
        // The folder page rebuilds itself on every render, so the missing-files
        // card is (re)attached at the end of BuildFolderWorkspace. If the page
        // has not rendered yet, park the card invisibly on the status panel.
        if (FoldersPanel.Children.Count > 0) FoldersPanel.Children.Add(MissingFilesPanel);
        else StatusPanel.Children.Add(MissingFilesPanel);
    }

    void AttachStatusCardsToFolderPage()
    {
        if (UiTheme.IsClassicMode) return;
        if (ReferenceEquals(MissingFilesPanel.Parent, FoldersPanel)) return;
        DetachFromParent(MissingFilesPanel);
        FoldersPanel.Children.Add(MissingFilesPanel);
    }

    static void DetachFromParent(FrameworkElement element)
    {
        if (element.Parent is Panel panel) panel.Children.Remove(element);
    }

    /// <summary>The redesigned modes embed the shared role profile view in the
    /// main window; classic mode keeps the standalone window. Created lazily
    /// so the async store load only runs when the page is first opened.</summary>
    void EnsureEmbeddedRoles()
    {
        if (embeddedRoles is not null) return;
        embeddedRoles = new RoleProfilesView(DataDir, videos, null, null, config.TmdbToken, id =>
        {
            var found = videos.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (found is not null) ShowMetadataDetailsForVideo(found);
        }, savedMetadata: metadata);
        RolesHost.Children.Add(embeddedRoles);
    }

    /// <summary>Called from the Closing handler: block closing while the
    /// embedded role page is saving or scraping (same contract as the classic
    /// role window).</summary>
    bool TryConfirmEmbeddedRolesClose()
    {
        if (embeddedRoles is null || !embeddedRoles.IsBusy) return true;
        embeddedRoles.RequestAutoStop();
        Log("人物档案正在保存或搜刮，已请求停止；完成后即可关闭窗口");
        return false;
    }
}

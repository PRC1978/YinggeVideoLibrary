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
    StackPanel? librarySegmentButtons;
    RoleProfilesView? embeddedRoles;
    readonly List<TextBlock> modernSidebarHeadings = [];
    bool? arrangedClassicMode;
    bool? raisedSidebarBorder;

    void EnsureSidebarGroups()
    {
        if (modernSidebarHeadings.Count != 0 || SidebarScroll.Content is not StackPanel items) return;
        foreach (var (before, text) in new[]
        {
            (LibraryNav, "浏览"), (CategoriesNav, "整理"), (OnlineNav, "扩展")
        })
        {
            var heading = new TextBlock
            {
                Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(19, 9, 0, 3),
                IsHitTestVisible = false, Visibility = Visibility.Collapsed
            };
            heading.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("TextTertiaryBrush")
                { Source = UiThemeState.Instance });
            items.Children.Insert(items.Children.IndexOf(before), heading);
            modernSidebarHeadings.Add(heading);
        }
    }

    void ApplySidebarPresentation()
    {
        EnsureSidebarGroups();
        var collapsed = SidebarColumn.Width.Value <= 28;
        var wide = !UiTheme.IsClassicMode && !collapsed && ActualWidth >= 1100;
        var floatingGlass = !UiTheme.IsClassicMode && !collapsed && UiThemeState.Instance.IsGlass;
        Sidebar.Margin = floatingGlass ? new Thickness(8, 8, 0, 8) : new Thickness(0);
        Sidebar.CornerRadius = floatingGlass ? new CornerRadius(16) : new CornerRadius(0);
        Sidebar.BorderThickness = floatingGlass ? new Thickness(1) : new Thickness(0, 0, 1, 0);
        var materialEdge = floatingGlass || !UiTheme.IsClassicMode && UiThemeState.Instance.IsNeumorphism;
        if (raisedSidebarBorder != materialEdge)
        {
            Sidebar.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(materialEdge
                ? nameof(UiThemeState.PanelEdgeBrush) : nameof(UiThemeState.BorderBrushToken))
                { Source = UiThemeState.Instance });
            raisedSidebarBorder = materialEdge;
        }
        if (!collapsed)
            SidebarColumn.Width = new GridLength(UiTheme.IsClassicMode ? UiTheme.Num(UiTheme.SidebarWidth) : wide ? 200 : 72);
        foreach (var heading in modernSidebarHeadings)
            heading.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { LibraryNav, AllNav, RecentNav, FavoriteNav, RecommendationsNav,
            CategoriesNav, FoldersNav, StatusNav, RolesNav, ScrapeNav, ReviewNav, OnlineNav, PluginsNav, AccountNav, SettingsNav })
        {
            button.Width = wide ? 188 : UiTheme.IsClassicMode ? 60 : 60;
            button.Height = wide ? 43 : UiTheme.IsClassicMode ? 50 : 48;
            button.Margin = ReferenceEquals(button, SettingsNav) ? new Thickness(0, 0, 0, 15)
                : wide ? new Thickness(0, 0, 0, 2) : new Thickness(0, 0, 0, 2);
            button.HorizontalContentAlignment = wide ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Center;
            button.Padding = wide ? new Thickness(10, 0, 0, 0) : new Thickness(0);
            if (button.Content is not StackPanel content || content.Children.Count < 2) continue;
            content.Orientation = wide ? System.Windows.Controls.Orientation.Horizontal : System.Windows.Controls.Orientation.Vertical;
            content.HorizontalAlignment = wide ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Center;
            if (content.Children[0] is FrameworkElement icon)
            {
                icon.Width = 21;
                icon.Margin = wide ? new Thickness(0, 0, 14, 0) : new Thickness(0, 1, 0, 1);
            }
            if (content.Children[1] is TextBlock label)
            {
                label.FontSize = wide ? 13 : 11;
                label.VerticalAlignment = VerticalAlignment.Center;
                label.TextAlignment = wide ? TextAlignment.Left : TextAlignment.Center;
            }
        }
    }

    void UpdateHeaderResponsive()
    {
        ApplySidebarPresentation();
        // Short windows need to show the first review or video row without
        // spending most of the viewport on empty page chrome.
        var shortWindow = !UiTheme.IsClassicMode && ActualHeight > 0 && ActualHeight < 650;
        var dense = UiTheme.Current == UiTheme.Compact;
        BrandEyebrow.Visibility = UiTheme.IsClassicMode && !dense ? Visibility.Visible : Visibility.Collapsed;
        PageSubtitle.Visibility = dense ? Visibility.Collapsed : Visibility.Visible;
        HeaderContent.Margin = UiTheme.IsClassicMode && !dense ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        HeaderToggle.Visibility = UiTheme.IsClassicMode ? Visibility.Visible : Visibility.Collapsed;

        var onlineLeft = SidebarColumn.Width.Value <= 28 ? 48 : 28;
        HeaderArea.Margin = view == "online" ? new Thickness(onlineLeft, 18, 28, 12)
            : UiTheme.IsClassicMode ? new Thickness(48, 42, 48, 25)
            : UiTheme.Current == UiTheme.Cinema ? new Thickness(40, 14, 40, 16)
            : dense ? new Thickness(40, 12, 40, 12)
            : shortWindow ? new Thickness(40, 14, 40, 12)
            : new Thickness(40, 24, 40, 20);
        UpdateSettingsNavigation();
        ReviewPanel.Padding = shortWindow ? new Thickness(18) : new Thickness(24);
        var headerWidth = HeaderContent.ActualWidth;
        var compact = headerWidth > 0 ? headerWidth < 900 : ActualWidth < 1040;
        Grid.SetColumnSpan(HeaderTitleStack, compact ? 2 : 1);
        Grid.SetRow(Toolbar, compact ? 1 : 0);
        Grid.SetColumn(Toolbar, compact ? 0 : 1);
        Grid.SetColumnSpan(Toolbar, compact ? 2 : 1);
        Toolbar.HorizontalAlignment = compact ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        Toolbar.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(15, 0, 0, 0);
    }

    /// <summary>Apply the current interface mode's chrome. Called once during
    /// startup (after the scrape groups exist) and on every mode switch.</summary>
    void ApplyInterfaceMode(bool refreshView = true)
    {
        UpdateHeaderResponsive();
        Cards.UpdateCellWidth();
        var classic = UiTheme.IsClassicMode;
        var navigationStyle = (Style)FindResource(classic ? "Nav" : "NavModern");
        foreach (var item in new[] { LibraryNav, AllNav, RecentNav, FavoriteNav, RecommendationsNav, CategoriesNav, FoldersNav, StatusNav, RolesNav, ScrapeNav, ReviewNav, OnlineNav, PluginsNav, SettingsNav })
            if (!ReferenceEquals(item.Style, navigationStyle)) item.Style = navigationStyle;
        var pagerStyle = (Style)FindResource(classic ? "Action" : "PagerModern");
        foreach (var item in new[] { PreviousPage, NextPage, PageJumpButton })
            if (!ReferenceEquals(item.Style, pagerStyle)) item.Style = pagerStyle;
        foreach (var item in new[] { reviewPreviousPageButton, reviewNextPageButton, reviewTopPreviousPageButton, reviewTopNextPageButton })
            if (item is not null && !ReferenceEquals(item.Style, pagerStyle)) item.Style = pagerStyle;
        Pager.HorizontalAlignment = classic ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Right;
        Pager.Margin = classic ? new Thickness(0, 8, 0, 20) : new Thickness(0, 12, 40, 20);
        LibraryNav.Visibility = classic ? Visibility.Collapsed : Visibility.Visible;
        AllNav.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        RecentNav.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        FavoriteNav.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        StatusNav.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        ReviewNav.Visibility = classic ? Visibility.Collapsed : Visibility.Visible;
        PluginsNav.Visibility = classic ? Visibility.Collapsed : Visibility.Visible;
        // These operations detach and rebuild whole pages. Their structure
        // changes only at the classic/new interface boundary, not when the
        // user switches between two modern palettes.
        if (arrangedClassicMode != classic)
        {
            ArrangeScrapeGroups();
            RelocateStatusCards();
            ArrangeReviewSections();
            arrangedClassicMode = classic;
        }
        if (ReviewQueuePanel.Parent is ScrollViewer reviewScroll)
            ConfigureReviewQueueScrolling(reviewScroll);
        InvalidateAvailabilityChipTheme();
        // The sphere graph caches a rendered bitmap in the palette active at
        // construction time. Rebuild it after a theme switch so links and
        // labels never retain the previous mode's colors.
        lastCategoryRenderVersion = -1;
        // Views that only exist in one mode fall back to a page that does.
        if (classic && view is "review") view = "scrape";
        else if (classic && view is "roles") view = "all";
        else if (!classic && view is "status") view = "all";
        else if (UiTheme.Current == UiTheme.Atlas && view == "all") view = "categories";
        if (refreshView) ShowView(view);
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
        librarySegmentButtons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var segmentFrame = new Border
        {
            Child = librarySegmentButtons,
            CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1),
            Padding = new Thickness(2), Margin = new Thickness(0, 0, 4, 0)
        };
        segmentFrame.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding("SurfaceBrush") { Source = UiThemeState.Instance });
        segmentFrame.SetBinding(Border.BorderBrushProperty,
            new System.Windows.Data.Binding("BorderBrushToken") { Source = UiThemeState.Instance });
        librarySegments.Children.Add(segmentFrame);
        foreach (var (id, label) in new[] { ("all", "全部"), ("recent", "最近"), ("favorites", "收藏") })
        {
            var button = new System.Windows.Controls.Button
            {
                Content = label, Style = (Style)FindResource("Action"), Tag = id,
                MinWidth = 72, Margin = new Thickness(0), BorderThickness = new Thickness(0)
            };
            button.Click += (_, _) => ShowView(id);
            librarySegmentButtons.Children.Add(button);
        }
        var selectFiltered = new System.Windows.Controls.Button
        {
            Content = "全选当前结果", Style = (Style)FindResource("Action"),
            Tag = "select", Margin = new Thickness(15, 0, 0, 0),
            ToolTip = "选择当前筛选条件匹配的全部视频，包括未显示的页面"
        };
        selectFiltered.Click += SelectFilteredVideos;
        librarySegments.Children.Add(selectFiltered);
        var selectLibrary = new System.Windows.Controls.Button
        {
            Content = "全选全库", Style = (Style)FindResource("Action"), Tag = "select-library",
            ToolTip = "选择整个视频库，包括其他分类与搜索条件以外的视频"
        };
        selectLibrary.Click += SelectAllLibraryVideos;
        librarySegments.Children.Add(selectLibrary);
        var browseTags = new System.Windows.Controls.Button
        {
            Content = "浏览标签", Style = (Style)FindResource("Action"), Tag = "tags",
            ToolTip = "浏览全库标签并按标签筛选"
        };
        browseTags.Click += BrowseAllTags;
        librarySegments.Children.Add(browseTags);
        // Insert right under the section header of the library page.
        if (Cards.Parent is Panel host) host.Children.Insert(1, librarySegments);
    }

    void UpdateLibrarySegments()
    {
        if (librarySegments is null) return;
        librarySegments.Margin = UiTheme.Current == UiTheme.Cinema
            ? new Thickness(0, 0, 0, 4) : new Thickness(0, 0, 0, 12);
        librarySegments.Visibility = !UiTheme.IsClassicMode && view is "all" or "recent" or "favorites"
            ? Visibility.Visible : Visibility.Collapsed;
        foreach (var child in librarySegmentButtons!.Children.OfType<System.Windows.Controls.Button>())
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
        }, savedMetadata: metadata, postgresSession: postgresSession);
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

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using ListBox = System.Windows.Controls.ListBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Image = System.Windows.Controls.Image;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using TextBox = System.Windows.Controls.TextBox;

namespace YinggeVideoLibrary;

internal sealed class RoleProfilesWindow : Window
{
    readonly string dataDir;
    readonly IReadOnlyList<VideoItem> videos;
    Dictionary<string, VideoItem> videosById = new(StringComparer.OrdinalIgnoreCase);
    readonly IReadOnlyDictionary<string, MovieMetadata> savedMetadata;
    readonly MovieMetadata? origin;
    readonly VideoItem? originVideo;
    readonly Action<string> openVideoDetails;
    readonly string tmdbToken;
    readonly List<VerifiedPersonOption> verifiedOptions = [];
    RoleProfileStore.State state;
    readonly ListBox profiles = new();
    readonly TextBox search = new();
    readonly StackPanel content = new();
    readonly Button manualAdd = UiButton("＋ 新建手工档案", true);
    readonly Button autoStart = UiButton("自动发现并搜刮人物");
    Button? autoEmptyButton;
    readonly TextBlock autoStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    readonly Button autoCancel = UiButton("取消搜刮");
    CancellationTokenSource? autoCancellation;
    bool autoRunning;
    readonly System.Windows.Threading.DispatcherTimer profileSearchTimer = new()
    { Interval = TimeSpan.FromMilliseconds(170) };
    string? selectedProfileId;
    int workPage;
    int listRevision;
    bool saving;
    const int WorksPerPage = 40;
    internal int VerifiedOptionCountForDiagnostics => verifiedOptions.Count;
    internal int DisplayedWorkCountForDiagnostics =>
        SelectedProfile() is { } selected ? Math.Min(WorksPerPage,
            Math.Max(0, selected.Works.Count - workPage * WorksPerPage)) : 0;
    internal void SelectProfileForDiagnostics(string id) { selectedProfileId = id; RefreshList(); }

    sealed record VerifiedPersonOption(string Name, string Provider, string PersonId, string PersonUrl,
        string OriginWorkUrl, string CharacterName)
    {
        public string Display => Provider + " · " + Name + (CharacterName.Length == 0 ? "" : " 饰 " + CharacterName);
    }

    internal RoleProfilesWindow(string dataDir, IReadOnlyList<VideoItem> videos,
        MovieMetadata? origin, VideoItem? originVideo, string tmdbToken,
        Action<string> openVideoDetails, bool preloadForDiagnostics = false,
        IReadOnlyDictionary<string, MovieMetadata>? savedMetadata = null)
    {
        this.dataDir = dataDir;
        // Copy references only; the potentially expensive ID map is built
        // together with the profile snapshot after the window becomes visible.
        this.videos = videos.ToArray();
        this.origin = origin;
        this.originVideo = originVideo;
        this.savedMetadata = savedMetadata ?? new Dictionary<string, MovieMetadata>(StringComparer.OrdinalIgnoreCase);
        this.tmdbToken = tmdbToken;
        this.openVideoDetails = openVideoDetails;
        if (preloadForDiagnostics)
        {
            if (this.videos.Count > 256)
                throw new InvalidOperationException("离屏诊断只能同步加载少量临时视频。");
            videosById = BuildVideoMap(this.videos);
            state = RoleProfileStore.Load(dataDir);
        }
        else state = new RoleProfileStore.State();
        Title = "人物与角色档案";
        Width = 1110; Height = 750; MinWidth = 760; MinHeight = 520;
        Closing += (_, args) =>
        {
            if (saving || autoRunning) args.Cancel = true;
            if (autoRunning)
            {
                autoCancellation?.Cancel();
                autoStatus.Text = "正在停止搜刮…";
            }
        };
        Closed += (_, _) => { profileSearchTimer.Stop(); autoCancellation?.Dispose(); };
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(247, 249, 253));
        BuildVerifiedOptions();

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock { Text = "人物与角色档案", FontSize = 22,
            FontWeight = FontWeights.SemiBold, Foreground = Ink() });
        heading.Children.Add(new TextBlock { Text = "手工资料与已核实来源分别显示。姓名相同不会自动合并，作品须明确关联。",
            Foreground = Muted(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
        root.Children.Add(heading);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(285) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(columns, 1); root.Children.Add(columns);

        var left = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14),
            BorderBrush = Line(), BorderThickness = new Thickness(1), Padding = new Thickness(12) };
        Grid.SetColumn(left, 0); columns.Children.Add(left);
        var leftBody = new DockPanel(); left.Child = leftBody;
        var actions = new StackPanel(); DockPanel.SetDock(actions, Dock.Top); leftBody.Children.Add(actions);
        search.Height = 36; search.Padding = new Thickness(9, 5, 9, 5);
        search.ToolTip = "搜索档案姓名、别名或已核实的来源人物 ID";
        profileSearchTimer.Tick += (_, _) => { profileSearchTimer.Stop(); RefreshList(); };
        search.TextChanged += (_, _) =>
        {
            listRevision++;
            profileSearchTimer.Stop(); profileSearchTimer.Start();
        };
        actions.Children.Add(search);
        manualAdd.Margin = new Thickness(0, 8, 0, 5);
        manualAdd.Click += (_, _) => EditProfile(null);
        actions.Children.Add(manualAdd);
        autoStart.Margin = new Thickness(0, 0, 0, 4);
        autoStart.Click += async (_, _) => await StartAutoDiscoveryAsync();
        actions.Children.Add(autoStart);
        autoStatus.Foreground = Muted();
        autoStatus.Text = "从已核实的作品来源识别人物，不会按同名自动合并。";
        actions.Children.Add(autoStatus);
        autoCancel.Visibility = Visibility.Collapsed;
        autoCancel.Click += (_, _) =>
        {
            autoCancellation?.Cancel();
            autoStatus.Text = "正在停止搜刮…";
        };
        actions.Children.Add(autoCancel);
        if (verifiedOptions.Count > 0)
        {
            actions.Children.Add(new TextBlock { Text = "从当前作品已核实的演员 / 角色建立",
                Foreground = Muted(), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 5) });
            var pick = new ComboBox { Height = 35, DisplayMemberPath = nameof(VerifiedPersonOption.Display),
                ItemsSource = verifiedOptions };
            actions.Children.Add(pick);
            var link = UiButton("创建或打开来源人物");
            link.Margin = new Thickness(0, 6, 0, 11);
            link.Click += async (_, _) =>
            {
                if (pick.SelectedItem is not VerifiedPersonOption option) return;
                link.IsEnabled = false;
                try { await CreateOrOpenVerified(option); }
                catch (Exception ex) { MessageBox.Show(this, "本地作品无法核对：" + ex.Message, "角色档案"); }
                finally { link.IsEnabled = true; }
            };
            actions.Children.Add(link);
        }
        else if (origin is not null && origin.Actors is { Count: > 0 })
            actions.Children.Add(new TextBlock
            {
                Text = "这部作品只有演员姓名，尚无核实的人物 ID。可新建手工档案，再明确关联本地视频。",
                TextWrapping = TextWrapping.Wrap, Foreground = Muted(), FontSize = 12,
                Margin = new Thickness(0, 8, 0, 11)
            });
        profiles.DisplayMemberPath = nameof(RoleProfileStore.Profile.Name);
        profiles.BorderThickness = new Thickness(0);
        VirtualizingPanel.SetIsVirtualizing(profiles, true);
        VirtualizingPanel.SetVirtualizationMode(profiles, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(profiles, true);
        profiles.SelectionChanged += (_, _) =>
        {
            if (profiles.SelectedItem is RoleProfileStore.Profile selected)
            {
                if (selectedProfileId != selected.Id) workPage = 0;
                selectedProfileId = selected.Id;
            }
            RenderProfile();
        };
        leftBody.Children.Add(profiles);

        var right = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14),
            BorderBrush = Line(), BorderThickness = new Thickness(1), Padding = new Thickness(18) };
        Grid.SetColumn(right, 2); columns.Children.Add(right);
        var scroller = new ScrollViewer { Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        right.Child = scroller;
        Content = root;
        if (preloadForDiagnostics) RefreshList();
        else
        {
            root.IsEnabled = false;
            content.Children.Add(new TextBlock { Text = "正在读取角色档案…", Foreground = Muted() });
            Loaded += async (_, _) =>
            {
                try
                {
                    var ready = await Task.Run(() =>
                        (Profiles: RoleProfileStore.Load(dataDir), VideoMap: BuildVideoMap(this.videos)));
                    state = ready.Profiles;
                    videosById = ready.VideoMap;
                    root.IsEnabled = true;
                    RefreshList();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "角色档案无法读取：" + ex.Message, "人物与角色档案",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    Close();
                }
            };
        }
    }

    static Dictionary<string, VideoItem> BuildVideoMap(IReadOnlyList<VideoItem> items)
    {
        var map = new Dictionary<string, VideoItem>(items.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var video in items) map.TryAdd(video.Id, video);
        return map;
    }

    static Brush Ink() => new SolidColorBrush(Color.FromRgb(35, 49, 74));
    static Brush Muted() => new SolidColorBrush(Color.FromRgb(112, 128, 151));
    static Brush Line() => new SolidColorBrush(Color.FromRgb(218, 227, 243));
    static Button UiButton(string text, bool primary = false) => new()
    {
        Content = text, MinHeight = 35, Padding = new Thickness(11, 6, 11, 6),
        Margin = new Thickness(0, 0, 7, 6), Background = primary
            ? new SolidColorBrush(Color.FromRgb(78, 107, 225)) : Brushes.White,
        Foreground = primary ? Brushes.White : Ink(), BorderBrush = Line(), BorderThickness = new Thickness(1)
    };

    void BuildVerifiedOptions()
    {
        if (origin is null || !origin.DetailFetched || !origin.DetailIdentityVerified) return;
        if (origin.Source.Equals("JavDB", StringComparison.OrdinalIgnoreCase))
            foreach (var actor in (origin.CatalogLinks ?? []).Where(link => link is not null && link.Kind == "演员"))
                if (RoleProfileStore.TryVerifiedJavDbActor(origin, actor, out var identity))
                    verifiedOptions.Add(new VerifiedPersonOption(identity.NameAtSource,
                        identity.Provider, identity.PersonId, identity.PersonUrl,
                        identity.VerifiedFromWorkUrl, ""));
        foreach (var credit in origin.PersonCredits ?? [])
            if (credit is not null &&
                (credit.Provider != "TMDB" || CurrentTmdbWorkIsAllowed()) &&
                RoleProfileStore.TryVerifiedPersonCredit(origin, credit, out var identity))
                verifiedOptions.Add(new VerifiedPersonOption(identity.NameAtSource,
                    identity.Provider, identity.PersonId, identity.PersonUrl,
                    identity.VerifiedFromWorkUrl, credit.CharacterName));
        var distinct = verifiedOptions.DistinctBy(option => option.Provider + "\n" + option.PersonId,
            StringComparer.OrdinalIgnoreCase).ToList();
        verifiedOptions.Clear(); verifiedOptions.AddRange(distinct);
    }

    bool CurrentTmdbWorkIsAllowed() => originVideo is not null && origin is not null &&
        TmdbUsagePolicy.Evaluate(originVideo, origin, origin.Title).Allowed;

    // A previously saved or imported credit is not sufficient once its local
    // video changes category, title, tags or provenance. Recheck the current
    // video and its verified work record before showing TMDB person material.
    internal static bool TmdbWorkIsAllowed(RoleProfileStore.WorkLink work,
        IReadOnlyDictionary<string, VideoItem> videosById,
        IReadOnlyDictionary<string, MovieMetadata> savedMetadata,
        MovieMetadata? origin = null, VideoItem? originVideo = null)
    {
        if (work.SourceProvider != "TMDB" || work.LinkOrigin != "verified-source" ||
            !videosById.TryGetValue(work.LocalVideoId, out var video)) return false;
        MovieMetadata? record = null;
        if (savedMetadata.TryGetValue(work.LocalVideoId, out var saved)) record = saved;
        else if (originVideo is not null && originVideo.Id.Equals(work.LocalVideoId,
                     StringComparison.OrdinalIgnoreCase)) record = origin;
        if (record is null || record.Source is not ("TMDB 电影" or "TMDB 剧集") ||
            record.SourceUrl != work.EvidenceUrl ||
            !record.DetailFetched || !record.DetailIdentityVerified || record.IsAdultContent ||
            !(record.PersonCredits?.Any(credit => credit is not null && credit.Provider == "TMDB" &&
                credit.PersonId == work.SourcePersonId &&
                credit.VerifiedFromWorkUrl == work.EvidenceUrl) ?? false)) return false;
        return TmdbUsagePolicy.Evaluate(video, record, record.Title).Allowed;
    }

    bool TmdbProfileIsAllowed(RoleProfileStore.Profile profile) =>
        profile.Works.Count > 0 && profile.Works.All(work => TmdbWorkIsAllowed(work,
            videosById, savedMetadata, origin, originVideo));

    void RefreshList()
    {
        var query = search.Text.Trim();
        var snapshot = state.Profiles.ToArray();
        var revision = ++listRevision;
        if (snapshot.Length >= 500)
        {
            _ = RefreshListInBackground(snapshot, query, revision);
            return;
        }
        ApplyProfileList(FilterProfiles(snapshot, query), revision);
    }

    async Task RefreshListInBackground(RoleProfileStore.Profile[] snapshot, string query, int revision)
    {
        try
        {
            var shown = await Task.Run(() => FilterProfiles(snapshot, query));
            ApplyProfileList(shown, revision);
        }
        catch (Exception ex)
        {
            if (revision == listRevision)
                MessageBox.Show(this, "档案搜索失败：" + ex.Message, "角色档案");
        }
    }

    static List<RoleProfileStore.Profile> FilterProfiles(RoleProfileStore.Profile[] snapshot, string query) =>
        snapshot.Where(p => query.Length == 0 || p.Name.Contains(query,
                StringComparison.CurrentCultureIgnoreCase) ||
            p.Aliases.Any(alias => alias.Contains(query, StringComparison.CurrentCultureIgnoreCase)) ||
            p.Sources.Any(source => source.PersonId.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    void ApplyProfileList(List<RoleProfileStore.Profile> shown, int revision)
    {
        if (revision != listRevision) return;
        profiles.ItemsSource = shown;
        var selected = shown.FirstOrDefault(p => p.Id == selectedProfileId) ?? shown.FirstOrDefault();
        profiles.SelectedItem = selected;
        selectedProfileId = selected?.Id;
        RenderProfile();
    }

    RoleProfileStore.Profile? SelectedProfile() =>
        state.Profiles.FirstOrDefault(p => p.Id == selectedProfileId);

    async Task StartAutoDiscoveryAsync()
    {
        if (autoRunning || saving) return;
        autoRunning = true;
        autoCancellation = new CancellationTokenSource();
        var token = autoCancellation.Token;
        autoStart.IsEnabled = false;
        if (autoEmptyButton is not null) autoEmptyButton.IsEnabled = false;
        manualAdd.IsEnabled = false;
        search.IsEnabled = false;
        profiles.IsEnabled = false;
        content.IsEnabled = false;
        autoCancel.Visibility = Visibility.Visible;
        autoStatus.Text = "正在检查已核实的作品资料…";
        var startedImport = false;
        try
        {
            // Read the committed metadata file on the worker. The main window's
            // live dictionary can change as the scraping queue completes.
            var nameFilter = search.Text.Trim();
            var preview = await Task.Run(() => RoleProfileAutoScraper.Preview(
                videos, MetadataStore.Load(dataDir), state, nameFilter, token), token);
            token.ThrowIfCancellationRequested();
            if (preview.CapacityExceeded)
            {
                autoStatus.Text = preview.WorkCapacityExceededCount > 0
                    ? $"有 {preview.WorkCapacityExceededCount:N0} 条作品关系会超过单个人物 20,000 部上限。请在左侧搜索框缩小导入范围后重试。"
                    : "候选人物超过 10,000 份档案上限。请在左侧搜索框输入姓名或来源 ID 后重试。";
                return;
            }
            if (preview.Candidates.Count == 0)
            {
                autoStatus.Text = "没有可自动建立的人物：请先为作品搜刮并审核完整资料，或手工建立档案。";
                return;
            }
            autoStatus.Text = $"找到 {preview.Candidates.Count:N0} 位有来源 ID 的人物，等待确认。";
            if (!ShowAutoPreview(preview))
            {
                autoStatus.Text = "已取消，人物档案未更改。";
                return;
            }
            startedImport = true;
            var progress = new Progress<RoleProfileAutoScraper.Progress>(item =>
            {
                if (!autoRunning) return;
                autoStatus.Text = $"正在搜刮 {item.Processed:N0}/{item.Total:N0} · 新建 {item.Created:N0} · 更新 {item.Updated:N0} · 详情 {item.Fetched:N0} · 失败 {item.Failed:N0}"
                    + (item.CurrentName.Length == 0 ? "" : "\n当前：" + item.CurrentName);
            });
            var result = await Task.Run(() => RoleProfileAutoScraper.RunAsync(dataDir,
                preview, tmdbToken, progress, token), token);
            autoStatus.Text = $"完成：新建 {result.Created:N0}、更新 {result.Updated:N0}，读取详情 {result.Fetched:N0}；失败 {result.Failed:N0}。"
                + (result.SkippedCredentials > 0 ? $" {result.SkippedCredentials:N0} 位缺少来源凭据，仍保留来源身份。" : "")
                + (result.SkippedChanged > 0 ? $" {result.SkippedChanged:N0} 位因作品资料变更而跳过，请重新预览。" : "")
                + (result.SkippedRateLimited > 0 ? $" {result.SkippedRateLimited:N0} 位详情因来源限速暂未读取，稍后可重试。" : "");
            if (result.Failures.Count > 0)
                MessageBox.Show(this, "部分人物详情暂未读取，档案中已保存可核实的来源身份：\n"
                    + string.Join("\n", result.Failures.Take(8)), "自动搜刮人物",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            autoStatus.Text = startedImport
                ? "已停止搜刮；已完成的来源档案会保留。"
                : "已取消，人物档案未更改。";
        }
        catch (Exception ex)
        {
            autoStatus.Text = "自动搜刮未完成。";
            MessageBox.Show(this, "自动搜刮人物失败：" + ex.Message,
                "人物与角色档案", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (startedImport)
            {
                try
                {
                    state = await Task.Run(() => RoleProfileStore.Load(dataDir));
                    RefreshList();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "人物档案已写入，但重新读取失败：" + ex.Message,
                        "人物与角色档案", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            autoRunning = false;
            autoCancellation.Dispose();
            autoCancellation = null;
            autoCancel.Visibility = Visibility.Collapsed;
            autoStart.IsEnabled = true;
            if (autoEmptyButton is not null) autoEmptyButton.IsEnabled = true;
            manualAdd.IsEnabled = true;
            search.IsEnabled = true;
            profiles.IsEnabled = true;
            content.IsEnabled = true;
        }
    }

    bool ShowAutoPreview(RoleProfileAutoScraper.PreviewResult preview)
    {
        var dialog = new Window
        {
            Title = "预览自动搜刮人物", Owner = this, Width = 680, Height = 540,
            MinWidth = 520, MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 253))
        };
        var layout = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancel = UiButton("返回");
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var start = UiButton("开始搜刮", true);
        start.Click += (_, _) => dialog.DialogResult = true;
        footer.Children.Add(cancel); footer.Children.Add(start);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock { Text = "将从已审核的作品发现人物",
            FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Ink() });
        heading.Children.Add(new TextBlock
        {
            Text = $"候选 {preview.Candidates.Count:N0} 位 · 新建 {preview.NewCount:N0} 位 · 已有档案 {preview.ExistingCount:N0} 位 · 作品关系 {preview.WorkCount:N0} 条",
            Foreground = Muted(), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 4)
        });
        var providers = preview.Candidates.GroupBy(candidate => candidate.Provider)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key + " " + group.Count().ToString("N0"));
        heading.Children.Add(new TextBlock { Text = "来源：" + string.Join(" · ", providers),
            Foreground = Muted(), TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(new TextBlock
        {
            Text = $"未核实来源的人名不会自动合并。跳过 {preview.SkippedUnverifiedCount:N0} 条未核实记录；已填写的手工资料不会被覆盖。确认后会访问 TMDB 或 TVmaze 读取人物详情；JavDB 目前只建立来源身份与作品关系。请求逐个进行，可随时取消；大量肖像只保存来源链接，按需读取图片。",
            Foreground = Muted(), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 0)
        });
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var items = preview.Candidates.Take(150).Select(candidate =>
            $"{candidate.Provider} · {candidate.Name} · {candidate.WorkCount:N0} 部作品"
            + (candidate.CanFetchDetails ? " · 可读取详情" : " · 仅来源身份")).ToArray();
        var list = new ListBox { ItemsSource = items, Background = Brushes.White,
            BorderBrush = Line(), BorderThickness = new Thickness(1) };
        if (preview.Candidates.Count > items.Length)
            heading.Children.Add(new TextBlock { Text = $"以下展示前 {items.Length} 位；确认后处理全部候选。",
                Foreground = Muted(), Margin = new Thickness(0, 5, 0, 0) });
        layout.Children.Add(list);
        dialog.Content = layout;
        return dialog.ShowDialog() == true;
    }

    void RenderProfile()
    {
        content.Children.Clear();
        autoEmptyButton = null;
        var profile = SelectedProfile();
        if (profile is null)
        {
            content.Children.Add(new TextBlock { Text = state.Profiles.Count == 0
                    ? "还没有人物档案" : "没有匹配的人物档案",
                Foreground = Ink(), FontSize = 19, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8) });
            content.Children.Add(new TextBlock
            {
                Text = "可以从视频已核实的作品资料中发现人物并读取简介、肖像等来源信息；只有演员姓名而没有来源 ID 的作品仍需手工核对。",
                Foreground = Muted(), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            autoEmptyButton = UiButton("自动发现并搜刮人物", true);
            autoEmptyButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            autoEmptyButton.IsEnabled = !autoRunning;
            autoEmptyButton.Click += async (_, _) => await StartAutoDiscoveryAsync();
            content.Children.Add(autoEmptyButton);
            return;
        }
        var tmdbProfile = profile.Sources.Any(source => source.Provider == "TMDB");
        var tmdbAllowed = !tmdbProfile || TmdbProfileIsAllowed(profile);
        var top = new Grid { Margin = new Thickness(0, 0, 0, 15) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var portrait = new Border { Width = 158, Height = 190, Background = new SolidColorBrush(Color.FromRgb(236, 241, 250)),
            CornerRadius = new CornerRadius(10), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        try
        {
            var sourcedPortrait = profile.SourcedDetails.FirstOrDefault(item =>
                item.PortraitBase64.Length > 0 && (item.Provider != "TMDB" || tmdbAllowed));
            var imageData = profile.PortraitBase64.Length > 0 ? profile.PortraitBase64
                : sourcedPortrait?.PortraitBase64 ?? "";
            if (imageData.Length > 0)
            {
                var bytes = Convert.FromBase64String(imageData);
                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage(); bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 250;
                bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                portrait.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
            }
            else portrait.Child = new TextBlock { Text = "暂无肖像", Foreground = Muted(),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        catch { portrait.Child = new TextBlock { Text = "肖像无法显示", Foreground = Muted(),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; }
        top.Children.Add(portrait);
        var title = new StackPanel(); Grid.SetColumn(title, 1); top.Children.Add(title);
        title.Children.Add(new TextBlock { Text = profile.Name, FontSize = 25,
            FontWeight = FontWeights.SemiBold, Foreground = Ink(), TextWrapping = TextWrapping.Wrap });
        if (profile.Aliases.Count > 0) title.Children.Add(new TextBlock { Text = "别名：" + string.Join("、", profile.Aliases),
            Foreground = Muted(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
        title.Children.Add(new TextBlock { Text = "手工档案 · " + profile.Sources.Count + " 个已核实来源 · " + profile.Works.Count + " 个明确关联作品",
            Foreground = new SolidColorBrush(Color.FromRgb(78, 107, 187)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 12) });
        var edit = UiButton("✎ 编辑档案"); edit.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        edit.Click += (_, _) => EditProfile(profile);
        title.Children.Add(edit);
        if (profile.PortraitBase64.Length == 0 && profile.SourcedDetails.Any(item =>
                item.PortraitBase64.Length > 0 && (item.Provider != "TMDB" || tmdbAllowed)))
            title.Children.Add(new TextBlock { Text = "当前肖像来自已核实的人物来源；手工肖像优先显示。",
                Foreground = Muted(), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(top);
        Section("手工资料");
        Field("身份 / 关系", profile.Role);
        Field("生日", profile.Birthday);
        Field("性别", profile.Gender);
        Field("身高", profile.Height);
        Field("体重", profile.Weight);
        Field("三围", profile.Measurements);
        Field("血型", profile.BloodType);
        Field("生平 / 简介", profile.Biography);
        if (new[] { profile.Role, profile.Birthday, profile.Gender, profile.Height, profile.Weight,
                profile.Measurements, profile.BloodType, profile.Biography }.All(string.IsNullOrWhiteSpace))
            content.Children.Add(new TextBlock { Text = "尚未填写；这些字段都是可选项。",
                Foreground = Muted(), Margin = new Thickness(0, 0, 0, 14) });

        Section("已核实的来源身份");
        if (profile.Sources.Count == 0) Hint("当前没有来源人物 ID；不会根据同名自动合并。 ");
        foreach (var source in profile.Sources)
        {
            if (source.Provider == "TMDB" && !tmdbAllowed)
            {
                Hint("TMDB 来源资料已隐藏：关联的本地作品缺失、来源未核实，或当前标题、分类、标签等不再满足普通影视条件。手工资料仍可编辑；请取消不适用的作品关联。 ");
                continue;
            }
            Field(source.Provider, source.NameAtSource + " · " + source.PersonId);
            Hint("来自作品详情：" + source.VerifiedFromWorkUrl);
            var sourced = profile.SourcedDetails.FirstOrDefault(item =>
                item.Provider.Equals(source.Provider, StringComparison.OrdinalIgnoreCase) &&
                item.PersonId.Equals(source.PersonId, StringComparison.OrdinalIgnoreCase));
            if (sourced is not null)
            {
                Hint("人物详情读取于 " + sourced.FetchedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") +
                    " · 资料来自 " + sourced.SourceUrl + "；手工字段保持独立");
                Field("来源姓名", sourced.Name);
                Field("来源简介", sourced.Biography);
                Field("来源生日", sourced.Birthday);
                Field("来源逝世日期", sourced.Deathday);
                Field("来源性别", sourced.Gender);
                Field("来源出生地", sourced.PlaceOfBirth);
                Field("来源国家 / 地区", sourced.Country);
                if (sourced.PortraitUrl.Length > 0)
                {
                    var imageLink = UiButton("查看来源肖像链接");
                    imageLink.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                    imageLink.Click += (_, _) => OpenHttps(sourced.PortraitUrl);
                    content.Children.Add(imageLink);
                }
            }
            if (source.Provider is "TMDB" or "TVmaze")
            {
                var fetchLabel = sourced is null ? "读取人物来源详情"
                    : sourced.PortraitUrl.Length > 0 && sourced.PortraitBase64.Length == 0
                        ? "加载来源肖像" : "重新读取来源详情";
                var fetch = UiButton(fetchLabel);
                fetch.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                fetch.Click += async (_, _) =>
                {
                    if (source.Provider == "TMDB" && !TmdbProfileIsAllowed(profile))
                    {
                        RenderProfile();
                        return;
                    }
                    fetch.IsEnabled = false;
                    fetch.Content = "正在读取…";
                    try
                    {
                        var details = await RolePersonDetailsFetcher.FetchAsync(source, tmdbToken);
                        if (source.Provider == "TMDB" && !TmdbProfileIsAllowed(profile))
                        {
                            RenderProfile();
                            return;
                        }
                        await MutateAsync(() =>
                        {
                            profile.SourcedDetails.RemoveAll(item =>
                                item.Provider.Equals(source.Provider, StringComparison.OrdinalIgnoreCase) &&
                                item.PersonId.Equals(source.PersonId, StringComparison.OrdinalIgnoreCase));
                            profile.SourcedDetails.Add(details);
                        });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "读取来源详情失败：" + ex.Message,
                            "角色档案", MessageBoxButton.OK, MessageBoxImage.Warning);
                        fetch.IsEnabled = true;
                        fetch.Content = fetchLabel;
                    }
                };
                content.Children.Add(fetch);
            }
            var open = UiButton("查看人物来源页面");
            open.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            open.Click += (_, _) => OpenHttps(source.PersonUrl);
            content.Children.Add(open);
        }
        Section("关联作品");
        if (profile.Works.Count == 0) Hint("尚无作品关联。添加时须明确选择本地视频；未核实的姓名不会建立关系。 ");
        workPage = Math.Clamp(workPage, 0, Math.Max(0, (profile.Works.Count - 1) / WorksPerPage));
        if (profile.Works.Count > WorksPerPage)
        {
            var pager = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            var back = UiButton("上一页"); back.IsEnabled = workPage > 0;
            back.Click += (_, _) => { workPage--; RenderProfile(); };
            var next = UiButton("下一页"); next.IsEnabled = (workPage + 1) * WorksPerPage < profile.Works.Count;
            next.Click += (_, _) => { workPage++; RenderProfile(); };
            pager.Children.Add(back);
            pager.Children.Add(new TextBlock
            {
                Text = $"第 {workPage + 1} / {(profile.Works.Count + WorksPerPage - 1) / WorksPerPage} 页 · 共 {profile.Works.Count:N0} 部",
                VerticalAlignment = VerticalAlignment.Center, Foreground = Muted(), Margin = new Thickness(0, 0, 8, 6)
            });
            pager.Children.Add(next); content.Children.Add(pager);
        }
        foreach (var work in profile.Works.Skip(workPage * WorksPerPage).Take(WorksPerPage))
        {
            videosById.TryGetValue(work.LocalVideoId, out var local);
            var showWork = work.SourceProvider != "TMDB" || TmdbWorkIsAllowed(work,
                videosById, savedMetadata, origin, originVideo);
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 11) };
            row.Children.Add(new TextBlock { Text = showWork
                    ? (local?.DisplayTitle ?? work.WorkTitle) +
                      (local is null && work.LocalVideoId.Length > 0 ? " · 本地文件未找到" : "")
                    : "TMDB 关联作品已隐藏 · 本地编号 " + work.LocalVideoId,
                FontWeight = FontWeights.SemiBold, Foreground = Ink(), TextWrapping = TextWrapping.Wrap });
            if (showWork) row.Children.Add(new TextBlock
            {
                Text = (work.Role.Length == 0 ? "" : "角色：" + work.Role + " · ") +
                    (work.LinkOrigin == "verified-source" ? "已核实作品关系" : "手工关联") +
                    (work.SourceProvider.Length == 0 ? "" : " · " + work.SourceProvider),
                Foreground = Muted(), FontSize = 12, TextWrapping = TextWrapping.Wrap
            });
            var buttons = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            if (local is not null && showWork && work.ContentSha256.Length != 64)
            {
                var verify = UiButton("核对并关联当前文件");
                verify.Click += async (_, _) =>
                {
                    if (work.SourceProvider == "TMDB" && !TmdbWorkIsAllowed(work,
                            videosById, savedMetadata, origin, originVideo))
                    {
                        RenderProfile(); return;
                    }
                    var prompt = "这条作品关系来自已核实的来源资料，但本地文件尚未核对。\n\n"
                        + "来源作品：" + work.WorkTitle + "\n本地文件：" + local.DisplayTitle
                        + "\n\n确认这是对应的当前文件？软件会读取文件内容来建立校验，文件很大时可能需要一些时间。";
                    if (MessageBox.Show(this, prompt, "核对本地作品",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    verify.IsEnabled = false;
                    verify.Content = "正在核对文件…";
                    try
                    {
                        var captured = await RoleLocalWorkIdentity.CaptureAsync(local);
                        await MutateAsync(() => RoleLocalWorkIdentity.Apply(work, captured));
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "本地文件核对失败：" + ex.Message,
                            "角色档案", MessageBoxButton.OK, MessageBoxImage.Warning);
                        verify.IsEnabled = true;
                        verify.Content = "核对并关联当前文件";
                    }
                };
                buttons.Children.Add(verify);
            }
            if (local is not null && showWork && work.ContentSha256.Length == 64)
            {
                var details = UiButton("查看本地作品资料");
                details.Click += async (_, _) =>
                {
                    details.IsEnabled = false;
                    details.Content = "正在核对本地文件…";
                    try
                    {
                        if (work.SourceProvider == "TMDB" && !TmdbWorkIsAllowed(work,
                                videosById, savedMetadata, origin, originVideo))
                        {
                            RenderProfile();
                            return;
                        }
                        if (!await RoleLocalWorkIdentity.MatchesAsync(local, work))
                        {
                            MessageBox.Show(this, "本地文件与建立关联时的内容不一致，已阻止跳转。请取消旧关联后重新关联当前文件。",
                                "角色档案", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                        Close(); openVideoDetails(local.Id);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "无法核对本地文件：" + ex.Message, "角色档案",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    finally { details.IsEnabled = true; details.Content = "查看本地作品资料"; }
                };
                buttons.Children.Add(details);
            }
            if (showWork && work.EvidenceUrl.Length > 0)
            {
                var evidence = UiButton("查看关联证据"); evidence.Click += (_, _) => OpenHttps(work.EvidenceUrl);
                buttons.Children.Add(evidence);
            }
            var remove = UiButton("取消关联");
            remove.Click += async (_, _) =>
            {
                if (MessageBox.Show(this, "取消此作品与该档案的关联？", "角色档案",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                await MutateAsync(() => profile.Works.Remove(work));
            };
            buttons.Children.Add(remove); row.Children.Add(buttons); content.Children.Add(row);
        }
        var hasVerifiedCurrentTmdb = verifiedOptions.Any(option => option.Provider == "TMDB" &&
            profile.Sources.Any(source => source.Provider == "TMDB" &&
                source.PersonId == option.PersonId));
        if (originVideo is not null && (!tmdbProfile ||
                (hasVerifiedCurrentTmdb && CurrentTmdbWorkIsAllowed())) &&
            !profile.Works.Any(work => work.LocalVideoId.Equals(originVideo.Id,
                StringComparison.OrdinalIgnoreCase)))
        {
            var current = UiButton("关联当前作品：" + originVideo.DisplayTitle);
            current.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            current.Click += async (_, _) =>
            {
                current.IsEnabled = false;
                current.Content = "正在核对本地文件…";
                try { await AddCurrentWork(profile); }
                catch (Exception ex) { MessageBox.Show(this, "本地作品无法核对：" + ex.Message, "角色档案"); }
                finally { current.IsEnabled = true; current.Content = "关联当前作品：" + originVideo.DisplayTitle; }
            };
            content.Children.Add(current);
        }
        if (profile.Sources.Any(source => source.Provider == "TMDB"))
            Hint("TMDB 来源资料仅与经过来源核实的普通电影或剧集关联；若需整理其他本地作品，请另建手工档案。");
        else AddLocalWorkPicker(profile);
    }

    void AddLocalWorkPicker(RoleProfileStore.Profile profile)
    {
        var find = new TextBox { Height = 34, Padding = new Thickness(8, 5, 8, 5),
            ToolTip = "输入至少两个字，从本地视频中选择一部明确关联" };
        var matches = new ComboBox { Height = 35, Margin = new Thickness(0, 5, 0, 5),
            DisplayMemberPath = nameof(VideoItem.DisplayTitle) };
        var debounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        var searchRevision = 0;
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            var query = find.Text.Trim();
            var revision = ++searchRevision;
            if (query.Length < 2) { matches.ItemsSource = Array.Empty<VideoItem>(); return; }
            try
            {
                var found = await Task.Run(() => videos
                    .Where(video => video.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || (!string.IsNullOrWhiteSpace(video.Code) && video.Code.Contains(query,
                            StringComparison.OrdinalIgnoreCase)))
                    .Take(60).ToArray());
                if (revision == searchRevision && find.IsLoaded) matches.ItemsSource = found;
            }
            catch (Exception ex) { if (revision == searchRevision)
                MessageBox.Show(this, "本地作品搜索失败：" + ex.Message, "角色档案"); }
        };
        find.TextChanged += (_, _) => { searchRevision++; debounce.Stop(); debounce.Start(); };
        find.Unloaded += (_, _) => { searchRevision++; debounce.Stop(); };
        Hint("从本地视频库选择作品（至少输入 2 字）");
        content.Children.Add(find); content.Children.Add(matches);
        var role = new TextBox { Height = 34, Padding = new Thickness(8, 5, 8, 5),
            ToolTip = "在此作品中的角色或关系，可留空" };
        Hint("作品中的角色或关系（可选）"); content.Children.Add(role);
        var add = UiButton("明确关联所选本地视频", true);
        add.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; add.Margin = new Thickness(0, 8, 0, 0);
        add.Click += async (_, _) =>
        {
            if (matches.SelectedItem is not VideoItem video)
            { MessageBox.Show(this, "请先从搜索结果中选择一部本地视频。", "角色档案"); return; }
            if (profile.Works.Any(work => work.LocalVideoId.Equals(video.Id, StringComparison.OrdinalIgnoreCase)))
            { MessageBox.Show(this, "这部本地视频已经关联。", "角色档案"); return; }
            var relation = role.Text.Trim();
            if (relation.Length > 180)
            { MessageBox.Show(this, "角色或关系说明最多 180 字。", "角色档案"); return; }
            add.IsEnabled = false;
            add.Content = "正在核对本地文件…";
            try
            {
                var captured = await RoleLocalWorkIdentity.CaptureAsync(video);
                var work = new RoleProfileStore.WorkLink
                {
                    LocalVideoId = video.Id, WorkTitle = video.DisplayTitle,
                    Role = relation, LinkOrigin = "manual"
                };
                RoleLocalWorkIdentity.Apply(work, captured);
                await MutateAsync(() => profile.Works.Add(work));
            }
            catch (Exception ex) { MessageBox.Show(this, "本地作品无法核对：" + ex.Message, "角色档案"); }
            finally { add.IsEnabled = true; add.Content = "明确关联所选本地视频"; }
        };
        content.Children.Add(add);
    }

    async Task AddCurrentWork(RoleProfileStore.Profile profile)
    {
        if (originVideo is null) return;
        if (profile.Works.Any(work => work.LocalVideoId.Equals(originVideo.Id, StringComparison.OrdinalIgnoreCase))) return;
        // Only a matching verified source person ID gives this work a verified
        // relation. All other user-selected links are labelled manual.
        var option = verifiedOptions.FirstOrDefault(value => profile.Sources.Any(source =>
            source.Provider.Equals(value.Provider, StringComparison.OrdinalIgnoreCase) &&
            source.PersonId.Equals(value.PersonId, StringComparison.OrdinalIgnoreCase)));
        if (profile.Sources.Any(source => source.Provider == "TMDB") &&
            option?.Provider != "TMDB") return;
        if (option?.Provider == "TMDB" && !CurrentTmdbWorkIsAllowed()) return;
        var work = option is null ? new RoleProfileStore.WorkLink
        {
            LocalVideoId = originVideo.Id, WorkTitle = originVideo.DisplayTitle, LinkOrigin = "manual"
        } : VerifiedWork(option, originVideo.Id);
        RoleLocalWorkIdentity.Apply(work, await RoleLocalWorkIdentity.CaptureAsync(originVideo));
        await MutateAsync(() => profile.Works.Add(work));
    }

    RoleProfileStore.WorkLink VerifiedWork(VerifiedPersonOption person, string localVideoId) => new()
    {
        LocalVideoId = localVideoId, WorkTitle = origin?.Title ?? "来源作品",
        Role = person.CharacterName.Length > 180 ? person.CharacterName[..180] : person.CharacterName,
        SourceProvider = person.Provider, SourcePersonId = person.PersonId,
        SourceWorkId = person.OriginWorkUrl,
        EvidenceUrl = person.OriginWorkUrl, LinkOrigin = "verified-source"
    };

    async Task CreateOrOpenVerified(VerifiedPersonOption option)
    {
        if (autoRunning) return;
        if (option.Provider == "TMDB" && !CurrentTmdbWorkIsAllowed())
            throw new InvalidOperationException("当前视频已不符合 TMDB 普通影视来源条件，不能建立人物档案。 ");
        var found = RoleProfileStore.FindBySource(state, option.Provider, option.PersonId);
        if (found is not null)
        {
            selectedProfileId = found.Id;
            if (originVideo is not null && !found.Works.Any(work => work.LocalVideoId.Equals(originVideo.Id,
                    StringComparison.OrdinalIgnoreCase)))
            {
                if (MessageBox.Show(this, "将当前已核实作品关联到此人物档案？", "角色档案",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    var work = VerifiedWork(option, originVideo.Id);
                    RoleLocalWorkIdentity.Apply(work, await RoleLocalWorkIdentity.CaptureAsync(originVideo));
                    await MutateAsync(() => found.Works.Add(work));
                }
            }
            RefreshList(); return;
        }
        var profile = new RoleProfileStore.Profile { Name = option.Name };
        profile.Sources.Add(new RoleProfileStore.SourceIdentity
        {
            Provider = option.Provider, PersonId = option.PersonId,
            PersonUrl = option.PersonUrl, NameAtSource = option.Name,
            VerifiedFromWorkUrl = option.OriginWorkUrl
        });
        if (originVideo is not null)
        {
            var work = VerifiedWork(option, originVideo.Id);
            RoleLocalWorkIdentity.Apply(work, await RoleLocalWorkIdentity.CaptureAsync(originVideo));
            profile.Works.Add(work);
        }
        await MutateAsync(() => state.Profiles.Add(profile), profile.Id);
    }

    async Task MutateAsync(Action change, string? selectId = null)
    {
        if (autoRunning) throw new InvalidOperationException("正在自动搜刮人物，请先等待完成或取消。 ");
        if (saving) throw new InvalidOperationException("角色档案正在保存，请稍后重试。");
        saving = true;
        var visual = (UIElement)Content;
        visual.IsEnabled = false;
        try
        {
            change();
            var p = state.Profiles.FirstOrDefault(value => value.Id == (selectId ?? selectedProfileId));
            if (p is not null) p.UpdatedUtc = DateTimeOffset.UtcNow;
            await Task.Run(() => RoleProfileStore.Save(dataDir, state));
            if (selectId is not null) selectedProfileId = selectId;
            RefreshList();
        }
        catch (Exception ex)
        {
            try { state = await Task.Run(() => RoleProfileStore.Load(dataDir)); } catch { }
            MessageBox.Show(this, ex.Message, "角色档案未保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshList();
        }
        finally { saving = false; visual.IsEnabled = true; }
    }

    void EditProfile(RoleProfileStore.Profile? existing)
    {
        if (autoRunning) return;
        var draft = existing is null ? new RoleProfileStore.Profile() :
            RoleProfileStore.Parse(System.Text.Json.JsonSerializer.Serialize(new RoleProfileStore.State
            { Profiles = [existing] })).Profiles[0];
        var editor = new Window { Title = existing is null ? "新建角色档案" : "编辑角色档案",
            Owner = this, Width = 650, Height = 690, MinWidth = 500, MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 253)) };
        var layout = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var save = UiButton("保存档案", true); var cancel = UiButton("取消");
        cancel.Click += (_, _) => editor.Close();
        footer.Children.Add(cancel); footer.Children.Add(save);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var fields = new StackPanel();
        var scroll = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        layout.Children.Add(scroll);
        var inputs = new Dictionary<string, TextBox>();
        TextBox Add(string key, string label, string value, bool multiline = false)
        {
            fields.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold,
                Foreground = Ink(), Margin = new Thickness(0, 9, 0, 4) });
            var box = new TextBox { Text = value, MinHeight = multiline ? 105 : 35,
                Padding = new Thickness(8, 5, 8, 5), TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = multiline, VerticalScrollBarVisibility = multiline
                    ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
            fields.Children.Add(box); inputs[key] = box; return box;
        }
        Add("name", "姓名 / 角色名 *", draft.Name);
        Add("aliases", "别名（用分号分隔）", string.Join("；", draft.Aliases));
        Add("role", "身份 / 关系", draft.Role);
        Add("birthday", "生日", draft.Birthday);
        Add("gender", "性别", draft.Gender);
        Add("height", "身高", draft.Height);
        Add("weight", "体重", draft.Weight);
        Add("measurements", "三围", draft.Measurements);
        Add("blood", "血型", draft.BloodType);
        Add("bio", "生平 / 简介", draft.Biography, multiline: true);
        fields.Children.Add(new TextBlock { Text = "这些值由你维护。已核实来源姓名和关联证据会单独保留。",
            Foreground = Muted(), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 10) });
        var portrait = new WrapPanel();
        var choose = UiButton("选择本地肖像…");
        choose.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "图片 (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
                CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(editor) != true) return;
            try { RoleProfileStore.SetPortrait(draft, picker.FileName); }
            catch (Exception ex) { MessageBox.Show(editor, ex.Message, "肖像无法使用"); }
        };
        var clear = UiButton("移除肖像"); clear.Click += (_, _) => RoleProfileStore.ClearPortrait(draft);
        portrait.Children.Add(choose); portrait.Children.Add(clear); fields.Children.Add(portrait);
        var editorSaving = false;
        editor.Closing += (_, args) => { if (editorSaving) args.Cancel = true; };
        save.Click += async (_, _) =>
        {
            draft.Name = inputs["name"].Text.Trim();
            draft.Aliases = inputs["aliases"].Text.Split([';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            draft.Role = inputs["role"].Text.Trim(); draft.Birthday = inputs["birthday"].Text.Trim();
            draft.Gender = inputs["gender"].Text.Trim(); draft.Height = inputs["height"].Text.Trim();
            draft.Weight = inputs["weight"].Text.Trim(); draft.Measurements = inputs["measurements"].Text.Trim();
            draft.BloodType = inputs["blood"].Text.Trim(); draft.Biography = inputs["bio"].Text.Trim();
            editorSaving = true;
            editor.IsEnabled = false;
            try
            {
                if (existing is null) state.Profiles.Add(draft);
                else
                {
                    var index = state.Profiles.FindIndex(profile => profile.Id.Equals(existing.Id,
                        StringComparison.OrdinalIgnoreCase));
                    if (index < 0) throw new InvalidDataException("原档案已更改，请重新打开编辑窗口。");
                    state.Profiles[index] = draft;
                }
                await Task.Run(() => RoleProfileStore.Save(dataDir, state));
                selectedProfileId = draft.Id;
                editorSaving = false;
                editor.DialogResult = true;
                RefreshList();
            }
            catch (Exception ex)
            {
                try { state = await Task.Run(() => RoleProfileStore.Load(dataDir)); } catch { }
                MessageBox.Show(editor, ex.Message, "角色档案未保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { editorSaving = false; editor.IsEnabled = true; }
        };
        editor.Content = layout;
        editor.ShowDialog();
    }

    void Section(string title) => content.Children.Add(new TextBlock { Text = title,
        FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Ink(),
        Margin = new Thickness(0, 14, 0, 8) });
    void Hint(string text) => content.Children.Add(new TextBlock { Text = text,
        Foreground = Muted(), FontSize = 12, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 6) });
    void Field(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, Foreground = Muted() });
        var text = new TextBlock { Text = value, Foreground = Ink(), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 1); row.Children.Add(text); content.Children.Add(row);
    }

    void OpenHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "无法打开来源页面：" + ex.Message, "角色档案"); }
    }
}

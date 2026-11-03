using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace YinggeVideoLibrary;

internal sealed partial class RoleProfilesView
{
    async Task SearchWikipediaForNameAsync(string name, RoleProfileStore.Profile? target)
    {
        if (actorLookupRunning || autoRunning || saving) return;
        actorLookupRunning = true;
        autoCancellation = new CancellationTokenSource();
        autoCancel.Visibility = Visibility.Visible;
        autoStatus.Text = "正在按姓名搜索维基百科公开人物条目…";
        try
        {
            var token = autoCancellation.Token;
            var candidates = await WikipediaPersonScraper.SearchAsync(name, token);
            token.ThrowIfCancellationRequested();
            if (candidates.Count == 0)
            {
                autoStatus.Text = "维基百科没有找到候选条目；本地档案未更改。";
                return;
            }
            var chosen = ChooseWikipediaCandidate(candidates);
            if (chosen is null) { autoStatus.Text = "已取消维基百科条目选择。"; return; }
            autoStatus.Text = "正在读取所选条目的公开摘要…";
            var reference = await WikipediaPersonScraper.FetchAsync(chosen, token);
            token.ThrowIfCancellationRequested();
            var shouldLink = ConfirmWikipediaImport(reference, target, originVideo is not null);
            if (shouldLink is null) { autoStatus.Text = "已取消维基百科导入。"; return; }

            RoleProfileStore.WorkLink? localWork = null;
            if (shouldLink.Value && originVideo is not null)
            {
                localWork = new RoleProfileStore.WorkLink
                {
                    LocalVideoId = originVideo.Id,
                    WorkTitle = origin?.Title ?? originVideo.Title,
                    LinkOrigin = "manual"
                };
                RoleLocalWorkIdentity.Apply(localWork, await RoleLocalWorkIdentity.CaptureAsync(originVideo));
            }
            if (target is null)
            {
                var created = new RoleProfileStore.Profile { Name = name.Trim() };
                created.WikipediaReferences.Add(reference);
                if (localWork is not null) created.Works.Add(localWork);
                await MutateAsync(() => state.Profiles.Add(created), created.Id);
                target = created;
            }
            else
            {
                var selected = target;
                await MutateAsync(() =>
                {
                    selected.WikipediaReferences.RemoveAll(item => item.PageId == reference.PageId);
                    selected.WikipediaReferences.Add(reference);
                    if (localWork is not null && !selected.Works.Any(work => work.LocalVideoId.Equals(
                            localWork.LocalVideoId, StringComparison.OrdinalIgnoreCase)))
                        selected.Works.Add(localWork);
                }, selected.Id);
            }
            var persisted = await Task.Run(() => RoleProfileStore.Load(dataDir), token);
            var saved = persisted.Profiles.FirstOrDefault(profile => profile.Id == target.Id);
            autoStatus.Text = saved?.WikipediaReferences.Any(item => item.PageId == reference.PageId) == true &&
                (!shouldLink.Value || saved.Works.Any(work => work.LocalVideoId == originVideo?.Id))
                ? "维基百科摘要已保存在本地人物库。当前作品如有勾选，已作为手工确认关联。"
                : "维基百科资料未能保存，请检查档案保存提示后重试。";
        }
        catch (OperationCanceledException) { autoStatus.Text = "已取消维基百科搜刮，档案未更改。"; }
        catch (Exception ex)
        {
            autoStatus.Text = "维基百科搜刮未完成，档案未更改。";
            MessageBox.Show(Window.GetWindow(this), ex.Message, "维基百科人物资料",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            actorLookupRunning = false;
            autoCancel.Visibility = Visibility.Collapsed;
            autoCancellation?.Dispose();
            autoCancellation = null;
        }
    }

    WikipediaPersonScraper.Candidate? ChooseWikipediaCandidate(
        IReadOnlyList<WikipediaPersonScraper.Candidate> candidates)
    {
        var dialog = new Window { Title = "选择维基百科人物条目", Owner = Window.GetWindow(this),
            Width = 560, Height = 310, MinWidth = 420, MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = UiTheme.SurfaceBrush, Foreground = Ink() };
        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock { Text = "请核对人物条目，姓名相同不代表身份相同。",
            Foreground = Muted(), TextWrapping = TextWrapping.Wrap });
        var picker = new ComboBox { ItemsSource = candidates, DisplayMemberPath = "Display",
            SelectedIndex = 0, Height = 35, Margin = new Thickness(0, 12, 0, 10) };
        body.Children.Add(picker);
        var current = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Muted() };
        void Refresh() => current.Text = picker.SelectedItem is WikipediaPersonScraper.Candidate item
            ? $"条目 ID：{item.PageId}\n{item.Description}\n{WikipediaPersonScraper.PageUrl(item.Title)}" : "";
        picker.SelectionChanged += (_, _) => Refresh();
        Refresh(); body.Children.Add(current);
        var choose = UiButton("读取所选条目", true);
        choose.Margin = new Thickness(0, 18, 0, 0);
        choose.Click += (_, _) => dialog.DialogResult = true;
        body.Children.Add(choose);
        dialog.Content = body;
        return dialog.ShowDialog() == true ? picker.SelectedItem as WikipediaPersonScraper.Candidate : null;
    }

    bool? ConfirmWikipediaImport(RoleProfileStore.WikipediaReference reference,
        RoleProfileStore.Profile? target, bool hasCurrentVideo)
    {
        var dialog = new Window { Title = "核对并保存维基资料", Owner = Window.GetWindow(this),
            Width = 610, Height = 510, MinWidth = 440, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = UiTheme.SurfaceBrush, Foreground = Ink() };
        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock { Text = reference.Title, FontSize = 20,
            FontWeight = FontWeights.SemiBold, Foreground = Ink() });
        body.Children.Add(new TextBlock { Text = reference.Description + "\n" + reference.Url,
            TextWrapping = TextWrapping.Wrap, Foreground = Muted(), Margin = new Thickness(0, 8, 0, 12) });
        body.Children.Add(new TextBlock { Text = reference.Extract.Length == 0 ? "该条目没有可读取的摘要。" : reference.Extract,
            TextWrapping = TextWrapping.Wrap, Foreground = Ink(), MaxHeight = 220 });
        body.Children.Add(new TextBlock { Text = "摘要将保存在本地人物库，并保留维基百科条目链接和署名信息；不会覆盖手工填写的生平。",
            TextWrapping = TextWrapping.Wrap, Foreground = Muted(), Margin = new Thickness(0, 13, 0, 9) });
        var link = new CheckBox { Content = "同时把当前视频关联到此人物（手工确认关系）",
            IsChecked = false, Visibility = hasCurrentVideo ? Visibility.Visible : Visibility.Collapsed,
            Foreground = Ink(), Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(link);
        var confirm = UiButton(target is null ? "新建本地档案并保存" : "保存到当前本地档案", true);
        confirm.Click += (_, _) => dialog.DialogResult = true;
        body.Children.Add(confirm);
        dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return dialog.ShowDialog() == true ? link.IsChecked == true : null;
    }
}

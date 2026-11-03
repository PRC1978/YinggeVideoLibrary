using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    void NavRoles(object sender, RoutedEventArgs e)
    {
        // Classic mode keeps the standalone window; the redesigned modes embed
        // the same shared view as a main-window page.
        if (UiTheme.IsClassicMode) OpenRoleProfiles();
        else ShowView("roles");
    }

    string VerifyRoleProfilesOffscreen()
    {
        if (RolesNav.Parent is not StackPanel)
            throw new InvalidOperationException("人物主入口未显示在可滚动侧栏。 ");
        using var source = System.Text.Json.JsonDocument.Parse("""
            {"id":7,"title":"示例作品","credits":{"cast":[{"id":11,"name":"示例演员","character":"主角"}]}}
            """);
        var candidate = new MovieMetadata
        {
            Source = "TMDB 电影", SourceUrl = "https://www.themoviedb.org/movie/7", Title = "示例作品"
        };
        var verified = AdditionalScrapers.ParseTmdbDetails(candidate, source.RootElement, "movie");
        var fixtureDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "yingge-role-ui-" + Guid.NewGuid().ToString("N"));
        var sample = new[] { new VideoItem
        {
            Id = "role-ui-diagnostic", Title = "示例作品", Category = "电影",
            Path = System.IO.Path.Combine(fixtureDir, "example.mp4"), Folder = fixtureDir,
            Size = 1, Modified = DateTime.UtcNow
        } };
        var overview = new RoleProfilesWindow(fixtureDir, sample, null, null, "", _ => { }, true);
        var details = new RoleProfilesWindow(fixtureDir, sample, verified,
            sample.FirstOrDefault(), "", _ => { }, true);
        var count = details.VerifiedOptionCountForDiagnostics;
        overview.Close(); details.Close();
        if (count != 1) throw new InvalidOperationException("已核实作品的人物档案入口没有保留稳定 ID。 ");

        // Embedded-host lifecycle probe: switching pages unloads the view, and
        // that must NOT tear down its timer/cancellation/state (regression:
        // Cleanup used to run on Unloaded, disposing the auto-discovery token
        // while a scrape could still be running).
        var host = new Window
        {
            Left = -30000, Top = -30000, Width = 900, Height = 640,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0
        };
        var embedded = new RoleProfilesView(fixtureDir, sample, null, null, "", _ => { }, true);
        host.Content = embedded;
        host.Show();
        host.Content = null; // page switch away: view unloads
        host.Content = embedded; // page switch back: view reloads
        embedded.SelectProfileForDiagnostics("nonexistent-id"); // must survive re-attach
        embedded.RequestAutoStop(); // safe when nothing is running
        if (embedded.IsBusy) throw new InvalidOperationException("空闲人物页被误判为忙碌状态");
        embedded.Cleanup();
        embedded.Cleanup(); // final teardown must be idempotent
        embedded.RequestAutoStop(); // must stay safe after cleanup
        host.Close();
        return "左侧人物入口可用；总览与作品详情两条路径离屏创建正常；TMDB 稳定人物 ID 已显示；"
            + "嵌入宿主经切页卸载/重载后状态正确，清理幂等；未写入用户资料";
    }

    void OpenRoleProfiles(VideoItem? video = null, MovieMetadata? record = null)
    {
        try
        {
            var browser = new RoleProfilesWindow(DataDir, videos, record, video, config.TmdbToken, id =>
            {
                var found = videos.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (found is not null) ShowMetadataDetailsForVideo(found);
            }, savedMetadata: metadata) { Owner = this };
            browser.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法打开角色档案", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

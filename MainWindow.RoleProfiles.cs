using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    void NavRoles(object sender, RoutedEventArgs e) => OpenRoleProfiles();

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
        return "左侧人物入口可用；总览与作品详情两条路径离屏创建正常；TMDB 稳定人物 ID 已显示；未写入用户资料";
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

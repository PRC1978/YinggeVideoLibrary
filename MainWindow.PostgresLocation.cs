using System.IO;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    private void AppendPostgresLocationControls(StackPanel section)
    {
        section.Children.Add(new TextBlock
        {
            Text = "本地 PostgreSQL 位置",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 0)
        });
        section.Children.Add(SettingsHint("当前数据库：" + PostgresClusterLocation.DataPath(DataDir)));
        section.Children.Add(SettingsHint("选择新磁盘文件夹后，下次启动会在数据库停止时复制并逐文件校验。成功后才切换；旧数据库副本暂时保留，以便失败时回退。请先确保目标磁盘空间充足且始终可用。"));
        var status = SettingsHint(PostgresClusterLocation.PendingTarget(DataDir) is { } pending
            ? "待迁移到：" + pending
            : "尚未排定迁移。");
        section.Children.Add(status);
        var cancel = new Button
        {
            Content = "取消待迁移",
            Style = (Style)FindResource("Action"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            IsEnabled = PostgresClusterLocation.PendingTarget(DataDir) is not null
        };
        var choose = new Button
        {
            Content = "选择数据库新位置…",
            Style = (Style)FindResource("Action"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        choose.Click += (_, _) =>
        {
            using var picker = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择 PostgreSQL 数据库的新位置（软件会在其中创建独立文件夹）",
                ShowNewFolderButton = true
            };
            if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            try
            {
                var target = PostgresClusterLocation.Stage(DataDir, picker.SelectedPath);
                status.Text = "已排定：" + target + "。完全退出映格并重新打开后迁移。";
                cancel.IsEnabled = true;
            }
            catch (Exception error)
            {
                status.Text = "无法排定迁移：" + error.Message;
            }
        };
        section.Children.Add(choose);
        cancel.Click += (_, _) =>
        {
            try
            {
                PostgresClusterLocation.CancelPending(DataDir);
                status.Text = "已取消待迁移；当前数据库位置未改动。";
                cancel.IsEnabled = false;
            }
            catch (Exception error) { status.Text = "取消失败：" + error.Message; }
        };
        section.Children.Add(cancel);
        if (PostgresClusterLocation.PreviousPath(DataDir) is { } previous)
            section.Children.Add(SettingsHint("保留的旧数据库副本：" + previous + "。核对新位置与备份后，可自行清理此副本。"));
    }
}

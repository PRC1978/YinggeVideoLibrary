using System.IO;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    void AppendPostgresBackupControls(StackPanel section)
    {
        section.Children.Add(new TextBlock { Text = "PostgreSQL 一致性备份",
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 17, 0, 0) });
        section.Children.Add(SettingsHint("备份在下次启动、载入视频库之前完成；失败时保留待办，不会切回旧文件库。"));
        var enabled = new CheckBox { Content = "启用定期备份", Margin = new Thickness(0, 8, 0, 8) };
        section.Children.Add(enabled);
        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        options.Children.Add(new TextBlock { Text = "频率", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0) });
        var frequency = new ComboBox { Width = 110, Height = 34, Margin = new Thickness(0, 0, 16, 0) };
        foreach (var (key, label) in new[] { ("daily", "每天"), ("weekly", "每周"), ("monthly", "每月") })
            frequency.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        frequency.SelectedIndex = 0;
        options.Children.Add(frequency);
        options.Children.Add(new TextBlock { Text = "保留最近", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0) });
        var retention = new TextBox { Width = 56, Height = 34, Text = "3",
            VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        options.Children.Add(retention);
        options.Children.Add(new TextBlock { Text = "轮", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0) });
        section.Children.Add(options);
        section.Children.Add(new TextBlock { Text = "备份位置", FontWeight = FontWeights.SemiBold });
        var destination = new TextBox { Height = 35, IsReadOnly = true,
            Text = PostgresBackupPlanStore.BackupRoot(DataDir, new()),
            VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 7) };
        section.Children.Add(destination);
        var selectedDestination = "";
        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var choose = new Button { Content = "选择备份文件夹", Style = (Style)FindResource("Action") };
        choose.Click += (_, _) =>
        {
            using var picker = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择数据库备份位置",
                SelectedPath = Directory.Exists(selectedDestination) ? selectedDestination : "",
                ShowNewFolderButton = true
            };
            if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            selectedDestination = picker.SelectedPath;
            destination.Text = selectedDestination;
        };
        controls.Children.Add(choose);
        var useDefault = new Button { Content = "使用默认位置", Style = (Style)FindResource("Action") };
        useDefault.Click += (_, _) =>
        {
            selectedDestination = "";
            destination.Text = PostgresBackupPlanStore.BackupRoot(DataDir, new());
        };
        controls.Children.Add(useDefault);
        var save = new Button { Content = "保存备份设置", Style = (Style)FindResource("Primary") };
        save.Click += async (_, _) =>
        {
            try
            {
                if (!int.TryParse(retention.Text.Trim(), out var keep) || keep is < 1 or > 30)
                    throw new InvalidDataException("保留轮数须为 1 到 30。");
                PostgresBackupPlanStore.ValidateDestination(DataDir,
                    PostgresBackupPlanStore.BackupRoot(DataDir,
                        new() { DestinationDirectory = selectedDestination }), config.Folders);
                var selectedFrequency = (frequency.SelectedItem as ComboBoxItem)?.Tag as string ?? "daily";
                var saved = await PostgresBackupPlanStore.ChangeAsync(postgresSession!.ConnectionString,
                    plan =>
                    {
                        var changed = !plan.Enabled || plan.Frequency != selectedFrequency;
                        plan.Enabled = enabled.IsChecked == true;
                        plan.Frequency = selectedFrequency;
                        plan.RetentionCount = keep;
                        plan.DestinationDirectory = selectedDestination;
                        plan.NextDueUtc = !plan.Enabled ? null : changed || plan.NextDueUtc is null
                            ? PostgresBackupPlanStore.NextDueUtc(selectedFrequency) : plan.NextDueUtc;
                        return plan;
                    });
                periodicBackupEnabled = saved.Enabled;
                periodicBackupNextDueUtc = saved.NextDueUtc;
                RefreshOfflineBackupStatus();
            }
            catch (Exception error) { settingsOfflineBackupStatus!.Text = "保存备份设置失败：" + error.Message; }
        };
        controls.Children.Add(save);
        section.Children.Add(controls);
        var actions = new WrapPanel();
        var stage = new Button { Content = "下次启动创建一致性备份", Style = (Style)FindResource("Primary") };
        stage.Click += async (_, _) =>
        {
            try
            {
                await PostgresBackupPlanStore.ChangeAsync(postgresSession!.ConnectionString,
                    plan => { plan.Pending = true; return plan; });
                RefreshOfflineBackupStatus();
            }
            catch (Exception error) { settingsOfflineBackupStatus!.Text = "无法排定备份：" + error.Message; }
        };
        actions.Children.Add(stage);
        var cancel = new Button { Content = "取消待执行备份", Style = (Style)FindResource("Action") };
        cancel.Click += async (_, _) =>
        {
            try
            {
                await PostgresBackupPlanStore.ChangeAsync(postgresSession!.ConnectionString,
                    plan => { plan.Pending = false; return plan; });
                RefreshOfflineBackupStatus();
            }
            catch (Exception error) { settingsOfflineBackupStatus!.Text = "取消失败：" + error.Message; }
        };
        actions.Children.Add(cancel);
        section.Children.Add(actions);
        settingsOfflineBackupStatus = SettingsHint("正在读取数据库备份状态…");
        section.Children.Add(settingsOfflineBackupStatus);
        _ = LoadInitialPostgresBackupPlanAsync();

        async Task LoadInitialPostgresBackupPlanAsync()
        {
            try
            {
                var plan = await PostgresBackupPlanStore.LoadAsync(postgresSession!.ConnectionString);
                if (isClosing) return;
                enabled.IsChecked = plan.Enabled;
                frequency.SelectedItem = frequency.Items.Cast<ComboBoxItem>()
                    .First(item => (string)item.Tag == plan.Frequency);
                retention.Text = plan.RetentionCount.ToString();
                selectedDestination = plan.DestinationDirectory;
                destination.Text = PostgresBackupPlanStore.BackupRoot(DataDir, plan);
                periodicBackupEnabled = plan.Enabled;
                periodicBackupNextDueUtc = plan.NextDueUtc;
                RefreshOfflineBackupStatus();
            }
            catch (Exception error) { settingsOfflineBackupStatus!.Text = "数据库备份设置无法读取：" + error.Message; }
        }
    }

    async Task RefreshPostgresBackupStatusAsync()
    {
        try
        {
            var plan = await PostgresBackupPlanStore.LoadAsync(postgresSession!.ConnectionString);
            if (isClosing || settingsOfflineBackupStatus is null) return;
            periodicBackupEnabled = plan.Enabled;
            periodicBackupNextDueUtc = plan.NextDueUtc;
            settingsOfflineBackupStatus.Text =
                (plan.Pending ? "已排定：关闭所有映格窗口后，下次启动先创建备份。" :
                    plan.Enabled ? $"下次到期：{plan.NextDueUtc?.ToLocalTime():yyyy-MM-dd HH:mm}" : "定期备份：关闭") +
                "\n目标：" + PostgresBackupPlanStore.BackupRoot(DataDir, plan) +
                (plan.LastCompletedUtc is null ? "\n尚无数据库备份。" :
                    $"\n最近完成：{plan.LastCompletedUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm} · {plan.LastBackupDirectory}") +
                (plan.LastError.Length == 0 ? "" : "\n上次失败：" + plan.LastError);
        }
        catch (Exception error)
        {
            if (!isClosing && settingsOfflineBackupStatus is not null)
                settingsOfflineBackupStatus.Text = "数据库备份状态读取失败：" + error.Message;
        }
    }
}

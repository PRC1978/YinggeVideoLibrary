using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Yingge.Cloud.Client;
using Yingge.Cloud.Protocol;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace YinggeVideoLibrary;

// Cloud account UI. Every network call is user initiated and awaited off the
// library path; a failure only changes the status line.
public partial class MainWindow
{
    CloudAccountRuntime? cloudAccount;

    CloudAccountRuntime CloudAccount(CloudAccountSettings settings)
    {
        cloudAccount?.Dispose();
        cloudAccount = new CloudAccountRuntime(settings);
        return cloudAccount;
    }

    FrameworkElement BuildCloudAccountSettingsEntry()
    {
        var body = new StackPanel { Margin = new Thickness(0, 16, 0, 8) };
        body.Children.Add(new TextBlock
        {
            Text = "云端账号（错误日志）", FontWeight = FontWeights.SemiBold,
            Foreground = UiTheme.TextPrimaryBrush
        });
        body.Children.Add(new TextBlock
        {
            Text = "邀请制。登录后只上传错误日志文字，已在本机脱敏；视频文件、封面、索引、配置和 API Key 不会上传。"
                 + "会话令牌只保存在 Windows 凭据管理器。",
            TextWrapping = TextWrapping.Wrap, Foreground = UiTheme.TextSecondaryBrush,
            Margin = new Thickness(0, 4, 0, 8)
        });

        var saved = CloudAccountSettings.Load();
        var server = LabeledInput(body, "服务器地址（HTTPS，例如 https://reports.yingevideo.top）", saved.ServerUrl);
        var username = LabeledInput(body, "账号名", "");
        body.Children.Add(new TextBlock
        {
            Text = "密码", Foreground = UiTheme.TextPrimaryBrush, Margin = new Thickness(0, 4, 0, 2)
        });
        var password = new PasswordBox { Height = 34, Width = 480, Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        body.Children.Add(password);

        var status = new TextBlock
        {
            Text = "未登录。", TextWrapping = TextWrapping.Wrap, Foreground = UiTheme.TextSecondaryBrush,
            Margin = new Thickness(0, 8, 0, 6)
        };

        var buttons = new WrapPanel();
        var login = new Button { Content = "登录", Style = (Style)FindResource("Action") };
        var register = new Button { Content = "用邀请码注册", Style = (Style)FindResource("Action") };
        var logout = new Button { Content = "退出登录", Style = (Style)FindResource("Action") };
        var check = new Button { Content = "检查登录状态", Style = (Style)FindResource("Action") };
        var upload = new Button { Content = "上传错误日志", Style = (Style)FindResource("Action") };
        var pending = new Button { Content = "补传待上传日志", Style = (Style)FindResource("Action") };
        var mine = new Button { Content = "查看/删除我的上传", Style = (Style)FindResource("Action") };

        CloudAccountSettings Current()
        {
            var settings = new CloudAccountSettings { ServerUrl = server.Text.Trim() };
            if (settings.IsConfigured)
            {
                try { settings.Save(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { /* Non-fatal: the endpoint is only a convenience setting. */ }
            }
            return settings;
        }

        login.Click += (_, _) => RunCloudAction(status, async () =>
        {
            var auth = await CloudAccount(Current()).LoginAsync(username.Text.Trim(), password.Password,
                CancellationToken.None);
            password.Clear();
            return $"已登录：{auth.Username}，会话有效至 {auth.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm}。";
        });

        register.Click += (_, _) =>
        {
            var invite = PromptInviteCode();
            if (invite is null) return;
            RunCloudAction(status, async () =>
            {
                var created = await CloudAccount(Current()).RegisterAsync(invite, username.Text.Trim(),
                    password.Password, CancellationToken.None);
                password.Clear();
                return $"账号 {created.Username} 已创建，请点击“登录”。";
            });
        };

        logout.Click += (_, _) => RunCloudAction(status, async () =>
        {
            var result = await CloudAccount(Current()).LogoutAsync(CancellationToken.None);
            return result.RevokedOnServer
                ? "已退出登录，服务器会话已撤销。"
                : "本地已退出；服务器暂时无法连接，会话将在到期后失效。";
        });

        check.Click += (_, _) => RunCloudAction(status, async () =>
        {
            var runtime = CloudAccount(Current());
            var session = await runtime.GetStatusAsync(CancellationToken.None);
            return session is null
                ? "当前没有有效登录，请先登录。"
                : $"已登录：{session.Username}；24 小时内已上传 {session.UploadsLast24Hours}/{session.DailyUploadLimit} 份。";
        });

        upload.Click += async (_, _) => await UploadErrorReportToCloudAsync(upload, Current(), status);
        pending.Click += (_, _) => RunCloudAction(status, async () =>
        {
            var result = await CloudAccount(Current()).FlushOutboxAsync(CancellationToken.None);
            return result.Uploaded == 0 && result.Remaining > 0
                ? $"补传失败，仍有 {result.Remaining} 份保存在本机：{result.LastError}"
                : $"已补传 {result.Uploaded} 份，剩余 {result.Remaining} 份。";
        });
        mine.Click += async (_, _) => await ShowMyCloudReportsAsync(Current(), status);

        buttons.Children.Add(login);
        buttons.Children.Add(register);
        buttons.Children.Add(logout);
        buttons.Children.Add(check);
        buttons.Children.Add(upload);
        buttons.Children.Add(pending);
        buttons.Children.Add(mine);
        body.Children.Add(buttons);
        body.Children.Add(status);

        if (CloudAccountSettings.Load().IsConfigured)
            status.Text = "已保存服务器地址。登录状态需点击“检查登录状态”确认。";
        return body;
    }

    TextBox LabeledInput(StackPanel body, string label, string value)
    {
        body.Children.Add(new TextBlock
        {
            Text = label, Foreground = UiTheme.TextPrimaryBrush, Margin = new Thickness(0, 4, 0, 2)
        });
        var box = new TextBox
        {
            Text = value, Height = 34, Width = 480, Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        body.Children.Add(box);
        return box;
    }

    // async void is confined to UI event plumbing; every exception is caught
    // here and reported in the status line, never to the dispatcher.
    static async void RunCloudAction(TextBlock status, Func<Task<string>> action)
    {
        try
        {
            status.Text = "正在处理…";
            status.Text = await action();
        }
        catch (CloudAccountException exception)
        {
            status.Text = "操作失败：" + exception.Message;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException
                                             or IOException or UnauthorizedAccessException
                                             or System.Net.Http.HttpRequestException)
        {
            status.Text = "操作失败：" + exception.Message;
        }
    }

    async Task UploadErrorReportToCloudAsync(Button button, CloudAccountSettings settings, TextBlock status)
    {
        if (!settings.IsConfigured)
        {
            status.Text = "请先填写 HTTPS 服务器地址。";
            return;
        }

        var snapshotSnapshot = await Task.Run(() => EncryptedErrorReport.Capture(DataDir, messages.ToArray()));
        if (!EncryptedErrorReport.IsErrorOnly(snapshotSnapshot))
        {
            status.Text = "当前没有可上传的错误记录。";
            return;
        }

        var draft = ErrorReportDraft.Create(
            typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            snapshotSnapshot.RecentMessages);

        var header = $"将上传到 {settings.ServerUrl.Trim()}\n\n";
        if (MessageBox.Show(this, header + draft.PreviewText() + "\n\n确认上传？", "预览将要上传的内容",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        button.IsEnabled = false;
        status.Text = "正在上传…";
        string? savedPath = null;
        try
        {
            var runtime = CloudAccount(settings);
            // Write to the local outbox first: a network failure or a crash
            // after this point cannot lose the record.
            savedPath = await runtime.Outbox.SaveAsync(draft);
            var receipt = await runtime.UploadAsync(draft, CancellationToken.None);
            if (savedPath is not null) await runtime.Outbox.RemoveAsync(savedPath);
            status.Text = $"上传成功：{receipt.MessageCount} 条，{receipt.ByteSize} 字节，"
                          + $"保留至 {receipt.ExpiresAt.ToLocalTime():yyyy-MM-dd}。";
        }
        catch (CloudAccountException exception)
        {
            status.Text = savedPath is null
                ? "上传失败：" + exception.Message
                : "上传失败：" + exception.Message + "。日志已保存在本机，可稍后点击“补传待上传日志”。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                             or InvalidOperationException or ArgumentException)
        {
            status.Text = "上传失败：" + exception.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    async Task ShowMyCloudReportsAsync(CloudAccountSettings settings, TextBlock status)
    {
        if (!settings.IsConfigured)
        {
            status.Text = "请先填写 HTTPS 服务器地址。";
            return;
        }

        ErrorReportSummary[] reports;
        try
        {
            reports = (await CloudAccount(settings).ListReportsAsync(CancellationToken.None)).ToArray();
        }
        catch (CloudAccountException exception)
        {
            status.Text = "读取上传记录失败：" + exception.Message;
            return;
        }

        var dialog = new Window
        {
            Title = "我的云端上传", Owner = this, Width = 560, Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = UiTheme.SurfaceBrush
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = $"共 {reports.Length} 份（最多显示最近 50 份）。删除后服务器立即移除文件。",
            TextWrapping = TextWrapping.Wrap, Foreground = UiTheme.TextSecondaryBrush,
            Margin = new Thickness(0, 0, 0, 10)
        });

        if (reports.Length == 0)
            panel.Children.Add(new TextBlock { Text = "还没有上传记录。", Foreground = UiTheme.TextPrimaryBrush });

        foreach (var report in reports)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = $"{report.ReceivedAt.ToLocalTime():yyyy-MM-dd HH:mm}  {report.MessageCount} 条  "
                       + $"{report.ByteSize / 1024.0:0.0} KB  保留至 {report.ExpiresAt.ToLocalTime():MM-dd}",
                Foreground = UiTheme.TextPrimaryBrush, VerticalAlignment = VerticalAlignment.Center
            };
            var delete = new Button { Content = "删除", Style = (Style)FindResource("Action") };
            delete.Click += async (_, _) =>
            {
                try
                {
                    await CloudAccount(settings).DeleteReportAsync(report.Id, CancellationToken.None);
                    delete.IsEnabled = false;
                    label.Text = "已删除：" + label.Text;
                }
                catch (CloudAccountException exception)
                {
                    MessageBox.Show(dialog, "删除失败：" + exception.Message, "我的云端上传");
                }
            };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(delete, 1);
            row.Children.Add(label);
            row.Children.Add(delete);
            panel.Children.Add(row);
        }

        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog();
        status.Text = "已关闭上传记录。";
    }

    string? PromptInviteCode()
    {
        var dialog = new Window
        {
            Title = "输入邀请码", Owner = this, Width = 460, Height = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Background = UiTheme.SurfaceBrush
        };
        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock
        {
            Text = "邀请码由维护者一次性发放，形如 ABCD-EFGH-…。", TextWrapping = TextWrapping.Wrap,
            Foreground = UiTheme.TextPrimaryBrush
        });
        var input = new TextBox { Height = 34, Margin = new Thickness(0, 12, 0, 12) };
        body.Children.Add(input);
        var confirm = new Button
        {
            Content = "继续", Style = (Style)FindResource("Action"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        confirm.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                MessageBox.Show(dialog, "请输入邀请码。", "邀请码");
                return;
            }
            dialog.DialogResult = true;
        };
        body.Children.Add(confirm);
        dialog.Content = body;
        input.Focus();
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
}

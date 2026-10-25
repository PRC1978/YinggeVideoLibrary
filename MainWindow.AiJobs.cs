using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    TextBlock? aiJobsSummary;
    StackPanel? aiJobsList;
    int aiJobsRefreshVersion;
    bool aiJobRetryBusy;

    void InitializeAiJobSettings(StackPanel section)
    {
        section.Children.Add(new Separator { Margin = new Thickness(0, 15, 0, 13) });
        section.Children.Add(new TextBlock
        {
            Text = "AI 作业与结果", FontWeight = FontWeights.SemiBold,
            Foreground = Tint("#293A56"), FontSize = 14
        });
        section.Children.Add(new TextBlock
        {
            Text = "中断或失败的请求只恢复状态，不会自动重发。重新发送需人工确认，并重新核验当前接口与预算；返回结果只保存供查看，不会直接修改分类或资料。",
            TextWrapping = TextWrapping.Wrap, Foreground = Tint("#7687A0"), FontSize = 11,
            Margin = new Thickness(0, 5, 0, 9)
        });
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        aiJobsSummary = new TextBlock
        {
            Text = "正在读取 AI 作业…", Foreground = Tint("#526785"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        };
        toolbar.Children.Add(aiJobsSummary);
        var refresh = JobButton("刷新作业");
        refresh.Click += async (_, _) => await RefreshAiJobsAsync();
        toolbar.Children.Add(refresh);
        section.Children.Add(toolbar);
        aiJobsList = new StackPanel();
        section.Children.Add(new ScrollViewer
        {
            Content = aiJobsList, MaxHeight = 410,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        _ = RefreshAiJobsAsync();
    }

    Button JobButton(string text) => new()
    {
        Content = text, MinHeight = 30, MinWidth = 70,
        Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 8, 0),
        Style = (Style)FindResource("Action")
    };

    internal async Task RefreshAiJobsAsync()
    {
        if (aiJobsSummary is null || aiJobsList is null) return;
        var version = Interlocked.Increment(ref aiJobsRefreshVersion);
        aiJobsSummary.Text = "正在读取 AI 作业…";
        try
        {
            var snapshot = await Task.Run(() =>
            {
                // Only status recovery occurs here. No saved request is ever dispatched.
                var interrupted = AiRequestQueue.RecoverInterrupted(DataDir);
                return (interrupted, jobs: AiJobStore.List(DataDir));
            });
            if (version != aiJobsRefreshVersion) return;
            var jobs = snapshot.jobs;
            var unresolved = jobs.Count(job => job.Status is AiJobStore.Interrupted or AiJobStore.Failed);
            var running = jobs.Count(job => job.Status == AiJobStore.InFlight);
            var ready = jobs.Count(job => job.Status is AiJobStore.Created or AiJobStore.ReadyToRetry);
            aiJobsSummary.Text =
                $"共 {jobs.Count} 项 · 待人工处理 {unresolved} · 进行中 {running} · 待发送 {ready}" +
                (snapshot.interrupted > 0 ? $" · 本次识别中断 {snapshot.interrupted}" : "");
            aiJobsList.Children.Clear();
            foreach (var job in jobs.Take(20)) aiJobsList.Children.Add(BuildAiJobRow(job));
            if (jobs.Count > 20) aiJobsList.Children.Add(new TextBlock
            {
                Text = $"仅显示最近 20 项；完整记录位于数据目录的 ai-jobs 文件夹。",
                Foreground = Tint("#75859E"), Margin = new Thickness(0, 7, 0, 3),
                TextWrapping = TextWrapping.Wrap
            });
            if (jobs.Count == 0) aiJobsList.Children.Add(new TextBlock
            {
                Text = "暂无 AI 请求记录。", Foreground = Tint("#8290A5"),
                Margin = new Thickness(0, 6, 0, 4)
            });
        }
        catch (Exception ex)
        {
            if (version != aiJobsRefreshVersion) return;
            aiJobsSummary.Text = "AI 作业记录无法读取，已停止重试：" + ex.Message;
            aiJobsList.Children.Clear();
        }
    }

    FrameworkElement BuildAiJobRow(AiJobStore.Job job)
    {
        var body = new StackPanel();
        var title = new TextBlock
        {
            Text = $"{JobKind(job.Kind)} · {JobStatus(job.Status)} · {job.CreatedUtc.LocalDateTime:MM-dd HH:mm}",
            FontWeight = FontWeights.SemiBold, Foreground = Tint("#304566"),
            TextWrapping = TextWrapping.Wrap, ToolTip = "作业编号：" + job.Id
        };
        body.Children.Add(title);
        body.Children.Add(new TextBlock
        {
            Text = $"编号 {job.Id[..8]} · 尝试 {job.AttemptCount} 次" +
                (job.LastErrorCode.Length > 0 ? " · " + JobError(job.LastErrorCode) : ""),
            Foreground = Tint("#75859E"), FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 7)
        });
        var actions = new WrapPanel();
        if (!string.IsNullOrWhiteSpace(job.ProtectedRequest) &&
            job.Status is (AiJobStore.Interrupted or AiJobStore.Failed or AiJobStore.Canceled or AiJobStore.ReadyToRetry or AiJobStore.Created))
        {
            var retry = JobButton(job.Status is AiJobStore.Created or AiJobStore.ReadyToRetry ? "发送" : "人工重试");
            retry.Click += async (_, _) => await RetryAiJobAsync(job.Id);
            actions.Children.Add(retry);
        }
        if (job.Status is AiJobStore.Created or AiJobStore.ReadyToRetry or AiJobStore.InFlight)
        {
            var cancel = JobButton("取消");
            cancel.Click += async (_, _) => await CancelAiJobAsync(job.Id);
            actions.Children.Add(cancel);
        }
        if (actions.Children.Count > 0) body.Children.Add(actions);
        if (job.Status == AiJobStore.Completed && !string.IsNullOrWhiteSpace(job.ResultJson))
        {
            var result = new Expander
            {
                Header = "查看已保存的返回内容", Foreground = Tint("#4C6BDC"),
                Margin = new Thickness(0, 4, 0, 0)
            };
            result.Expanded += (_, _) =>
            {
                if (result.Content is not null) return;
                result.Content = new TextBox
                {
                    Text = job.ResultJson, IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 190, Margin = new Thickness(0, 6, 0, 0)
                };
            };
            body.Children.Add(result);
        }
        return new Border
        {
            Child = body, CornerRadius = new CornerRadius(10),
            BorderBrush = Tint("#E1E8F3"), BorderThickness = new Thickness(1),
            Background = Brushes.White, Padding = new Thickness(11),
            Margin = new Thickness(0, 0, 0, 8)
        };
    }

    async Task CancelAiJobAsync(string jobId)
    {
        try
        {
            var canceled = AiRequestQueue.TryCancelActive(jobId) ||
                await Task.Run(() => AiRequestQueue.TryCancelPending(DataDir, jobId));
            if (!canceled) MessageBox.Show(this,
                "此作业已结束，或正在另一进程执行。请刷新后查看状态。",
                "AI 作业", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAiJobsAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "取消 AI 作业失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    async Task RetryAiJobAsync(string jobId)
    {
        if (aiJobRetryBusy) return;
        aiJobRetryBusy = true;
        try
        {
            if (!AiRequestQueue.CanUse(config))
                throw new InvalidOperationException("请先保存有效的 AI 接口设置；云端接口还需要 API Key。");
            if (!string.Equals(AiEndpointBox.Text.Trim(), config.AiEndpoint, StringComparison.Ordinal) ||
                !string.Equals(AiModelBox.Text.Trim(), config.AiModel, StringComparison.Ordinal) ||
                !string.Equals(AiKeyBox.Password.Trim(), config.AiKey, StringComparison.Ordinal))
                throw new InvalidOperationException("AI 接口设置尚未保存，请先保存设置再重试。");
            var job = await Task.Run(() => AiJobStore.Get(DataDir, jobId));
            if (job.Status is not (AiJobStore.Created or AiJobStore.ReadyToRetry or
                AiJobStore.Interrupted or AiJobStore.Failed or AiJobStore.Canceled))
                throw new InvalidOperationException("此作业当前无需重试，请刷新状态。");
            string requestJson;
            try { requestJson = await Task.Run(() => AiJobStore.ReadProtectedRequest(job)); }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException(
                    "Windows 当前账户无法解密此作业请求，可能来自另一账户或备份。请重新创建 AI 作业；不会自动发送。", ex);
            }
            var endpoint = GetAiChatEndpoint();
            if (AiJobStore.RequestHash(endpoint, requestJson) != job.RequestSha256)
                throw new InvalidOperationException("当前接口或请求内容与原作业不同，请重新创建 AI 作业。");
            var estimatedUsd = AiRequestQueue.EstimateReservedUsd(config, requestJson);
            if (!double.IsFinite(estimatedUsd) || estimatedUsd < 0)
                throw new InvalidOperationException("AI 费率无效，请先检查并保存设置。");
            var ledger = await Task.Run(() => AiRequestQueue.LoadBudgetLedger(DataDir));
            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var used = ledger.Day == today ? ledger.EstimatedUsd : 0;
            if (config.AiDailyBudgetUsd > 0 && used + estimatedUsd > config.AiDailyBudgetUsd)
                throw new InvalidOperationException("今日 AI 预算不足，未发送请求。请先检查费用上限与费率。");
            var cloud = !new Uri(endpoint).IsLoopback;
            var confirmation = cloud
                ? $"原请求可能已被服务商处理，重试可能重复计费。\n本次预留费用约 ${estimatedUsd:0.######}；今日已计约 ${used:0.######}。\n\n确认重新发送此作业？"
                : "确认向当前本机模型接口重新发送此作业？";
            if (MessageBox.Show(this, confirmation, "AI 作业 · 人工重试",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (job.Status is AiJobStore.Interrupted or AiJobStore.Failed or AiJobStore.Canceled)
                await Task.Run(() => AiRequestQueue.PrepareStoredRetry(DataDir, jobId, endpoint, requestJson));
            using var result = await AiRequestQueue.SendStoredJobAsync(
                config, endpoint, requestJson, DataDir, jobId);
            MessageBox.Show(this, "AI 返回结果已保存到作业记录。请查看内容并另行审核；此操作没有修改视频资料。",
                "AI 作业完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "AI 作业未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            aiJobRetryBusy = false;
            RefreshAiUsageStatus();
            await RefreshAiJobsAsync();
        }
    }

    static string JobKind(string kind) => kind switch
    {
        "classification" => "AI 分类", "tags" => "AI 标签", "review" => "AI 审核",
        _ => "AI 请求"
    };

    static string JobStatus(string status) => status switch
    {
        AiJobStore.Created => "待发送", AiJobStore.ReadyToRetry => "待重试发送",
        AiJobStore.InFlight => "进行中", AiJobStore.Interrupted => "上次中断，结果未确认",
        AiJobStore.Failed => "失败，待人工处理", AiJobStore.Canceled => "已取消",
        AiJobStore.Completed => "结果已保存", _ => "未知状态"
    };

    static string JobError(string code) => code switch
    {
        "process-interrupted" => "上次退出时结果未确认",
        "policy-blocked" => "设置或预算已阻止发送",
        "transport-error" => "网络或服务错误",
        "request-timeout" => "请求超时，可人工重试",
        "invalid-response" => "服务返回格式错误",
        "response-too-large" => "服务响应过大，未保存结果",
        "response-contained-secret" => "响应含接口凭据，已拒绝保存",
        "request-canceled" or "user-canceled" => "用户取消",
        "relocated-request" => "视频库已迁移，请重新创建 AI 请求",
        "invalid-data" => "本地记录异常",
        _ => "请求未完成"
    };

    internal bool VerifyAiJobsUiForDiagnostics() =>
        aiJobsSummary is not null && aiJobsList is not null &&
        settingsSections.TryGetValue("ai", out var section) &&
        section.Content is StackPanel stack &&
        stack.Children.OfType<StackPanel>().Any(child => child.Children
            .OfType<WrapPanel>().Any(toolbar => toolbar.Children.Contains(aiJobsSummary)));
}

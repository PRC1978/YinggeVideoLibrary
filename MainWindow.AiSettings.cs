using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using StackPanel = System.Windows.Controls.StackPanel;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    TextBox? aiBudgetBox;
    TextBox? aiInputRateBox;
    TextBox? aiOutputRateBox;
    TextBox? aiLocalIntervalBox;
    TextBox? aiCloudIntervalBox;
    TextBox? aiLocalConcurrencyBox;
    TextBox? aiCloudConcurrencyBox;
    TextBox? aiLocalRetryBox;
    TextBox? aiCloudRetryBox;
    TextBlock? aiUsageStatus;

    void InitializeAiQueueSettings()
    {
        if (SettingsPanel.Child is not StackPanel panel) return;
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        section.Children.Add(new TextBlock { Text = "AI 请求队列与费用估算", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 7, 0, 8) });
        aiBudgetBox = AddAiNumber(section, "每日费用上限（美元；0 为不限）", config.AiDailyBudgetUsd);
        aiInputRateBox = AddAiNumber(section, "输入每百万 Token 费率（美元）", config.AiInputUsdPerMillion);
        aiOutputRateBox = AddAiNumber(section, "输出每百万 Token 费率（美元）", config.AiOutputUsdPerMillion);
        section.Children.Add(new TextBlock { Text = "本机模型", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 9, 0, 2) });
        aiLocalIntervalBox = AddAiNumber(section, "本机请求间隔（毫秒）",
            config.AiLocalRequestIntervalMs > 0 ? config.AiLocalRequestIntervalMs : config.AiRequestIntervalMs);
        aiLocalConcurrencyBox = AddAiNumber(section, "本机同时请求数（1–4）", config.AiLocalConcurrency);
        aiLocalRetryBox = AddAiNumber(section, "本机临时错误重试次数（0–5）", config.AiLocalRetryCount);
        section.Children.Add(new TextBlock { Text = "云端接口", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 9, 0, 2) });
        aiCloudIntervalBox = AddAiNumber(section, "云端请求间隔（毫秒）",
            config.AiCloudRequestIntervalMs > 0 ? config.AiCloudRequestIntervalMs : config.AiRequestIntervalMs);
        aiCloudConcurrencyBox = AddAiNumber(section, "云端同时请求数（1–4）", config.AiCloudConcurrency);
        aiCloudRetryBox = AddAiNumber(section, "云端临时错误重试次数（0–5）", config.AiCloudRetryCount);
        aiUsageStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11,
            Margin = new Thickness(0, 8, 0, 4) };
        section.Children.Add(aiUsageStatus);
        RefreshAiUsageStatus();
        section.Children.Add(new TextBlock { Text = "本机接口可填 http://127.0.0.1:端口/v1，API Key 可留空。本机和云端各自排队、限速与重试；费用预算仍共用，按设置费率估算，并非服务商账单。服务返回可信 Token 用量时按实核算；超时、429、取消或缺少用量时保留保守估算。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 4) });
        InitializeAiJobSettings(section);
        var save = panel.Children.OfType<System.Windows.Controls.Button>().FirstOrDefault(button => Equals(button.Content, "保存 AI 设置"));
        panel.Children.Insert(save is null ? Math.Max(0, panel.Children.Count - 2) : panel.Children.IndexOf(save), section);
    }

    static TextBox AddAiNumber(StackPanel parent, string label, double value)
    {
        parent.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 5, 0, 2) });
        var box = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Height = 30, Width = 170, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        parent.Children.Add(box);
        return box;
    }

    void ReadAiQueueSettings()
    {
        static double Number(TextBox? box, double fallback) => double.TryParse(box?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : fallback;
        config.AiDailyBudgetUsd = Number(aiBudgetBox, config.AiDailyBudgetUsd);
        config.AiInputUsdPerMillion = Number(aiInputRateBox, config.AiInputUsdPerMillion);
        config.AiOutputUsdPerMillion = Number(aiOutputRateBox, config.AiOutputUsdPerMillion);
        config.AiLocalRequestIntervalMs = (int)Math.Clamp(Number(aiLocalIntervalBox,
            config.AiLocalRequestIntervalMs > 0 ? config.AiLocalRequestIntervalMs : config.AiRequestIntervalMs), 500, 60000);
        config.AiCloudRequestIntervalMs = (int)Math.Clamp(Number(aiCloudIntervalBox,
            config.AiCloudRequestIntervalMs > 0 ? config.AiCloudRequestIntervalMs : config.AiRequestIntervalMs), 500, 60000);
        config.AiLocalConcurrency = (int)Math.Clamp(Number(aiLocalConcurrencyBox, config.AiLocalConcurrency), 1, 4);
        config.AiCloudConcurrency = (int)Math.Clamp(Number(aiCloudConcurrencyBox, config.AiCloudConcurrency), 1, 4);
        config.AiLocalRetryCount = (int)Math.Clamp(Number(aiLocalRetryBox, config.AiLocalRetryCount), 0, 5);
        config.AiCloudRetryCount = (int)Math.Clamp(Number(aiCloudRetryBox, config.AiCloudRetryCount), 0, 5);
        RefreshAiUsageStatus();
        _ = RefreshAiJobsAsync();
    }

    void RefreshAiUsageStatus()
    {
        if (aiUsageStatus is null) return;
        try
        {
            var ledger = AiRequestQueue.LoadBudgetLedger(DataDir);
            if (ledger.Day != DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            {
                aiUsageStatus.Text = "今日尚无 AI 请求。";
                return;
            }
            var remaining = config.AiDailyBudgetUsd > 0
                ? $" · 剩余预算约 ${Math.Max(0, config.AiDailyBudgetUsd - ledger.EstimatedUsd):0.######}"
                : " · 未设置每日上限";
            var reportedTokens = ledger.ReportedUsageCount > 0 && ledger.InputTokens == 0
                ? "服务端 Token 明细：旧记录不可用"
                : $"服务端用量：输入 {ledger.InputTokens:N0} / 输出 {ledger.OutputTokens:N0} Token";
            var estimatedTokens = ledger.UnverifiedUsd > 0 && ledger.UnverifiedInputEstimateTokens == 0
                ? "未核实 Token 明细：旧记录不可用"
                : $"未核实保守估算：输入 {ledger.UnverifiedInputEstimateTokens:N0} / 已预留输出上限 {ledger.UnverifiedOutputLimitTokens:N0} Token";
            aiUsageStatus.Text =
                $"今日 {ledger.AttemptCount} 次发送尝试，其中 {ledger.ReportedUsageCount} 次有服务端用量" +
                $" · 已计预算约 ${ledger.EstimatedUsd:0.######}" +
                $"（服务端 Token 按设置费率折算 ${ledger.ReportedUsageUsd:0.######}；" +
                $"未核实预留 ${ledger.UnverifiedUsd:0.######}）" +
                $" · {reportedTokens} · {estimatedTokens}{remaining}";
        }
        catch (Exception ex) { aiUsageStatus.Text = "AI 用量记录无法读取，已暂停请求：" + ex.Message; }
    }
}

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
    TextBox? aiIntervalBox;

    void InitializeAiQueueSettings()
    {
        if (SettingsPanel.Child is not StackPanel panel) return;
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        section.Children.Add(new TextBlock { Text = "AI 请求队列与费用估算", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 7, 0, 8) });
        aiBudgetBox = AddAiNumber(section, "每日费用上限（美元；0 为不限）", config.AiDailyBudgetUsd);
        aiInputRateBox = AddAiNumber(section, "输入每百万 Token 费率（美元）", config.AiInputUsdPerMillion);
        aiOutputRateBox = AddAiNumber(section, "输出每百万 Token 费率（美元）", config.AiOutputUsdPerMillion);
        aiIntervalBox = AddAiNumber(section, "请求最小间隔（毫秒）", config.AiRequestIntervalMs);
        section.Children.Add(new TextBlock { Text = "本机接口可填 http://127.0.0.1:端口/v1，API Key 可留空。费用按你填写的费率估算，实际账单以服务商为准。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 4) });
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
        config.AiRequestIntervalMs = (int)Math.Clamp(Number(aiIntervalBox, config.AiRequestIntervalMs), 500, 60000);
    }
}

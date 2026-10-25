using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    StackPanel? sourceHealthRows;
    TextBlock? sourceHealthStatus;
    TextBlock? sourcePaidBudget;
    long sourceHealthRevision;

    void InitializeSourceDiagnostics(StackPanel page, StackPanel sourceToggles)
    {
        var section = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(215, 225, 248)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16), Margin = new Thickness(0, 8, 0, 16)
        };
        var body = new StackPanel();
        section.Child = body;
        body.Children.Add(new TextBlock { Text = "来源诊断", FontSize = 17, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = "只读取本机的来源开关、冷却与请求上限记录；刷新和导出不会联网，也不会消耗付费额度。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 10)
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = new Button { Content = "刷新本地状态", Style = (Style)FindResource("Action") };
        refresh.Click += (_, _) => RefreshSourceDiagnostics();
        actions.Children.Add(refresh);
        var export = new Button { Content = "导出脱敏诊断", Style = (Style)FindResource("Action"),
            Margin = new Thickness(8, 0, 0, 0) };
        export.Click += async (_, _) => await ExportSourceDiagnosticsAsync();
        actions.Children.Add(export);
        body.Children.Add(actions);
        sourceHealthStatus = new TextBlock { Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 8) };
        body.Children.Add(sourceHealthStatus);
        var scroll = new ScrollViewer { MaxHeight = 355, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        sourceHealthRows = new StackPanel();
        scroll.Content = sourceHealthRows;
        body.Children.Add(scroll);
        sourcePaidBudget = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(76, 91, 116)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        body.Children.Add(sourcePaidBudget);
        page.Children.Insert(page.Children.IndexOf(sourceToggles) + 1, section);
        ScrapePanel.IsVisibleChanged += (_, _) => { if (ScrapePanel.IsVisible) RefreshSourceDiagnostics(); };
        RefreshSourceDiagnostics();
    }

    async void RefreshSourceDiagnostics()
    {
        if (sourceHealthRows is null || sourceHealthStatus is null || sourcePaidBudget is null) return;
        var revision = Interlocked.Increment(ref sourceHealthRevision);
        sourceHealthStatus.Text = "正在读取本地来源状态…";
        SourceHealthDiagnostics.Snapshot snapshot;
        try { snapshot = await Task.Run(() => SourceHealthDiagnostics.Capture(DataDir, config)); }
        catch
        {
            if (revision == Interlocked.Read(ref sourceHealthRevision))
                sourceHealthStatus.Text = "本地来源状态暂不可读。请检查数据目录访问权限。";
            return;
        }
        if (isClosing || revision != Interlocked.Read(ref sourceHealthRevision)) return;
        sourceHealthStatus.Text = $"{snapshot.LocalRecordStatus} · 解析器随应用版本 {snapshot.ApplicationVersion} 发布 · {snapshot.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} 刷新";
        sourceHealthRows.Children.Clear();
        foreach (var source in snapshot.Sources)
        {
            var row = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(227, 233, 244)),
                BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(2, 9, 2, 9) };
            var content = new StackPanel();
            row.Child = content;
            content.Children.Add(new TextBlock
            {
                Text = $"{source.Name}   {source.Status}", FontWeight = FontWeights.SemiBold,
                Foreground = source.Status == "冷却中" || source.Status == "付费请求已阻止"
                    ? new SolidColorBrush(Color.FromRgb(163, 93, 45)) : new SolidColorBrush(Color.FromRgb(48, 70, 115))
            });
            var cooldown = source.CooldownUntilUtc is { } until
                ? $" · 冷却至 {until.ToLocalTime():MM-dd HH:mm:ss}" : "";
            content.Children.Add(new TextBlock
            {
                Text = $"开关：{(source.Enabled ? "开启" : "关闭")} · {source.Mode}{cooldown} · 最近失败：{source.LastFailureCategory} · 解析版本：{source.ParserRevision}",
                Foreground = Brushes.SlateGray, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
            sourceHealthRows.Children.Add(row);
        }
        var budget = snapshot.JavinfoBudget;
        sourcePaidBudget.Text = budget.ReservedToday is { } reserved
            ? $"Javinfo 可选付费预算：{budget.Status}；UTC {budget.UtcDay} 已预留 {reserved}/{budget.DailyLimit} 次，剩余 {Math.Max(0, budget.DailyLimit - reserved)} 次。实际计费以来源账单为准。"
            : $"Javinfo 可选付费预算：{budget.Status}；为避免超额，付费请求已阻止。";
    }

    async Task ExportSourceDiagnosticsAsync()
    {
        SourceHealthDiagnostics.Snapshot snapshot;
        try { snapshot = await Task.Run(() => SourceHealthDiagnostics.Capture(DataDir, config)); }
        catch
        {
            MessageBox.Show(this, "本地来源状态暂不可读，无法导出。", "来源诊断", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = new SaveFileDialog { Title = "导出脱敏来源诊断", Filter = "JSON 文件 (*.json)|*.json",
            DefaultExt = ".json", AddExtension = true,
            FileName = $"yingge-source-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var json = SourceHealthDiagnostics.ToRedactedJson(snapshot);
            await File.WriteAllTextAsync(dialog.FileName, json, new UTF8Encoding(false));
            MessageBox.Show(this, "诊断已导出。仅含来源状态、错误类别与预算计数；不含密钥、Cookie、本地路径或视频标题。",
                "来源诊断", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "导出失败。请换一个可写入的位置。", "来源诊断", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

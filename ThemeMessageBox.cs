using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MessageBoxOptions = System.Windows.MessageBoxOptions;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;

namespace YinggeVideoLibrary;

// Shared themed replacement for the desktop application's legacy WPF
// MessageBox calls. It preserves the MessageBoxResult contract, including
// Yes/No/Cancel, so changing the presentation does not change decisions.
internal static class ThemeMessageBox
{
    internal static MessageBoxResult Show(string messageBoxText, string caption = "映格视频库",
        MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None) =>
        ShowCore(null, messageBoxText, caption, button, icon, defaultResult, options);

    internal static MessageBoxResult Show(Window? owner, string messageBoxText,
        string caption = "映格视频库", MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None) =>
        ShowCore(owner, messageBoxText, caption, button, icon, defaultResult, options);

    static MessageBoxResult ShowCore(Window? owner, string text, string caption,
        MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult,
        MessageBoxOptions options)
    {
        if (System.Windows.Application.Current is { } app && !app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => ShowCore(owner, text, caption,
                buttons, icon, defaultResult, options));
        if (options != MessageBoxOptions.None)
            throw new NotSupportedException("主题对话框不支持特殊系统消息框选项。");
        var theme = UiThemeState.Instance;
        var choice = buttons switch
        {
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK
        };
        var window = new Window
        {
            Title = caption, Width = 500, MinWidth = 380, SizeToContent = SizeToContent.Height,
            MaxHeight = SystemParameters.WorkArea.Height * .82,
            WindowStartupLocation = owner?.IsVisible == true
                ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = owner is null,
            WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Transparent, Foreground = theme.TextPrimaryBrush,
            FontFamily = theme.BodyFontFamily, FontSize = 14
        };
        if (owner is not null && owner.IsVisible) window.Owner = owner;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        var title = new TextBlock
        {
            Text = caption, FontSize = 19, FontWeight = FontWeights.SemiBold,
            Foreground = theme.TextPrimaryBrush, Margin = new Thickness(0, 0, 34, 0)
        };
        heading.Children.Add(title);
        var close = new Button
        {
            Content = "×", Width = 28, Height = 28, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent, Foreground = theme.TextSecondaryBrush,
            BorderThickness = new Thickness(0), FontSize = 20, ToolTip = "关闭对话框"
        };
        close.Click += (_, _) => window.Close();
        heading.Children.Add(close);
        root.Children.Add(heading);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var warning = icon is MessageBoxImage.Warning or MessageBoxImage.Error or MessageBoxImage.Question;
        var symbol = new TextBlock
        {
            Text = icon switch
            {
                MessageBoxImage.Warning => "!", MessageBoxImage.Error => "×",
                MessageBoxImage.Question => "?", _ => "i"
            },
            Width = 30, Height = 30, TextAlignment = TextAlignment.Center,
            FontSize = 19, FontWeight = FontWeights.Bold,
            Foreground = warning ? theme.WarningBrush : theme.AccentBrush,
            VerticalAlignment = VerticalAlignment.Top
        };
        body.Children.Add(symbol);
        var content = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap,
            Foreground = theme.TextPrimaryBrush, LineHeight = 22
        };
        System.Windows.Automation.AutomationProperties.SetName(content, text);
        Grid.SetColumn(content, 1);
        body.Children.Add(content);
        var scroll = new ScrollViewer
        {
            Content = body, MaxHeight = SystemParameters.WorkArea.Height * .55,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 22)
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        var entries = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { ("取消", MessageBoxResult.Cancel), ("确定", MessageBoxResult.OK) },
            MessageBoxButton.YesNo => new[] { ("否", MessageBoxResult.No), ("是", MessageBoxResult.Yes) },
            MessageBoxButton.YesNoCancel => new[] { ("取消", MessageBoxResult.Cancel), ("否", MessageBoxResult.No), ("是", MessageBoxResult.Yes) },
            _ => new[] { ("确定", MessageBoxResult.OK) }
        };
        foreach (var (label, result) in entries)
        {
            var button = new Button
            {
                Content = label, MinWidth = 82, Height = 36, Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(14, 0, 14, 0),
                Background = result is MessageBoxResult.OK or MessageBoxResult.Yes
                    ? theme.AccentBrush : theme.SurfaceBrush,
                Foreground = result is MessageBoxResult.OK or MessageBoxResult.Yes
                    ? theme.AccentForegroundBrush : theme.TextPrimaryBrush,
                BorderBrush = theme.BorderBrushToken, BorderThickness = new Thickness(1),
                IsDefault = result == (defaultResult == MessageBoxResult.None ? entries[^1].Item2 : defaultResult),
                IsCancel = result == MessageBoxResult.Cancel ||
                    buttons == MessageBoxButton.OK && result == MessageBoxResult.OK
            };
            button.Click += (_, _) => { choice = result; window.Close(); };
            actions.Children.Add(button);
        }
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        var shell = new Grid { Margin = new Thickness(16) };
        shell.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(16), Padding = new Thickness(24),
            Background = theme.PageBackgroundBrush, BorderBrush = theme.BorderBrushToken,
            BorderThickness = new Thickness(1), Child = root,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 28, ShadowDepth = 8, Opacity = .38
            }
        });
        window.Content = shell;
        window.ShowDialog();
        return choice;
    }
}

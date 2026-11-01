using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace YinggeVideoLibrary;

// A TextBox can look like a broken empty control when it has no hint text.
// This keeps the hint as an overlay so the input itself stays a normal TextBox
// (same focus, IME and context menu behaviour as before).
internal static class UiTextInput
{
    internal static Grid WithPlaceholder(TextBox box, string hint, Thickness? hintMargin = null)
    {
        var grid = new Grid();
        grid.Children.Add(box);
        var label = new TextBlock
        {
            Text = hint,
            Foreground = new SolidColorBrush(Color.FromRgb(169, 181, 198)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            Margin = hintMargin ?? new Thickness(12, 0, 0, 0)
        };
        grid.Children.Add(label);
        void Sync() => label.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        box.Loaded += (_, _) => Sync();
        return grid;
    }
}

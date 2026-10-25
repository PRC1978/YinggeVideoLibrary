using System.Windows;
using System.Windows.Controls;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    // The complete category path and tag list can be long. A recycled card
    // needs them only while its tooltip is open, not on every scroll rebind.
    void CardSummaryToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not TextBlock { DataContext: VideoItem video } block) return;
        block.ToolTip = Equals(block.Tag, "category") ? video.CategoryPathsText : video.TagsPreviewText;
    }

    void CardSummaryContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBlock block) return;
        var placeholder = Equals(block.Tag, "category") ? "查看完整分类路径" : "查看全部标签";
        if (!Equals(block.ToolTip, placeholder)) block.ToolTip = placeholder;
    }
}

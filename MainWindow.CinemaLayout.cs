using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    void SelectCinemaPoster(int index)
    {
        for (var i = 0; i < CinemaBeltCards.Items.Count; i++)
        {
            if (CinemaBeltCards.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement item)
                continue;
            var selected = i == index;
            item.RenderTransformOrigin = new System.Windows.Point(.5, .5);
            item.RenderTransform = new ScaleTransform(selected ? 1.045 : 1, selected ? 1.045 : 1);
            item.Effect = selected ? new DropShadowEffect
            {
                Color = Color.FromRgb(231, 79, 105), BlurRadius = 18,
                ShadowDepth = 0, Opacity = .55
            } : null;
            System.Windows.Controls.Panel.SetZIndex(item, selected ? 1 : 0);
        }
    }

    void CinemaFilmstripClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: VideoItem video }) return;
        var index = pageItems.IndexOf(video);
        if (index < 0) return;
        SelectCinemaPoster(index);
        var pitch = UiTheme.Num(UiTheme.CardWidth) + UiTheme.Num(UiTheme.CardGap);
        var target = Math.Max(0, index * pitch - (CinemaBeltScroll.ViewportWidth - pitch) / 2);
        CinemaBeltScroll.ScrollToHorizontalOffset(target);
    }
}

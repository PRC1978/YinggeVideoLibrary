using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace YinggeVideoLibrary;

/// <summary>Retains page card visuals and rebinds them as the page changes.</summary>
public sealed class RecycledCardWrap : WrapPanel
{
    public static readonly DependencyProperty CellWidthProperty = DependencyProperty.Register(
        nameof(CellWidth), typeof(double), typeof(RecycledCardWrap),
        new PropertyMetadata(170d));

    public double CellWidth
    {
        get => (double)GetValue(CellWidthProperty);
        private set => SetValue(CellWidthProperty, value);
    }

    public static readonly DependencyProperty PosterHeightProperty = DependencyProperty.Register(
        nameof(PosterHeight), typeof(double), typeof(RecycledCardWrap),
        new PropertyMetadata(255d));

    public double PosterHeight
    {
        get => (double)GetValue(PosterHeightProperty);
        private set => SetValue(PosterHeightProperty, value);
    }

    public RecycledCardWrap()
    {
        SizeChanged += (_, _) => UpdateCellWidth();
        Loaded += (_, _) => UpdateCellWidth();
    }

    public void UpdateCellWidth()
    {
        var available = ActualWidth;
        if (!double.IsFinite(available) || available <= 0) return;
        var gap = UiTheme.Num(UiTheme.CardGap);
        var minimum = UiTheme.Num(UiTheme.CardWidth);
        if (UiTheme.IsClassicMode)
        {
            if (Math.Abs(CellWidth - minimum) > 0.5) CellWidth = minimum;
            var classicHeight = UiTheme.Num(UiTheme.CardHeight);
            if (Math.Abs(PosterHeight - classicHeight) > 0.5) PosterHeight = classicHeight;
            return;
        }
        var columns = Math.Max(1, (int)Math.Floor((available + gap) / (minimum + gap)));
        var width = Math.Min(240, Math.Max(minimum, (available - columns * gap) / columns));
        if (Math.Abs(CellWidth - width) > 0.5) CellWidth = width;
        var posterHeight = Math.Round(width * 1.5);
        if (Math.Abs(PosterHeight - posterHeight) > 0.5) PosterHeight = posterHeight;
    }
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IList<VideoItem>), typeof(RecycledCardWrap),
        new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty CardTemplateProperty = DependencyProperty.Register(
        nameof(CardTemplate), typeof(DataTemplate), typeof(RecycledCardWrap),
        new PropertyMetadata(null, (sender, _) => ((RecycledCardWrap)sender).SyncCards()));

    public IList<VideoItem>? ItemsSource
    {
        get => (IList<VideoItem>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public DataTemplate? CardTemplate
    {
        get => (DataTemplate?)GetValue(CardTemplateProperty);
        set => SetValue(CardTemplateProperty, value);
    }

    public int VisibleCardCount => ItemsSource?.Count ?? 0;

    static void OnItemsSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var panel = (RecycledCardWrap)sender;
        if (args.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= panel.OnCollectionChanged;
        if (args.NewValue is INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += panel.OnCollectionChanged;
        panel.SyncCards();
    }

    void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        var count = ItemsSource?.Count ?? 0;
        if (CardTemplate is null) return;
        if (args.Action == NotifyCollectionChangedAction.Add && args.NewStartingIndex == count - args.NewItems!.Count)
        {
            for (var i = args.NewStartingIndex; i < count; i++) BindCard(i);
            return;
        }
        if (args.Action == NotifyCollectionChangedAction.Replace &&
            args.NewStartingIndex >= 0 && args.NewItems is not null)
        {
            for (var i = args.NewStartingIndex; i < args.NewStartingIndex + args.NewItems.Count; i++)
                BindCard(i);
            return;
        }
        if (args.Action == NotifyCollectionChangedAction.Remove && args.OldStartingIndex == count)
        {
            for (var i = count; i < count + args.OldItems!.Count && i < Children.Count; i++) HideCard(i);
            return;
        }
        SyncCards();
    }

    void BindCard(int index)
    {
        while (Children.Count <= index)
        {
            if (CardTemplate!.LoadContent() is not FrameworkElement newCard)
                throw new InvalidOperationException("视频卡片模板没有可显示的根元素");
            Children.Add(newCard);
        }
        var card = (FrameworkElement)Children[index];
        var video = ItemsSource![index];
        if (!ReferenceEquals(card.DataContext, video)) card.DataContext = video;
        if (card.Visibility != Visibility.Visible) card.Visibility = Visibility.Visible;
    }

    void HideCard(int index)
    {
        var card = (FrameworkElement)Children[index];
        if (card.DataContext is not null) card.DataContext = null;
        if (card.Visibility != Visibility.Collapsed) card.Visibility = Visibility.Collapsed;
    }

    void SyncCards()
    {
        var count = ItemsSource?.Count ?? 0;
        if (CardTemplate is null) return;
        for (var i = 0; i < count; i++) BindCard(i);
        for (var i = count; i < Children.Count; i++) HideCard(i);
    }
}

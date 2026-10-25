using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace YinggeVideoLibrary;

/// <summary>
/// Reuses each card's visual tree when the outer virtualized row is recycled.
/// An ItemsControl would discard and recreate every card when its row source
/// changes, even though VirtualizingStackPanel recycles the row container.
/// </summary>
public sealed class RecycledCardRow : StackPanel
{
    static int diagnosticsEnabled;
    static long diagnosticRowsCreated;
    static long diagnosticCardsCreated;
    static long diagnosticRebinds;
    static long diagnosticBoundCards;
    static long diagnosticUpdateTicks;
    static long diagnosticMeasureCalls;
    static long diagnosticMeasureTicks;
    static long diagnosticArrangeCalls;
    static long diagnosticArrangeTicks;

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IReadOnlyList<VideoItem>), typeof(RecycledCardRow),
        new PropertyMetadata(null, OnItemsChanged));

    public static readonly DependencyProperty CardTemplateProperty = DependencyProperty.Register(
        nameof(CardTemplate), typeof(DataTemplate), typeof(RecycledCardRow),
        new PropertyMetadata(null, OnItemsChanged));

    public IReadOnlyList<VideoItem>? Items
    {
        get => (IReadOnlyList<VideoItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public DataTemplate? CardTemplate
    {
        get => (DataTemplate?)GetValue(CardTemplateProperty);
        set => SetValue(CardTemplateProperty, value);
    }

    public RecycledCardRow()
    {
        Orientation = System.Windows.Controls.Orientation.Horizontal;
        if (Volatile.Read(ref diagnosticsEnabled) != 0) Interlocked.Increment(ref diagnosticRowsCreated);
    }

    internal static void BeginScrollDiagnostic()
    {
        Volatile.Write(ref diagnosticsEnabled, 0);
        Interlocked.Exchange(ref diagnosticRowsCreated, 0);
        Interlocked.Exchange(ref diagnosticCardsCreated, 0);
        Interlocked.Exchange(ref diagnosticRebinds, 0);
        Interlocked.Exchange(ref diagnosticBoundCards, 0);
        Interlocked.Exchange(ref diagnosticUpdateTicks, 0);
        Interlocked.Exchange(ref diagnosticMeasureCalls, 0);
        Interlocked.Exchange(ref diagnosticMeasureTicks, 0);
        Interlocked.Exchange(ref diagnosticArrangeCalls, 0);
        Interlocked.Exchange(ref diagnosticArrangeTicks, 0);
        Volatile.Write(ref diagnosticsEnabled, 1);
    }

    internal static string EndScrollDiagnostic()
    {
        Volatile.Write(ref diagnosticsEnabled, 0);
        static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
        return $"行创建 {Interlocked.Read(ref diagnosticRowsCreated)}、卡片创建 {Interlocked.Read(ref diagnosticCardsCreated)}、行更新 {Interlocked.Read(ref diagnosticRebinds)}、卡片绑定 {Interlocked.Read(ref diagnosticBoundCards)}；"
            + $"绑定合计 {Milliseconds(Interlocked.Read(ref diagnosticUpdateTicks)):F1} ms、测量 {Interlocked.Read(ref diagnosticMeasureCalls)} 次/{Milliseconds(Interlocked.Read(ref diagnosticMeasureTicks)):F1} ms、"
            + $"排列 {Interlocked.Read(ref diagnosticArrangeCalls)} 次/{Milliseconds(Interlocked.Read(ref diagnosticArrangeTicks)):F1} ms";
    }

    static void OnItemsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((RecycledCardRow)sender).UpdateCards();

    void UpdateCards()
    {
        if (CardTemplate is null) return;
        var diagnostic = Volatile.Read(ref diagnosticsEnabled) != 0;
        var start = diagnostic ? Stopwatch.GetTimestamp() : 0;
        if (diagnostic) Interlocked.Increment(ref diagnosticRebinds);
        var count = Items?.Count ?? 0;
        while (Children.Count < count)
        {
            if (CardTemplate.LoadContent() is not FrameworkElement card)
                throw new InvalidOperationException("视频卡片模板没有可显示的根元素");
            Children.Add(card);
            if (diagnostic) Interlocked.Increment(ref diagnosticCardsCreated);
        }

        for (var index = 0; index < count; index++)
        {
            var card = (FrameworkElement)Children[index];
            var item = Items![index];
            if (!ReferenceEquals(card.DataContext, item)) card.DataContext = item;
            if (card.Visibility != Visibility.Visible) card.Visibility = Visibility.Visible;
            if (diagnostic) Interlocked.Increment(ref diagnosticBoundCards);
        }
        for (var index = count; index < Children.Count; index++)
        {
            var card = (FrameworkElement)Children[index];
            if (card.DataContext is not null) card.DataContext = null;
            if (card.Visibility != Visibility.Collapsed) card.Visibility = Visibility.Collapsed;
        }
        if (diagnostic) Interlocked.Add(ref diagnosticUpdateTicks, Stopwatch.GetTimestamp() - start);
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
    {
        if (Volatile.Read(ref diagnosticsEnabled) == 0) return base.MeasureOverride(constraint);
        var start = Stopwatch.GetTimestamp();
        var measured = base.MeasureOverride(constraint);
        Interlocked.Increment(ref diagnosticMeasureCalls);
        Interlocked.Add(ref diagnosticMeasureTicks, Stopwatch.GetTimestamp() - start);
        return measured;
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size arrangeSize)
    {
        if (Volatile.Read(ref diagnosticsEnabled) == 0) return base.ArrangeOverride(arrangeSize);
        var start = Stopwatch.GetTimestamp();
        var arranged = base.ArrangeOverride(arrangeSize);
        Interlocked.Increment(ref diagnosticArrangeCalls);
        Interlocked.Add(ref diagnosticArrangeTicks, Stopwatch.GetTimestamp() - start);
        return arranged;
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace YinggeVideoLibrary;

/// <summary>
/// Token-based visual theme system. All interface modes share one set of
/// semantic tokens; a mode is only a palette plus density values. Style setters
/// freeze the brushes they consume, so Apply() replaces each token's brush with
/// a fresh instance and UiThemeState raises PropertyChanged for every accessor;
/// XAML binds through UiThemeState.Instance and updates without rebuilding the
/// visual tree. The "classic" mode keeps the original palette values, so the
/// original look is expressed through the same tokens, not a second code path.
/// </summary>
public static class UiTheme
{
    public const string Classic = "classic";
    public const string Tech = "tech";
    public const string Cinema = "cinema";
    public const string Compact = "compact";
    public const string Contrast = "contrast";
    public const string Neumorphism = "neumorphism";
    public const string Glass = "glass";
    public const string LineField = "line-field";
    public const string Editorial = "editorial";

    public static readonly (string Id, string Label, string Hint)[] Modes =
    [
        (Classic, "原版界面", "恢复原有配色与导航，保留当前功能"),
        (Tech, "科技浅色", "清爽浅色底，钴蓝品牌色"),
        (Cinema, "深色影院", "黑色电影：灰阶光影、衬线标题、猩红点缀"),
        (Compact, "紧凑高密度", "同屏更多内容，文字保持可读"),
        (Contrast, "高对比护眼", "高对比文字，明显焦点框"),
        (Neumorphism, "新拟物派", "柔和灰底、圆角与双向光影"),
        (Glass, "雾面玻璃", "深墨夜景、无色玻璃与香槟金光点"),
        (LineField, "量化线场", "纸白底、钴蓝线条、清晰的资料层级"),
        (Editorial, "纸感档案", "暖纸色、克制分隔与作品优先"),
    ];

    public static string Current { get; private set; } = Classic;
    public static bool IsClassicMode => Current == Classic;

    public static string Normalize(string? id) =>
        Modes.Any(mode => mode.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ? id!.ToLowerInvariant() : Classic;

    public static string LabelOf(string id) =>
        Modes.FirstOrDefault(mode => mode.Id == Normalize(id)).Label ?? "原版界面";

    // Two brush sets per token:
    // - liveBrushes: returned to C# callers. Never referenced by XAML style
    //   setters, so they stay unfrozen and Apply() mutates their Color —
    //   one-time C# assignments (button.Foreground = UiTheme.TextPrimaryBrush)
    //   follow theme switches automatically.
    // - brushes: exposed through UiThemeState for XAML binding. Style setters
    //   freeze whatever they consume, so Apply() replaces these instances and
    //   UiThemeState raises PropertyChanged to refresh bindings.
    static readonly Dictionary<string, SolidColorBrush> brushes = new(StringComparer.Ordinal);
    static readonly Dictionary<string, SolidColorBrush> liveBrushes = new(StringComparer.Ordinal);
    static readonly Dictionary<string, double> numbers = new(StringComparer.Ordinal);

    public static SolidColorBrush Brush(string token) =>
        liveBrushes.TryGetValue(token, out var brush) ? brush : throw new InvalidOperationException("未知主题令牌：" + token);

    /// <summary>Display-set brush for XAML binding (may be frozen by style
    /// setters; replaced on Apply). C# callers must use Brush()/the named
    /// accessors, which return the live set.</summary>
    internal static SolidColorBrush DisplayBrush(string token) =>
        brushes.TryGetValue(token, out var brush) ? brush : throw new InvalidOperationException("未知主题令牌：" + token);

    /// <summary>Map a legacy hardcoded hex color (#RRGGBB / #AARRGGBB) to the
    /// closest semantic token brush. Legacy hex helpers (Tint/Brush/UpdateBrush)
    /// route through here so their many call sites follow theme switches
    /// without editing every call.</summary>
    public static SolidColorBrush FromLegacyHex(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 8) value = value[2..]; // drop alpha for classification
        if (value.Length != 6 || !int.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                null, out var rgb)) return TextPrimaryBrush;
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (r > 140 && g < 100 && b < 90) return DangerBrush;
        if (r > 140 && g is >= 60 and < 140 && b < 70) return WarningBrush;
        if (b - Math.Max(r, g) > 60 && b > 150) return AccentBrush;
        if (min > 245) return SurfaceBrush;
        if (min > 210 && b - r >= 12) return AccentSoftBackgroundBrush;
        if (max > 208) return BorderBrushToken;
        if (max < 60) return TextPrimaryBrush;
        if (max < 100) return TextSecondaryBrush;
        return TextTertiaryBrush;
    }

    public static double Num(string token) =>
        numbers.TryGetValue(token, out var value) ? value : throw new InvalidOperationException("未知主题数值：" + token);

    // Named accessors so XAML can bind once through x:Static and still follow
    // every later theme switch (the shared brush instances are mutated).
    public static SolidColorBrush PageBackgroundBrush => Brush(nameof(PageBackground));
    public static SolidColorBrush ChromeBackgroundBrush => Brush(nameof(ChromeBackground));
    public static SolidColorBrush SurfaceBrush => Brush(nameof(Surface));
    public static SolidColorBrush SurfaceAltBrush => Brush(nameof(SurfaceAlt));
    public static SolidColorBrush BorderBrushToken => Brush(nameof(Border));
    public static SolidColorBrush BorderStrongBrush => Brush(nameof(BorderStrong));
    public static SolidColorBrush TextPrimaryBrush => Brush(nameof(TextPrimary));
    public static SolidColorBrush TextSecondaryBrush => Brush(nameof(TextSecondary));
    public static SolidColorBrush TextTertiaryBrush => Brush(nameof(TextTertiary));
    public static SolidColorBrush AccentBrush => Brush(nameof(Accent));
    public static SolidColorBrush AccentHoverBrush => Brush(nameof(AccentHover));
    public static SolidColorBrush AccentForegroundBrush => Brush(nameof(AccentForeground));
    public static SolidColorBrush AccentSoftBackgroundBrush => Brush(nameof(AccentSoftBackground));
    public static SolidColorBrush AccentSoftForegroundBrush => Brush(nameof(AccentSoftForeground));
    public static SolidColorBrush AccentAltBrush => Brush(nameof(AccentAlt));
    public static SolidColorBrush SelectionBackgroundBrush => Brush(nameof(SelectionBackground));
    public static SolidColorBrush SelectionForegroundBrush => Brush(nameof(SelectionForeground));
    public static SolidColorBrush WarningBrush => Brush(nameof(Warning));
    public static SolidColorBrush DangerBrush => Brush(nameof(Danger));
    public static SolidColorBrush SuccessBrush => Brush(nameof(Success));
    public static SolidColorBrush FocusRingBrush => Brush(nameof(FocusRing));
    public static SolidColorBrush PosterFallbackBrush => Brush(nameof(PosterFallback));
    public static SolidColorBrush OverlayBackdropBrush => Brush(nameof(OverlayBackdrop));

    // Token names (also usable from code via Brush(nameof(...))).
    public const string PageBackground = nameof(PageBackground);
    public const string ChromeBackground = nameof(ChromeBackground);
    public const string Surface = nameof(Surface);
    public const string SurfaceAlt = nameof(SurfaceAlt);
    public const string Border = nameof(Border);
    public const string BorderStrong = nameof(BorderStrong);
    public const string TextPrimary = nameof(TextPrimary);
    public const string TextSecondary = nameof(TextSecondary);
    public const string TextTertiary = nameof(TextTertiary);
    public const string Accent = nameof(Accent);
    public const string AccentHover = nameof(AccentHover);
    public const string AccentForeground = nameof(AccentForeground);
    public const string AccentSoftBackground = nameof(AccentSoftBackground);
    public const string AccentSoftForeground = nameof(AccentSoftForeground);
    public const string AccentAlt = nameof(AccentAlt);
    public const string SelectionBackground = nameof(SelectionBackground);
    public const string SelectionForeground = nameof(SelectionForeground);
    public const string Warning = nameof(Warning);
    public const string Danger = nameof(Danger);
    public const string Success = nameof(Success);
    public const string FocusRing = nameof(FocusRing);
    public const string PosterFallback = nameof(PosterFallback);
    public const string OverlayBackdrop = nameof(OverlayBackdrop);

    sealed record Palette(
        string PageBackground, string ChromeBackground, string Surface, string SurfaceAlt,
        string Border, string BorderStrong,
        string TextPrimary, string TextSecondary, string TextTertiary,
        string Accent, string AccentHover, string AccentForeground,
        string AccentSoftBackground, string AccentSoftForeground, string AccentAlt,
        string SelectionBackground, string SelectionForeground,
        string Warning, string Danger, string Success,
        string FocusRing, string PosterFallback, string OverlayBackdrop);

    sealed record Density(
        double FontBase, double FontSmall, double FontTiny, double FontTitle, double FontSection,
        double CardWidth, double CardHeight, double CardRadius,
        double SidebarWidth, double NavHeight, double ControlHeight,
        double PageMarginH, double PageMarginTop, double CardGap);

    static readonly Dictionary<string, (Palette Colors, Density Density)> themes = new(StringComparer.Ordinal)
    {
        // 原版界面：与 1.6.0 硬编码颜色逐项一致，保证回退模式视觉不变。
        [Classic] = (new Palette(
            "#F7F8FB", "#FFFFFF", "#FFFFFF", "#F7F9FF", "#E3E8EF", "#BCC8D8",
            "#263347", "#52647A", "#8390A3",
            "#4B6FE1", "#3E63D5", "#FFFFFF", "#EAF0FF", "#4669D9", "#7B67B1",
            "#EEF3FF", "#435FAD",
            "#C27A2E", "#B4232A", "#0F6E56",
            "#5477DF", "#8397BE", "#6B14243B"),
            new Density(13, 12, 11, 29, 17, 170, 230, 13, 78, 50, 40, 48, 42, 22)),
        // 科技浅色：钴蓝品牌色，清爽浅色底。
        [Tech] = (new Palette(
            "#F3F5FB", "#FFFFFF", "#FFFFFF", "#EEF3FE", "#DFE6F2", "#B9C6E4",
            "#16233B", "#4A5B76", "#596881",
            "#2B4BEB", "#1B2FBF", "#FFFFFF", "#E9EEFE", "#2440C9", "#7C5CFC",
            "#E9EEFE", "#2440C9",
            "#B26A1B", "#B4232A", "#0E7A5F",
            "#2B4BEB", "#5D74C9", "#8014223B"),
            new Density(13, 12, 11, 26, 17, 172, 232, 12, 88, 52, 36, 44, 34, 18)),
        // 深色影院：Film Noir。以近黑灰阶承载内容，猩红仅标识关键操作。
        [Cinema] = (new Palette(
            "#0A0A0A", "#101010", "#171717", "#202020", "#303030", "#585858",
            "#F5F5F5", "#C6C6C6", "#A8A8A8",
            "#E75469", "#F0697E", "#0A0A0A", "#2B191D", "#F5F5F5", "#D4AF37",
            "#29181B", "#FFFFFF",
            "#D4AF37", "#F05A69", "#71B89C",
            "#A8A8A8", "#3E3E3E", "#C9000000"),
            new Density(13.5, 12, 11, 29, 17, 188, 256, 8, 88, 54, 38, 48, 38, 20)),
        // 紧凑高密度：小字号小卡片，同屏更多内容。
        [Compact] = (new Palette(
            "#F2F3F5", "#FFFFFF", "#FFFFFF", "#F6F7F9", "#D9DCE1", "#B4BAC4",
            "#1F2937", "#556070", "#636C78",
            "#2B4BEB", "#1B2FBF", "#FFFFFF", "#E9EEFE", "#2440C9", "#6D55C9",
            "#E9EEFE", "#2440C9",
            "#B45309", "#B4232A", "#0F6E56",
            "#2B4BEB", "#8B9DC3", "#8014223B"),
            new Density(12, 11, 10, 20, 15, 132, 180, 8, 60, 42, 30, 22, 16, 10)),
        // 高对比护眼：低蓝光绿底、高对比文字、明显焦点框。
        [Contrast] = (new Palette(
            "#E4EFE0", "#D8E8D2", "#F2F8EE", "#E9F3E3", "#5C7350", "#3E5A3E",
            "#16240F", "#334730", "#40543A",
            "#2C5223", "#1E3D17", "#F2F8EE", "#D2E5C8", "#1E3D17", "#4B3A8C",
            "#CFE4C2", "#16240F",
            "#7A4A00", "#8F1D1D", "#1E5E2E",
            "#16240F", "#6E8A66", "#8014223B"),
            new Density(13.5, 12.5, 11.5, 24, 16, 170, 230, 8, 92, 54, 40, 44, 34, 20)),
        [Neumorphism] = (new Palette(
            "#E0E5EC", "#E0E5EC", "#E0E5EC", "#E9EDF3", "#C8D0DA", "#AAB7C8",
            "#263449", "#4C5C72", "#506078",
            "#6652BD", "#5340A5", "#F4F1FC", "#DCD5F4", "#493796", "#7865C5",
            "#DCD5F4", "#352477",
            "#986018", "#AA3745", "#277456",
            "#6652BD", "#8D9BAE", "#701B2538"),
            new Density(13.5, 12, 11, 27, 17, 174, 234, 18, 88, 54, 40, 46, 36, 20)),
        [Glass] = (new Palette(
            "#0B1322", "#11FFFFFF", "#14FFFFFF", "#1FFFFFFF", "#28FFFFFF", "#4DFFFFFF",
            "#F5F7FB", "#D6E0ED", "#B6C5D7",
            "#E4B863", "#F0CA7F", "#0B1322", "#25FFFFFF", "#E4B863", "#7C9CC4",
            "#2EFFFFFF", "#FFFFFF", "#E4B863", "#F07882", "#7BC9A8",
            "#D6E0ED", "#33517A", "#C9060A13"),
            new Density(13.5, 12, 11, 27, 17, 178, 238, 16, 88, 54, 40, 46, 38, 20)),
        [LineField] = (new Palette(
            "#FAFAF8", "#F1F2EF", "#FFFFFF", "#EEEEEB", "#D9DBD5", "#A9B4C5",
            "#202A38", "#4A586B", "#59677B",
            "#2152E7", "#1742C2", "#FFFFFF", "#DEE8FF", "#153BA8", "#496DCE",
            "#DDE8FF", "#153BA8", "#9C621F", "#B43B45", "#247553",
            "#2152E7", "#95A9CF", "#701B2538"),
            new Density(13, 12, 11, 27, 17, 178, 238, 16, 88, 54, 40, 46, 36, 20)),
        [Editorial] = (new Palette(
            "#F5F1E9", "#EDE8DD", "#FAF7F0", "#F0EBE1", "#D5CFC3", "#9B9488",
            "#282722", "#575349", "#6E685E",
            "#62563F", "#483D2D", "#FFFFFF", "#E8E0D1", "#443927", "#87735A",
            "#E7DFD0", "#392F21", "#966019", "#A33936", "#3D7052",
            "#62563F", "#A8A093", "#701B2538"),
            new Density(13.5, 12, 11, 29, 18, 178, 238, 9, 88, 54, 40, 46, 38, 21)),
    };

    static UiTheme()
    {
        // Create every token brush once so later Apply() calls only mutate colors.
        var first = themes[Classic].Colors;
        foreach (var token in new[]
        {
            PageBackground, ChromeBackground, Surface, SurfaceAlt, Border, BorderStrong,
            TextPrimary, TextSecondary, TextTertiary,
            Accent, AccentHover, AccentForeground, AccentSoftBackground, AccentSoftForeground, AccentAlt,
            SelectionBackground, SelectionForeground,
            Warning, Danger, Success, FocusRing, PosterFallback, OverlayBackdrop
        })
        {
            brushes[token] = new SolidColorBrush(Pick(first, token));
            liveBrushes[token] = new SolidColorBrush(Pick(first, token));
        }
        ApplyDensity(themes[Classic].Density);
        UiThemeState.NotifyAll();
    }

    static System.Windows.Media.Color Pick(Palette palette, string token) =>
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(token switch
    {
        nameof(PageBackground) => palette.PageBackground,
        nameof(ChromeBackground) => palette.ChromeBackground,
        nameof(Surface) => palette.Surface,
        nameof(SurfaceAlt) => palette.SurfaceAlt,
        nameof(Border) => palette.Border,
        nameof(BorderStrong) => palette.BorderStrong,
        nameof(TextPrimary) => palette.TextPrimary,
        nameof(TextSecondary) => palette.TextSecondary,
        nameof(TextTertiary) => palette.TextTertiary,
        nameof(Accent) => palette.Accent,
        nameof(AccentHover) => palette.AccentHover,
        nameof(AccentForeground) => palette.AccentForeground,
        nameof(AccentSoftBackground) => palette.AccentSoftBackground,
        nameof(AccentSoftForeground) => palette.AccentSoftForeground,
        nameof(AccentAlt) => palette.AccentAlt,
        nameof(SelectionBackground) => palette.SelectionBackground,
        nameof(SelectionForeground) => palette.SelectionForeground,
        nameof(Warning) => palette.Warning,
        nameof(Danger) => palette.Danger,
        nameof(Success) => palette.Success,
        nameof(FocusRing) => palette.FocusRing,
        nameof(PosterFallback) => palette.PosterFallback,
        _ => palette.OverlayBackdrop,
    });

    /// <summary>Switch the active mode. XAML style setters freeze the brushes
    /// they consume, so the display set gets fresh instances and bound elements
    /// pick them up via UiThemeState PropertyChanged. The live set (C# callers)
    /// is mutated in place — C# assignments stay valid across switches. The
    /// visual tree is never rebuilt.</summary>
    public static void Apply(string? id)
    {
        var normalized = Normalize(id);
        Current = normalized;
        var (colors, density) = themes[normalized];
        foreach (var token in brushes.Keys.ToArray())
        {
            var color = Pick(colors, token);
            brushes[token] = new SolidColorBrush(color);
            if (liveBrushes[token].IsFrozen) liveBrushes[token] = new SolidColorBrush(color);
            else liveBrushes[token].Color = color;
        }
        ApplyDensity(density);
        UiThemeState.NotifyAll();
    }

    static void ApplyDensity(Density density)
    {
        numbers[nameof(FontBase)] = density.FontBase;
        numbers[nameof(FontSmall)] = density.FontSmall;
        numbers[nameof(FontTiny)] = density.FontTiny;
        numbers[nameof(FontTitle)] = density.FontTitle;
        numbers[nameof(FontSection)] = density.FontSection;
        numbers[nameof(CardWidth)] = density.CardWidth;
        numbers[nameof(CardHeight)] = density.CardHeight;
        numbers[nameof(CardRadius)] = density.CardRadius;
        numbers[nameof(SidebarWidth)] = density.SidebarWidth;
        numbers[nameof(NavHeight)] = density.NavHeight;
        numbers[nameof(ControlHeight)] = density.ControlHeight;
        numbers[nameof(PageMarginH)] = density.PageMarginH;
        numbers[nameof(PageMarginTop)] = density.PageMarginTop;
        numbers[nameof(CardGap)] = density.CardGap;
    }

    public const string FontBase = nameof(FontBase);
    public const string FontSmall = nameof(FontSmall);
    public const string FontTiny = nameof(FontTiny);
    public const string FontTitle = nameof(FontTitle);
    public const string FontSection = nameof(FontSection);
    public const string CardWidth = nameof(CardWidth);
    public const string CardHeight = nameof(CardHeight);
    public const string CardRadius = nameof(CardRadius);
    public const string SidebarWidth = nameof(SidebarWidth);
    public const string NavHeight = nameof(NavHeight);
    public const string ControlHeight = nameof(ControlHeight);
    public const string PageMarginH = nameof(PageMarginH);
    public const string PageMarginTop = nameof(PageMarginTop);
    public const string CardGap = nameof(CardGap);
}

/// <summary>
/// Binding source for density values (doubles cannot be mutated in place like
/// brush colors, so XAML binds to these properties and gets PropertyChanged on
/// every theme switch).
/// </summary>
public sealed class UiThemeState : INotifyPropertyChanged
{
    public static readonly UiThemeState Instance = new();
    UiThemeState() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal static void NotifyAll() =>
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(null));

    public double FontBase => UiTheme.Num(UiTheme.FontBase);
    public double FontSmall => UiTheme.Num(UiTheme.FontSmall);
    public double FontTiny => UiTheme.Num(UiTheme.FontTiny);
    public double FontTitle => UiTheme.Num(UiTheme.FontTitle);
    public double CardWidth => UiTheme.Num(UiTheme.CardWidth);
    public double CardHeight => UiTheme.Num(UiTheme.CardHeight);
    public double CardRadius => UiTheme.Num(UiTheme.CardRadius);
    public double CardGap => UiTheme.Num(UiTheme.CardGap);
    public Thickness CardMargin => new(0, 0, CardGap, CardGap + 5);
    public double ControlHeight => UiTheme.Num(UiTheme.ControlHeight);
    public FontFamily HeadingFontFamily => UiTheme.Current switch
    {
        UiTheme.Editorial => new FontFamily("SimSun"),
        UiTheme.Cinema => new FontFamily("SimSun"),
        UiTheme.LineField => new FontFamily("Segoe UI"),
        _ => new FontFamily("Microsoft YaHei UI")
    };
    public FontFamily MetricFontFamily => new("Segoe UI");
    public bool IsNeumorphism => UiTheme.Current == UiTheme.Neumorphism;
    public bool IsGlass => UiTheme.Current == UiTheme.Glass;
    public bool IsCinema => UiTheme.Current == UiTheme.Cinema;
    public Brush PanelEdgeBrush => IsNeumorphism
        ? new LinearGradientBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(174, 187, 203), 135)
        : IsGlass
            ? new LinearGradientBrush(Color.FromArgb(82, 255, 255, 255), Color.FromArgb(32, 2, 6, 16), 90)
            : BorderBrushToken;
    public Brush InputSurfaceBrush => IsNeumorphism
        ? new LinearGradientBrush(Color.FromRgb(210, 218, 229), Color.FromRgb(236, 241, 248), 135)
        : IsGlass
            ? new LinearGradientBrush(Color.FromArgb(45, 255, 255, 255), Color.FromArgb(18, 255, 255, 255), 90)
            : SurfaceBrush;
    public Brush RaisedSurfaceBrush => IsNeumorphism
        ? new LinearGradientBrush(Color.FromRgb(238, 243, 249), Color.FromRgb(214, 223, 234), 135)
        : IsGlass
            ? new LinearGradientBrush(Color.FromArgb(48, 255, 255, 255), Color.FromArgb(20, 255, 255, 255), 90)
            : SurfaceBrush;
    public Brush PressedSurfaceBrush => IsNeumorphism
        ? InputSurfaceBrush
        : IsGlass
            ? new SolidColorBrush(Color.FromArgb(67, 255, 255, 255))
            : SelectionBackgroundBrush;
    public Brush PageBackdropBrush => IsGlass ? new LinearGradientBrush(
        Color.FromRgb(6, 10, 19), Color.FromRgb(11, 19, 34), 26) : PageBackgroundBrush;
    public Brush MainContentBackdropBrush => IsGlass
        ? GlassSceneBrush
        : IsCinema ? new LinearGradientBrush(Color.FromRgb(10, 10, 10), Color.FromRgb(22, 22, 22), 155)
        : new LinearGradientBrush(UiTheme.PageBackgroundBrush.Color,
            UiTheme.SurfaceAltBrush.Color, 45);
    // The glass surface needs a scene to refract. These soft light wells sit
    // behind every translucent panel, and are drawn once rather than blurred
    // per card. This keeps the visual depth while lists continue to scroll.
    static readonly Brush GlassSceneBrush = CreateGlassSceneBrush();
    static Brush CreateGlassSceneBrush()
    {
        var scene = new DrawingGroup();
        scene.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(7, 12, 23)), null,
            new RectangleGeometry(new Rect(0, 0, 1000, 1000))));
        scene.Children.Add(new GeometryDrawing(
            new RadialGradientBrush(Color.FromArgb(110, 38, 70, 111), Colors.Transparent), null,
            new EllipseGeometry(new System.Windows.Point(730, 250), 490, 380)));
        scene.Children.Add(new GeometryDrawing(
            new RadialGradientBrush(Color.FromArgb(46, 159, 126, 79), Colors.Transparent), null,
            new EllipseGeometry(new System.Windows.Point(130, 760), 420, 360)));
        var brush = new DrawingBrush(scene)
        {
            Viewbox = new Rect(0, 0, 1000, 1000), ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill
        };
        brush.Freeze();
        return brush;
    }
    public Effect? SoftShadow => IsNeumorphism || IsGlass ? new DropShadowEffect
    {
        Color = IsGlass ? Color.FromRgb(3, 7, 18) : Color.FromRgb(160, 169, 183),
        Direction = 315, BlurRadius = IsGlass ? 40 : 12,
        ShadowDepth = IsGlass ? 14 : 4, Opacity = IsGlass ? .55 : .52
    } : null;
    public Effect? SoftHighlight => IsNeumorphism ? new DropShadowEffect
    {
        Color = System.Windows.Media.Color.FromRgb(255, 255, 255),
        Direction = 135, BlurRadius = 12, ShadowDepth = 4, Opacity = 0.85
    } : null;

    // Brush accessors for XAML binding (PropertyChanged(null) refreshes all).
    // These read the DISPLAY set: replaceable instances that style
    // setters may freeze. C# callers use UiTheme.XxxBrush (live set).
    public SolidColorBrush PageBackgroundBrush => UiTheme.DisplayBrush(UiTheme.PageBackground);
    public SolidColorBrush ChromeBackgroundBrush => UiTheme.DisplayBrush(UiTheme.ChromeBackground);
    public SolidColorBrush SurfaceBrush => UiTheme.DisplayBrush(UiTheme.Surface);
    public SolidColorBrush SurfaceAltBrush => UiTheme.DisplayBrush(UiTheme.SurfaceAlt);
    public SolidColorBrush BorderBrushToken => UiTheme.DisplayBrush(UiTheme.Border);
    public SolidColorBrush BorderStrongBrush => UiTheme.DisplayBrush(UiTheme.BorderStrong);
    public SolidColorBrush TextPrimaryBrush => UiTheme.DisplayBrush(UiTheme.TextPrimary);
    public SolidColorBrush TextSecondaryBrush => UiTheme.DisplayBrush(UiTheme.TextSecondary);
    public SolidColorBrush TextTertiaryBrush => UiTheme.DisplayBrush(UiTheme.TextTertiary);
    public SolidColorBrush AccentBrush => UiTheme.DisplayBrush(UiTheme.Accent);
    public SolidColorBrush AccentHoverBrush => UiTheme.DisplayBrush(UiTheme.AccentHover);
    public SolidColorBrush AccentForegroundBrush => UiTheme.DisplayBrush(UiTheme.AccentForeground);
    public SolidColorBrush AccentSoftBackgroundBrush => UiTheme.DisplayBrush(UiTheme.AccentSoftBackground);
    public SolidColorBrush AccentSoftForegroundBrush => UiTheme.DisplayBrush(UiTheme.AccentSoftForeground);
    public SolidColorBrush AccentAltBrush => UiTheme.DisplayBrush(UiTheme.AccentAlt);
    public SolidColorBrush SelectionBackgroundBrush => UiTheme.DisplayBrush(UiTheme.SelectionBackground);
    public SolidColorBrush SelectionForegroundBrush => UiTheme.DisplayBrush(UiTheme.SelectionForeground);
    public SolidColorBrush WarningBrush => UiTheme.DisplayBrush(UiTheme.Warning);
    public SolidColorBrush DangerBrush => UiTheme.DisplayBrush(UiTheme.Danger);
    public SolidColorBrush SuccessBrush => UiTheme.DisplayBrush(UiTheme.Success);
    public SolidColorBrush FocusRingBrush => UiTheme.DisplayBrush(UiTheme.FocusRing);
    public SolidColorBrush PosterFallbackBrush => UiTheme.DisplayBrush(UiTheme.PosterFallback);
    public SolidColorBrush OverlayBackdropBrush => UiTheme.DisplayBrush(UiTheme.OverlayBackdrop);
}

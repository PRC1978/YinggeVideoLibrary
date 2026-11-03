namespace YinggeVideoLibrary;

internal static class UiMotion
{
    public const string Smooth = "smooth";
    public const string Balanced = "balanced";
    public const string Animated = "animated";

    public static string Normalize(string? value) => value is Smooth or Animated ? value : Balanced;
    public static int PageFadeMilliseconds(string? value) => Normalize(value) switch
    {
        Smooth => 0, Animated => 280, _ => 130
    };
    public static int FeedbackMilliseconds(string? value) => Normalize(value) switch
    {
        Smooth => 0, Animated => 210, _ => 120
    };
}

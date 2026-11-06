namespace YinggeVideoLibrary;

public static class UiColorMode
{
    // Native preserves the palette of existing installations. Users can then
    // opt into a light, dark or high-contrast mode independently of layout.
    public const string Native = "native";
    public const string Light = "light";
    public const string Dark = "dark";
    public const string HighContrast = "high-contrast";

    public static string Normalize(string? value) => value?.ToLowerInvariant() switch
    {
        Light => Light, Dark => Dark, HighContrast => HighContrast,
        _ => Native
    };
}

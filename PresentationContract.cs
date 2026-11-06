namespace YinggeVideoLibrary;

// A theme is a composition contract, not a color swatch. Keep the legacy
// palette IDs available for existing settings while admitting these five
// complete presentations as the primary choices.
internal enum PresentationSkeleton { Library, Theater, Workbench, Atlas, Layered }
internal enum PresentationNavigation { Rail, Filmstrip, CommandBar, Inspector, FloatingRail }
internal enum PresentationDensity { Comfortable, Cinematic, Dense, Spatial, Airy }
internal enum PresentationMaterial { Flat, Noir, Paper, Diagram, Glass }
internal enum PresentationTypography { System, Editorial, Utility, Cartographic, Atmospheric }
internal enum PresentationMotion { Restrained, Cinematic, Immediate, Spatial, Parallax }
internal enum PresentationComponents { Cards, PosterBelt, Rows, Nodes, FloatingPanels }

internal sealed record PresentationContract(
    string Id,
    PresentationSkeleton Skeleton,
    PresentationNavigation Navigation,
    PresentationDensity Density,
    PresentationMaterial Material,
    PresentationTypography Typography,
    PresentationMotion Motion,
    PresentationComponents Components)
{
    public int DifferenceCount(PresentationContract other) =>
        (Skeleton != other.Skeleton ? 1 : 0) +
        (Navigation != other.Navigation ? 1 : 0) +
        (Density != other.Density ? 1 : 0) +
        (Material != other.Material ? 1 : 0) +
        (Typography != other.Typography ? 1 : 0) +
        (Motion != other.Motion ? 1 : 0) +
        (Components != other.Components ? 1 : 0);
}

internal static class PresentationRegistry
{
    internal const string Atlas = "atlas";

    internal static readonly IReadOnlyList<PresentationContract> Core =
    [
        new(UiTheme.Classic, PresentationSkeleton.Library, PresentationNavigation.Rail,
            PresentationDensity.Comfortable, PresentationMaterial.Flat,
            PresentationTypography.System, PresentationMotion.Restrained, PresentationComponents.Cards),
        new(UiTheme.Cinema, PresentationSkeleton.Theater, PresentationNavigation.Filmstrip,
            PresentationDensity.Cinematic, PresentationMaterial.Noir,
            PresentationTypography.Editorial, PresentationMotion.Cinematic, PresentationComponents.PosterBelt),
        new(UiTheme.Compact, PresentationSkeleton.Workbench, PresentationNavigation.CommandBar,
            PresentationDensity.Dense, PresentationMaterial.Paper,
            PresentationTypography.Utility, PresentationMotion.Immediate, PresentationComponents.Rows),
        new(Atlas, PresentationSkeleton.Atlas, PresentationNavigation.Inspector,
            PresentationDensity.Spatial, PresentationMaterial.Diagram,
            PresentationTypography.Cartographic, PresentationMotion.Spatial, PresentationComponents.Nodes),
        new(UiTheme.Glass, PresentationSkeleton.Layered, PresentationNavigation.FloatingRail,
            PresentationDensity.Airy, PresentationMaterial.Glass,
            PresentationTypography.Atmospheric, PresentationMotion.Parallax, PresentationComponents.FloatingPanels)
    ];

    static PresentationRegistry()
    {
        if (Core.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != Core.Count)
            throw new InvalidOperationException("主题 ID 重复");
        for (var i = 0; i < Core.Count; i++)
        for (var j = i + 1; j < Core.Count; j++)
            if (Core[i].DifferenceCount(Core[j]) < 3)
                throw new InvalidOperationException($"主题 {Core[i].Id} 与 {Core[j].Id} 的结构差异不足");
    }

    internal static PresentationContract? Find(string id) =>
        Core.FirstOrDefault(x => x.Id == id);
}

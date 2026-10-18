using System.Linq;

namespace OpenXLR.UI.Skinning;

/// <summary>
/// The window's arrangement when a skin declares no layout, written down
/// once: the five sections one above the other in their usual order, with
/// the gap MainWindow.axaml has always put between them.
/// </summary>
public static class SkinLayoutDefaults
{
    /// <summary>The gap between the sections, as MainWindow.axaml sets it on SectionCards.</summary>
    public const double Spacing = 8;

    /// <summary>
    /// The narrowest a section is drawn in a horizontal row. Every section is
    /// held to fit a 640 pixel window in the column, which leaves it about
    /// this wide, so a row never asks a section to fit less.
    /// </summary>
    public const double RowSectionMinWidth = 600;

    /// <summary>The classic tree: a vertical stack of the five regions in their usual order.</summary>
    public static SkinLayout Classic { get; } = new(SkinFormat.LayoutVersion,
        new SkinLayoutStack(SkinLayoutOrientation.Vertical, Spacing,
            [.. SkinLayoutRegions.All.Select(id => new SkinLayoutRegion(id))]));
}

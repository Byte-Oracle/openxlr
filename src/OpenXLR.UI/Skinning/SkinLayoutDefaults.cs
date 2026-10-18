using System.Linq;

namespace OpenXLR.UI.Skinning;

/// <summary>
/// The window's arrangement when a skin declares no layout, written down
/// once: the header at the top and the five sections one above the other
/// under it in their usual order, with the gap MainWindow.axaml has always
/// put between them.
/// </summary>
public static class SkinLayoutDefaults
{
    /// <summary>The gap between the sections, as MainWindow.axaml sets it on SectionCards.</summary>
    public const double Spacing = 8;

    /// <summary>
    /// The narrowest a region is drawn wherever its container gives it a
    /// width of its own: in a horizontal stack, a grid cell or a dock's side.
    /// Every section is held to fit a 640 pixel window in the column, which
    /// leaves it about this wide, so no container asks a region to fit less.
    /// The header is held to the same width.
    /// </summary>
    public const double RowSectionMinWidth = 600;

    /// <summary>The id of the classic tree's one stack, which names its slots in ui.json.</summary>
    public const string ClassicId = "main";

    /// <summary>The classic tree: a vertical stack of the five sections in their usual order, under the header.</summary>
    public static SkinLayout Classic { get; } = new(SkinFormat.LayoutVersion,
        new SkinLayoutStack(ClassicId, SkinLayoutOrientation.Vertical, Spacing,
            [.. SkinLayoutRegions.Sections.Select(id => new SkinLayoutChild(new SkinLayoutRegion(id)))]));
}

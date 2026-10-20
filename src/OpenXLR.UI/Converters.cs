using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

public static class Converters
{
    /// <summary>Tile-header chevron: up when the tile is expanded.</summary>
    public static readonly IValueConverter Chevron =
        new FuncValueConverter<bool, string>(expanded => expanded ? "⌃" : "⌄");

    /// <summary>An EQ band's colour by its number, 1 to 4: the skin's live Ox.Eq.Band1 to Ox.Eq.Band4.</summary>
    public static readonly IValueConverter EqBandColour =
        new FuncValueConverter<int, IBrush>(band => SkinService.LiveBrush(EqGraph.BandTokens[System.Math.Clamp(band, 1, 4) - 1]));

    /// <summary>An EQ type's icon path data, as geometry.</summary>
    public static readonly IValueConverter EqTypeIcon =
        new FuncValueConverter<string, Geometry?>(data => data is null ? null : Geometry.Parse(data));
}

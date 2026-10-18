using System;
using System.Collections.Generic;
using System.Linq;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>
/// How the main window's five sections stand: their order by tile name, the
/// direction they run in and the gap between them, and whose order it is.
/// </summary>
/// <param name="Order">Every section's tile name, in the order shown.</param>
/// <param name="SkinOrder">
/// The order Reset returns to: the skin's own, or the usual order for a skin
/// without a layout.
/// </param>
/// <param name="Skin">The skin whose layout this is; null when the skin sets none.</param>
/// <param name="Version">That layout's version; null with <paramref name="Skin"/>.</param>
internal sealed record SectionArrangement(
    IReadOnlyList<string> Order,
    IReadOnlyList<string> SkinOrder,
    SkinLayoutOrientation Orientation,
    double Spacing,
    string? Skin,
    int? Version)
{
    /// <summary>The tile names in their usual order, which is the classic layout's region order.</summary>
    internal static IReadOnlyList<string> Tiles { get; } = [.. SkinLayoutRegions.All.Select(TileFor)];

    /// <summary>The section tile that shows a layout region.</summary>
    internal static string TileFor(string region) => region switch
    {
        SkinLayoutRegions.Inputs => "InputsTile",
        SkinLayoutRegions.Headphones => "HeadphonesTile",
        SkinLayoutRegions.Monitor => "MonitorTile",
        SkinLayoutRegions.Applications => "ApplicationsTile",
        SkinLayoutRegions.Submixer => "SubmixerTile",
        _ => throw new ArgumentException($"\"{region}\" is not a layout region.", nameof(region)),
    };

    /// <summary>The layout region a section tile shows, or null for a name that is not one.</summary>
    internal static string? RegionFor(string tile) =>
        SkinLayoutRegions.All.FirstOrDefault(region => TileFor(region) == tile);

    /// <summary>True when the window would look the same: order, direction and gap.</summary>
    internal bool SameStructure(SectionArrangement other) =>
        Orientation == other.Orientation && Spacing.Equals(other.Spacing)
        && Order.SequenceEqual(other.Order, StringComparer.Ordinal);

    /// <summary>
    /// The arrangement for the skin in force, in this precedence:
    ///
    /// - a skin with no layout: the saved <see cref="UiSettings.SectionOrder"/>,
    ///   made whole, in the classic vertical stack, exactly as before skins
    ///   had layouts;
    /// - a skin with a layout: the user's arrangement for that skin and layout
    ///   version over the skin's own order, made whole against the skin's
    ///   order, in the skin's direction and gap. The legacy order is not read.
    /// </summary>
    internal static SectionArrangement Resolve(string skin, SkinLayout? layout, UiSettings settings)
    {
        if (layout is null)
            return new(MainWindow.CompleteSectionOrder(settings.SectionOrder), Tiles,
                SkinLayoutOrientation.Vertical, SkinLayoutDefaults.Spacing, null, null);

        string[] skinOrder = [.. layout.Main.RegionOrder.Select(TileFor)];
        IEnumerable<string>? saved = settings.SkinSectionOrderFor(skin, layout.Version)?.Order
            .Select(region => SkinLayoutRegions.Exists(region) ? TileFor(region) : "");
        return new(MainWindow.CompleteSectionOrder(saved, skinOrder), skinOrder,
            layout.Main.Orientation, layout.Main.Spacing, skin, layout.Version);
    }

    /// <summary>
    /// The settings with this order saved where it belongs: the skin's own
    /// entry when the skin sets a layout, the legacy order otherwise.
    /// </summary>
    internal UiSettings Save(UiSettings settings, IReadOnlyList<string> order) =>
        Skin is { } skin
            ? settings.WithSkinSectionOrder(skin, Version!.Value, [.. order.Select(RegionFor).OfType<string>()])
            : settings with { SectionOrder = [.. order] };

    /// <summary>
    /// The settings after Reset: the skin's own entry removed, so its order
    /// comes back, or the legacy order set to the usual one.
    /// </summary>
    internal UiSettings Reset(UiSettings settings) =>
        Skin is { } skin
            ? settings.WithSkinSectionOrder(skin, Version!.Value, null)
            : settings with { SectionOrder = [.. Tiles] };
}

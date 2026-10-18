using System;
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

    /// <summary>The padding a section's card keeps on its left and right together.</summary>
    public const double CardPadding = 24;

    /// <summary>The padding a mix bank's tile keeps on its left and right together.</summary>
    public const double BankPadding = 16;

    /// <summary>The gap between the strips of a bank.</summary>
    public const double StripSpacing = 4;

    /// <summary>
    /// The most strips a bank asks room for in its narrowest width: one
    /// channel and the master strip beside it. More strips than that scroll
    /// sideways inside the bank rather than widening the window.
    /// </summary>
    public const int BankMinimumStrips = 2;

    /// <summary>How many strip widths an input row, or a row of a send list, needs for its name, fader, value and mute.</summary>
    public const int RowStrips = 3;

    /// <summary>
    /// The narrowest a region is drawn wherever its container gives it a
    /// width of its own, by the template it wears. The classic templates keep
    /// <see cref="RowSectionMinWidth"/>. An input rack needs
    /// <see cref="RowStrips"/> strip widths for its rows; a submixer of banks
    /// needs each bank's strips, up to <see cref="BankMinimumStrips"/>, or the
    /// master card's width, side by side or one above the other, with the gap
    /// between banks. Neither ever asks for more than the classic width.
    /// </summary>
    /// <param name="sizes">The token values in force and the counts the window shows now.</param>
    public static double RegionMinWidth(string region, SkinLayout? layout, RegionSizes sizes)
    {
        if (layout is null || region == SkinLayoutRegions.Header) return RowSectionMinWidth;
        if (region == SkinLayoutRegions.Inputs && layout.Inputs.IsRack)
            return Math.Min(RowSectionMinWidth, RowStrips * sizes.StripWidth + CardPadding);
        if (region != SkinLayoutRegions.Submixer || !layout.Submixer.IsBanks) return RowSectionMinWidth;
        SkinSubmixerOptions banks = layout.Submixer;
        bool masterStrip = banks.Master.Template == SkinLayoutTemplates.MasterStrip;
        double bank;
        if (banks.Strip.Template == SkinLayoutTemplates.VerticalStrip)
        {
            int strips = Math.Clamp(sizes.VisibleChannels + (masterStrip ? 1 : 0), 1, BankMinimumStrips);
            bank = strips * sizes.StripWidth + (strips - 1) * StripSpacing;
        }
        else bank = RowStrips * sizes.StripWidth;
        if (!masterStrip) bank = Math.Max(bank, sizes.MixWidth);
        bank += BankPadding;
        int count = Math.Max(1, sizes.Banks);
        double width = banks.Orientation == SkinLayoutOrientation.Horizontal
            ? count * bank + (count - 1) * sizes.BankSpacing
            : bank;
        return Math.Min(RowSectionMinWidth, width + CardPadding);
    }

    /// <summary>The id of the classic tree's one stack, which names its slots in ui.json.</summary>
    public const string ClassicId = "main";

    /// <summary>The classic tree: a vertical stack of the five sections in their usual order, under the header.</summary>
    public static SkinLayout Classic { get; } = new(SkinFormat.LayoutVersion,
        new SkinLayoutStack(ClassicId, SkinLayoutOrientation.Vertical, Spacing,
            [.. SkinLayoutRegions.Sections.Select(id => new SkinLayoutChild(new SkinLayoutRegion(id)))]));
}

/// <summary>What a region's narrowest width is reckoned from: token values and what the window shows now.</summary>
/// <param name="StripWidth"><c>Ox.Mixer.StripWidth</c>.</param>
/// <param name="MixWidth"><c>Ox.Mixer.MixWidth</c>.</param>
/// <param name="BankSpacing"><c>Ox.Mixer.BankSpacing</c>.</param>
/// <param name="Banks">How many banks the submixer shows.</param>
/// <param name="VisibleChannels">How many channels a bank shows a strip for.</param>
public readonly record struct RegionSizes(double StripWidth, double MixWidth, double BankSpacing, int Banks, int VisibleChannels);

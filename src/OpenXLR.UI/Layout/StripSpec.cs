using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Layout;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI.Layout;

/// <summary>
/// How one strip is drawn, resolved from a skin's strip options for the
/// template that draws it: the parts it shows in order, the way they run,
/// where the meter sits, the label, and which way the fader and the meter
/// run. A <see cref="StripPanel"/> arranges the template's named parts by it.
/// </summary>
/// <param name="Parts">The parts shown, in order; a part not listed is left out and out of the tab order.</param>
/// <param name="Flow">Which way the parts follow one another.</param>
/// <param name="MeterAnchor">The part the meter sits beside, or null when it is a part of its own.</param>
public sealed record StripSpec(
    IReadOnlyList<string> Parts,
    Orientation Flow,
    string? MeterAnchor,
    SkinLabelStyle Label,
    Orientation FaderOrientation,
    bool FaderReversed,
    Orientation MeterOrientation)
{
    /// <summary>The two meters of a stereo pair lie one above the other when flat, side by side when upright.</summary>
    public Orientation MeterPairOrientation =>
        MeterOrientation == Orientation.Vertical ? Orientation.Horizontal : Orientation.Vertical;

    public bool FaderUpright => FaderOrientation == Orientation.Vertical;

    /// <summary>True when the template draws the icon the layout editor gave a channel or mix.</summary>
    public bool WantsIcon => Label != SkinLabelStyle.Text;

    /// <summary>True when the template draws the name even beside an icon.</summary>
    public bool WantsName => Label != SkinLabelStyle.Icon;

    /// <summary>
    /// A strip drawn from <paramref name="options"/>, keeping only the parts
    /// <paramref name="available"/> holds: a channel without hardware gain
    /// has no gain part, whatever the skin orders.
    /// </summary>
    /// <param name="anchor">The part a meter <c>beside-fader</c> sits beside: the fader, or the gain on an input row.</param>
    public static StripSpec From(SkinStripOptions options, Orientation flow, IReadOnlyCollection<string> available, string anchor)
    {
        string[] parts = [.. options.Shown.Where(available.Contains)];
        string? meterAnchor = options.Meter.Placement switch
        {
            SkinMeterPlacement.BesideLabel => SkinStripParts.Identity,
            SkinMeterPlacement.BesideFader => anchor,
            _ => null,
        };
        if (meterAnchor is not null && (!parts.Contains(meterAnchor, StringComparer.Ordinal)
            || !parts.Contains(SkinStripParts.Meter, StringComparer.Ordinal))) meterAnchor = null;
        return new StripSpec(parts, flow, meterAnchor, options.Label,
            Axis(options.Fader.Orientation), options.Fader.Direction == SkinFaderDirection.Reversed, Axis(options.Meter.Orientation));
    }

    /// <summary>A master drawn from a skin's master options: every part of a send strip, in their default order.</summary>
    public static StripSpec From(SkinMasterOptions options)
    {
        string[] parts = [.. SkinStripParts.Send];
        string? meterAnchor = options.Meter.Placement switch
        {
            SkinMeterPlacement.BesideLabel => SkinStripParts.Identity,
            SkinMeterPlacement.BesideFader => SkinStripParts.Fader,
            _ => null,
        };
        return new StripSpec(parts, Orientation.Vertical, meterAnchor, options.Label,
            Axis(options.Fader.Orientation), options.Fader.Direction == SkinFaderDirection.Reversed, Axis(options.Meter.Orientation));
    }

    internal static Orientation Axis(SkinLayoutOrientation orientation) =>
        orientation == SkinLayoutOrientation.Vertical ? Orientation.Vertical : Orientation.Horizontal;
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OpenXLR.UI.Skinning;

/// <summary>Which way a stack lays its children out.</summary>
public enum SkinLayoutOrientation
{
    /// <summary>One above the other, the classic window.</summary>
    Vertical,
    /// <summary>Side by side, in one row that scrolls when the window is narrower.</summary>
    Horizontal,
}

/// <summary>The edge of a dock a child sits against, or the rest of the dock.</summary>
public enum SkinLayoutEdge
{
    Top,
    Bottom,
    Left,
    Right,
    /// <summary>What the other children leave; only the last child fills.</summary>
    Fill,
}

/// <summary>How a section starts out under a skin.</summary>
public enum SkinRegionVisibility
{
    /// <summary>Shown and open, as every section is without a skin.</summary>
    Visible,
    /// <summary>Shown with its contents folded the first time the window wears the skin.</summary>
    Collapsed,
    /// <summary>Left out of the window until the user shows it from Arrange mode.</summary>
    Hidden,
}

/// <summary>How a grid track is sized.</summary>
public enum SkinTrackUnit
{
    /// <summary>As large as what it holds.</summary>
    Auto,
    /// <summary>A share of the room the other tracks leave.</summary>
    Star,
    /// <summary>A fixed number of device-independent pixels, raised to what it holds.</summary>
    Pixel,
}

/// <summary>One row or column of a grid.</summary>
/// <param name="Value">The star weight or the pixel size; 0 for <see cref="SkinTrackUnit.Auto"/>.</param>
public readonly record struct SkinLayoutTrack(SkinTrackUnit Unit, double Value)
{
    public static SkinLayoutTrack Auto { get; } = new(SkinTrackUnit.Auto, 0);

    public override string ToString() => Unit switch
    {
        SkinTrackUnit.Star => Value.ToString(CultureInfo.InvariantCulture) + "*",
        SkinTrackUnit.Pixel => Value.ToString(CultureInfo.InvariantCulture) + "px",
        _ => "auto",
    };
}

/// <summary>
/// The regions a skin's layout may place. Each id names one part of the
/// window the application owns and builds; a skin places it and never
/// supplies it.
/// </summary>
public static class SkinLayoutRegions
{
    public const string Header = "header";
    public const string Inputs = "inputs";
    public const string Headphones = "headphones";
    public const string Monitor = "monitor";
    public const string Applications = "applications";
    public const string Submixer = "submixer";

    /// <summary>The five sections, in the classic order.</summary>
    public static IReadOnlyList<string> Sections { get; } = [Inputs, Headphones, Monitor, Applications, Submixer];

    /// <summary>Every region a layout may place: the header, then the five sections.</summary>
    public static IReadOnlyList<string> All { get; } = [Header, .. Sections];

    public static bool Exists(string id) => All.Contains(id, StringComparer.Ordinal);

    public static bool IsSection(string id) => Sections.Contains(id, StringComparer.Ordinal);
}

/// <summary>
/// The header's actions a skin may order or put in the overflow menu, in
/// the order the header shows them without a skin. Options is not one of
/// them: it is always first and always shown, the way back from any skin.
/// </summary>
public static class SkinHeaderActions
{
    public const string Profiles = "profiles";
    public const string Arrange = "arrange";
    public const string Flow = "flow";
    public const string RestartDaemon = "restart-daemon";
    public const string Version = "version";
    public const string About = "about";

    public static IReadOnlyList<string> All { get; } = [Profiles, Arrange, Flow, RestartDaemon, Version, About];

    public static bool Exists(string id) => All.Contains(id, StringComparer.Ordinal);
}

/// <summary>
/// The templates a region or a strip can take. Each one is drawn by markup
/// the application owns; a skin names it and never supplies it.
/// </summary>
public static class SkinLayoutTemplates
{
    /// <summary>INPUTS as it has always been: the gain, the switches and the inserts in one panel.</summary>
    public const string HardwarePanel = "hardware-panel";
    /// <summary>INPUTS as one strip per input channel, each with its own parts.</summary>
    public const string InputRack = "input-rack";
    /// <summary>The submixer as it has always been: one tile per channel, the mix masters under them.</summary>
    public const string ChannelTiles = "channel-tiles";
    /// <summary>The submixer by mix: banks, each showing every channel's send into the mix its picker chose.</summary>
    public const string MixBanks = "mix-banks";
    /// <summary>An input rack's strip: one row of parts for one input channel.</summary>
    public const string InputRow = "input-row";
    /// <summary>A bank's strip: the parts of one send stacked in a column, the strips side by side.</summary>
    public const string VerticalStrip = "vertical-strip";
    /// <summary>A bank's strip: the parts of one send in a row, the rows one above the other.</summary>
    public const string SendList = "send-list";
    /// <summary>A bank's master as the mix tile the window has always drawn.</summary>
    public const string MasterCard = "master-card";
    /// <summary>A bank's master drawn like one more strip at the end of the bank.</summary>
    public const string MasterStrip = "master-strip";

    /// <summary>What <c>regions.inputs.template</c> may name; the first is the default.</summary>
    public static IReadOnlyList<string> Inputs { get; } = [HardwarePanel, InputRack];

    /// <summary>What <c>regions.submixer.template</c> may name; the first is the default.</summary>
    public static IReadOnlyList<string> Submixer { get; } = [ChannelTiles, MixBanks];

    /// <summary>What an input rack's <c>strip.template</c> may name.</summary>
    public static IReadOnlyList<string> InputStrips { get; } = [InputRow];

    /// <summary>What a bank's <c>strip.template</c> may name; the first is the default.</summary>
    public static IReadOnlyList<string> BankStrips { get; } = [VerticalStrip, SendList];

    /// <summary>What a bank's <c>master.template</c> may name; the first is the default.</summary>
    public static IReadOnlyList<string> Masters { get; } = [MasterCard, MasterStrip];
}

/// <summary>
/// The named parts of a strip. A skin orders them and may leave some out;
/// it cannot add one, and a part only ever shows the control the template
/// gives it.
/// </summary>
public static class SkinStripParts
{
    /// <summary>The channel's icon and name. Never hidden: a strip always says whose it is.</summary>
    public const string Identity = "identity";
    /// <summary>The channel's own level meter, left and right.</summary>
    public const string Meter = "meter";
    /// <summary>The input's hardware gain, where the interface has one.</summary>
    public const string Gain = "gain";
    /// <summary>The input's hardware switches: mute, low cut, phantom power and the rest the interface has.</summary>
    public const string HardwareControls = "hardware-controls";
    /// <summary>The input's plugin chain: its button and the chain at a glance.</summary>
    public const string Effects = "effects";
    /// <summary>The send's level into the bank's mix.</summary>
    public const string Fader = "fader";
    /// <summary>That level as a number.</summary>
    public const string Value = "value";
    /// <summary>The send's mute in the bank's mix.</summary>
    public const string Mute = "mute";

    /// <summary>An input row's parts, in their default order.</summary>
    public static IReadOnlyList<string> InputRow { get; } = [Identity, Meter, Gain, HardwareControls, Effects];

    /// <summary>A send strip's parts, in their default order.</summary>
    public static IReadOnlyList<string> Send { get; } = [Identity, Meter, Fader, Value, Mute];
}

/// <summary>How a strip names its channel.</summary>
public enum SkinLabelStyle
{
    /// <summary>The name alone.</summary>
    Text,
    /// <summary>The icon chosen in the layout editor, then the name.</summary>
    IconAndText,
    /// <summary>The icon alone, or the name when the channel has no icon. A screen reader still hears the whole name.</summary>
    Icon,
}

/// <summary>Where a strip's meter goes.</summary>
public enum SkinMeterPlacement
{
    /// <summary>A part of its own, where <c>partsOrder</c> puts it; by default straight under the name.</summary>
    BelowLabel,
    /// <summary>Beside the name, in the name's place.</summary>
    BesideLabel,
    /// <summary>Beside the fader, or the gain on an input row, in that part's place.</summary>
    BesideFader,
}

/// <summary>Which end of a fader is the top of its range.</summary>
public enum SkinFaderDirection
{
    /// <summary>The maximum at the top or at the right, as every fader has always been.</summary>
    Normal,
    /// <summary>The maximum at the bottom or at the left.</summary>
    Reversed,
}

/// <summary>A strip's meter: which way it runs and where it sits.</summary>
public sealed record SkinMeterOptions(
    SkinLayoutOrientation Orientation = SkinLayoutOrientation.Horizontal,
    SkinMeterPlacement Placement = SkinMeterPlacement.BelowLabel)
{
    public static SkinMeterOptions Default { get; } = new();

    internal string Description() => $"{Orientation}/{Placement}";
}

/// <summary>A strip's fader: which way it runs and which end is the top.</summary>
public sealed record SkinFaderOptions(
    SkinLayoutOrientation Orientation = SkinLayoutOrientation.Horizontal,
    SkinFaderDirection Direction = SkinFaderDirection.Normal)
{
    public static SkinFaderOptions Default { get; } = new();

    internal string Description() => $"{Orientation}/{Direction}";
}

/// <summary>
/// One strip template and how it is drawn: the order of its parts, the parts
/// left out, the label, the meter and the fader. <see cref="Order"/> is
/// complete: the parts the skin ordered, then the rest in their default order.
/// </summary>
public sealed record SkinStripOptions(
    string Template,
    IReadOnlyList<string> Order,
    IReadOnlyList<string> Hidden,
    SkinLabelStyle Label,
    SkinMeterOptions Meter,
    SkinFaderOptions Fader)
{
    /// <summary>An input row with every part in its default order.</summary>
    public static SkinStripOptions InputRowDefault { get; } = new(SkinLayoutTemplates.InputRow, SkinStripParts.InputRow, [],
        SkinLabelStyle.Text, SkinMeterOptions.Default, SkinFaderOptions.Default);

    /// <summary>A vertical send strip with every part in its default order and its fader and meter upright.</summary>
    public static SkinStripOptions BankDefault { get; } = new(SkinLayoutTemplates.VerticalStrip, SkinStripParts.Send, [],
        SkinLabelStyle.Text, new SkinMeterOptions(SkinLayoutOrientation.Vertical, SkinMeterPlacement.BesideFader),
        new SkinFaderOptions(SkinLayoutOrientation.Vertical));

    /// <summary>The parts the strip draws, in the order it draws them.</summary>
    public IReadOnlyList<string> Shown => [.. Order.Where(p => !Hidden.Contains(p, StringComparer.Ordinal))];

    internal string Description() =>
        $"{Template}[{string.Join(',', Shown)}]{Label}:{Meter.Description()}:{Fader.Description()}";
}

/// <summary>A bank's master: its template and how its label, meter and fader are drawn.</summary>
public sealed record SkinMasterOptions(
    string Template,
    SkinLabelStyle Label,
    SkinMeterOptions Meter,
    SkinFaderOptions Fader)
{
    public static SkinMasterOptions Default { get; } = new(SkinLayoutTemplates.MasterCard, SkinLabelStyle.Text,
        SkinMeterOptions.Default, SkinFaderOptions.Default);

    internal string Description() => $"{Template}:{Label}:{Meter.Description()}:{Fader.Description()}";
}

/// <summary>INPUTS: the panel the window has always drawn, or a rack of input strips.</summary>
public sealed record SkinInputsOptions(string Template, SkinStripOptions Strip)
{
    public static SkinInputsOptions Default { get; } = new(SkinLayoutTemplates.HardwarePanel, SkinStripOptions.InputRowDefault);

    public bool IsRack => Template == SkinLayoutTemplates.InputRack;

    internal string Description() => IsRack ? $"{Template}({Strip.Description()})" : Template;
}

/// <summary>
/// The submixer: the channel tiles the window has always drawn, or mix
/// banks. A bank always has a picker that lists every mix; the skin only
/// says how many banks show, which way they run, which mixes they start on
/// and how their strips and masters are drawn.
/// </summary>
/// <param name="SelectionDefaults">
/// Mix ids as mixer.json names them, which the banks show first when the
/// user has chosen none. An id with no mix behind it is skipped.
/// </param>
public sealed record SkinSubmixerOptions(
    string Template,
    SkinLayoutOrientation Orientation,
    int VisibleBanks,
    IReadOnlyList<string> SelectionDefaults,
    SkinStripOptions Strip,
    SkinMasterOptions Master)
{
    /// <summary>How many banks show when a skin with banks does not say.</summary>
    public const int DefaultVisibleBanks = 2;

    public static SkinSubmixerOptions Default { get; } = new(SkinLayoutTemplates.ChannelTiles, SkinLayoutOrientation.Horizontal,
        DefaultVisibleBanks, [], SkinStripOptions.BankDefault, SkinMasterOptions.Default);

    public bool IsBanks => Template == SkinLayoutTemplates.MixBanks;

    internal string Description() => IsBanks
        ? $"{Template}({Orientation},{VisibleBanks},{string.Join(',', SelectionDefaults)},{Strip.Description()},{Master.Description()})"
        : Template;
}

/// <summary>A node of the layout tree: a region or a container.</summary>
public abstract record SkinLayoutNode
{
    internal abstract void Describe(StringBuilder text);
}

/// <summary>A region placed in a container: one of <see cref="SkinLayoutRegions.All"/>.</summary>
public sealed record SkinLayoutRegion(string Id) : SkinLayoutNode
{
    internal override void Describe(StringBuilder text) => text.Append('<').Append(Id).Append('>');
}

/// <summary>
/// A child of a container and its place there. A grid child has a cell and
/// spans; a dock child has an edge; a stack child has neither, and the
/// fields keep their defaults.
/// </summary>
public sealed record SkinLayoutChild(SkinLayoutNode Node)
{
    public int Row { get; init; }
    public int Column { get; init; }
    public int RowSpan { get; init; } = 1;
    public int ColumnSpan { get; init; } = 1;
    public SkinLayoutEdge Edge { get; init; } = SkinLayoutEdge.Fill;
}

/// <summary>A container: a stack, a grid or a dock, with an id unique in its tree.</summary>
/// <param name="Spacing">The gap between children, in device-independent pixels.</param>
public abstract record SkinLayoutContainer(string Id, double Spacing, IReadOnlyList<SkinLayoutChild> Children) : SkinLayoutNode
{
    /// <summary>
    /// The children in the order the window places them and focus visits
    /// them: a stack's and a dock's in the file's order, a grid's by row and
    /// then by column.
    /// </summary>
    public virtual IReadOnlyList<SkinLayoutChild> Placed => Children;

    /// <summary>This container and every one inside it, outermost first.</summary>
    public IEnumerable<SkinLayoutContainer> Containers()
    {
        yield return this;
        foreach (SkinLayoutChild child in Placed)
            if (child.Node is SkinLayoutContainer inner)
                foreach (SkinLayoutContainer nested in inner.Containers())
                    yield return nested;
    }

    /// <summary>Every region the tree places, in placement order, a container's regions where the container stands.</summary>
    public IEnumerable<string> Regions()
    {
        foreach (SkinLayoutChild child in Placed)
        {
            if (child.Node is SkinLayoutRegion region) yield return region.Id;
            else if (child.Node is SkinLayoutContainer inner)
                foreach (string nested in inner.Regions())
                    yield return nested;
        }
    }

    /// <summary>The region ids this container places directly, in placement order.</summary>
    public IEnumerable<string> RegionOrder => Placed.Select(c => c.Node).OfType<SkinLayoutRegion>().Select(r => r.Id);

    /// <summary>Equal in what the window shows: kinds, ids, tracks, places, gaps and order.</summary>
    public bool SameAs(SkinLayoutContainer other) => Description() == other.Description();

    /// <summary>The tree written out in one line, equal for two trees that place everything alike.</summary>
    public string Description()
    {
        var text = new StringBuilder();
        Describe(text);
        return text.ToString();
    }

    private protected void DescribeChildren(StringBuilder text)
    {
        text.Append('[');
        foreach (SkinLayoutChild child in Placed)
        {
            text.Append(child.Row).Append(',').Append(child.Column).Append(',').Append(child.RowSpan).Append(',')
                .Append(child.ColumnSpan).Append(',').Append(child.Edge).Append(':');
            child.Node.Describe(text);
            text.Append(';');
        }
        text.Append(']');
    }
}

/// <summary>A stack: its children one after another, in one direction.</summary>
public sealed record SkinLayoutStack(
    string Id,
    SkinLayoutOrientation Orientation,
    double Spacing,
    IReadOnlyList<SkinLayoutChild> Children) : SkinLayoutContainer(Id, Spacing, Children)
{
    internal override void Describe(StringBuilder text)
    {
        text.Append("stack ").Append(Id).Append(' ').Append(Orientation).Append(' ')
            .Append(Spacing.ToString(CultureInfo.InvariantCulture));
        DescribeChildren(text);
    }
}

/// <summary>A grid: rows and columns of tracks, each child in a cell with its spans.</summary>
public sealed record SkinLayoutGrid(
    string Id,
    IReadOnlyList<SkinLayoutTrack> Rows,
    IReadOnlyList<SkinLayoutTrack> Columns,
    double Spacing,
    IReadOnlyList<SkinLayoutChild> Children) : SkinLayoutContainer(Id, Spacing, Children)
{
    public override IReadOnlyList<SkinLayoutChild> Placed =>
        [.. Children.OrderBy(c => c.Row).ThenBy(c => c.Column)];

    internal override void Describe(StringBuilder text)
    {
        text.Append("grid ").Append(Id).Append(' ').Append(string.Join(' ', Rows)).Append(" / ")
            .Append(string.Join(' ', Columns)).Append(' ').Append(Spacing.ToString(CultureInfo.InvariantCulture));
        DescribeChildren(text);
    }
}

/// <summary>A dock: children against its edges in order, the last one filling the rest.</summary>
public sealed record SkinLayoutDock(
    string Id,
    double Spacing,
    IReadOnlyList<SkinLayoutChild> Children) : SkinLayoutContainer(Id, Spacing, Children)
{
    internal override void Describe(StringBuilder text)
    {
        text.Append("dock ").Append(Id).Append(' ').Append(Spacing.ToString(CultureInfo.InvariantCulture));
        DescribeChildren(text);
    }
}

/// <summary>What a skin says about one section beyond where it goes.</summary>
/// <param name="Width">A preferred width, never below what the section needs; null for none.</param>
/// <param name="Height">A preferred height, a floor the section grows past; null for none.</param>
public sealed record SkinRegionOptions(
    SkinRegionVisibility Visibility = SkinRegionVisibility.Visible,
    double? Width = null,
    double? Height = null)
{
    public static SkinRegionOptions Default { get; } = new();
}

/// <summary>
/// The header's actions as a skin orders them: <see cref="Actions"/> first
/// in the row, then the ones it names nowhere in their usual order, and
/// <see cref="Overflow"/> in the menu at the end of the row.
/// </summary>
public sealed record SkinHeaderOptions(IReadOnlyList<string> Actions, IReadOnlyList<string> Overflow)
{
    public static SkinHeaderOptions Default { get; } = new([], []);

    /// <summary>The actions shown in the row, in order.</summary>
    public IReadOnlyList<string> Row =>
        [.. Actions, .. SkinHeaderActions.All.Where(a => !Actions.Contains(a) && !Overflow.Contains(a))];
}

/// <summary>
/// A skin's validated desktop layout. Every value has passed
/// <see cref="SkinLayoutReader"/>; nothing in it names a type, a binding, a
/// resource or a file.
/// </summary>
/// <param name="Version">The layout format version the skin was written for.</param>
/// <param name="Main">The main window's tree. Without the header in it, the header stays above it.</param>
public sealed record SkinLayout(int Version, SkinLayoutContainer Main)
{
    /// <summary>The sections' options, by region id; a section left out takes <see cref="SkinRegionOptions.Default"/>.</summary>
    public IReadOnlyDictionary<string, SkinRegionOptions> Regions { get; init; } =
        new Dictionary<string, SkinRegionOptions>(StringComparer.Ordinal);

    /// <summary>The header's action order and overflow.</summary>
    public SkinHeaderOptions Header { get; init; } = SkinHeaderOptions.Default;

    /// <summary>The template INPUTS is drawn with, and its strips.</summary>
    public SkinInputsOptions Inputs { get; init; } = SkinInputsOptions.Default;

    /// <summary>The template the submixer is drawn with, its banks, strips and masters.</summary>
    public SkinSubmixerOptions Submixer { get; init; } = SkinSubmixerOptions.Default;

    /// <summary>True when the tree places the header itself rather than leaving it above the tree.</summary>
    public bool PlacesHeader => Main.Regions().Contains(SkinLayoutRegions.Header, StringComparer.Ordinal);

    public SkinRegionOptions OptionsFor(string region) =>
        Regions.TryGetValue(region, out SkinRegionOptions? options) ? options : SkinRegionOptions.Default;

    /// <summary>The whole layout in one line: equal for two layouts the window draws alike.</summary>
    public string Description()
    {
        var text = new StringBuilder(Main.Description());
        foreach (string region in SkinLayoutRegions.Sections)
        {
            SkinRegionOptions options = OptionsFor(region);
            text.Append(' ').Append(region).Append('=').Append(options.Visibility).Append(',')
                .Append(options.Width?.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(options.Height?.ToString(CultureInfo.InvariantCulture));
        }
        text.Append(" header=").Append(string.Join(',', Header.Row)).Append('|').Append(string.Join(',', Header.Overflow));
        text.Append(" inputs=").Append(Inputs.Description()).Append(" submixer=").Append(Submixer.Description());
        return text.ToString();
    }
}

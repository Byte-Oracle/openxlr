using System;
using System.Collections.Generic;
using System.Linq;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>
/// One place a skin's tree has for a region: a child of a stack or a dock by
/// its index, or a child of a grid by its row and column. A user's
/// arrangement says which region sits in which place.
/// </summary>
/// <param name="Id">
/// The place's name in ui.json: <c>container:index</c> in a stack or a dock,
/// <c>container:row,column</c> in a grid.
/// </param>
/// <param name="Container">The container the place belongs to.</param>
/// <param name="Index">The child's index among the container's children, in the file's order.</param>
/// <param name="Child">The child as the skin wrote it, with its cell, spans or edge.</param>
/// <param name="SkinRegion">The region the skin itself puts here.</param>
internal sealed record LayoutSlot(string Id, SkinLayoutContainer Container, int Index, SkinLayoutChild Child, string SkinRegion)
{
    /// <summary>Every place in a tree, in placement order: the order focus visits them.</summary>
    internal static IReadOnlyList<LayoutSlot> Of(SkinLayoutContainer tree)
    {
        var slots = new List<LayoutSlot>();
        Collect(tree, slots);
        return slots;
    }

    private static void Collect(SkinLayoutContainer container, List<LayoutSlot> slots)
    {
        foreach (SkinLayoutChild child in container.Placed)
        {
            int index = IndexOf(container.Children, child);
            if (child.Node is SkinLayoutContainer inner) Collect(inner, slots);
            else if (child.Node is SkinLayoutRegion region)
                slots.Add(new LayoutSlot(container is SkinLayoutGrid
                    ? $"{container.Id}:{child.Row},{child.Column}" : $"{container.Id}:{index}",
                    container, index, child, region.Id));
        }
    }

    private static int IndexOf(IReadOnlyList<SkinLayoutChild> children, SkinLayoutChild child)
    {
        for (int i = 0; i < children.Count; i++)
            if (ReferenceEquals(children[i], child)) return i;
        return -1;
    }
}

/// <summary>What dropping one section on another does.</summary>
internal enum SectionDrop
{
    /// <summary>The section lands just before or after the other, as in a stack.</summary>
    Place,
    /// <summary>The two sections trade places.</summary>
    Swap,
}

/// <summary>
/// How the main window's regions stand: the tree they sit in, which region
/// is in which place, which sections are hidden, and whose arrangement it
/// is. Pure rules, testable without a window; the window draws it.
///
/// A skin without a layout has the classic tree, one vertical stack of the
/// five sections under the header, and the legacy saved order fills it.
/// A skin with a layout has its own tree; the user's moves are a
/// permutation of the sections over the tree's places, kept per skin and
/// layout version. The header stays in the place the skin gives it.
/// </summary>
/// <param name="Tree">The tree the regions sit in.</param>
/// <param name="Placement">Every place's id and the region in it now.</param>
/// <param name="Shown">The sections the skin hides that the user showed.</param>
/// <param name="Layout">The skin's layout, or null for a skin without one.</param>
/// <param name="Skin">The skin whose layout this is; null when the skin sets none.</param>
/// <param name="Version">That layout's version; null with <paramref name="Skin"/>.</param>
internal sealed record SectionArrangement(
    SkinLayoutContainer Tree,
    IReadOnlyDictionary<string, string> Placement,
    IReadOnlyList<string> Shown,
    SkinLayout? Layout,
    string? Skin,
    int? Version)
{
    /// <summary>The tile names in their usual order, which is the classic layout's region order.</summary>
    internal static IReadOnlyList<string> Tiles { get; } = [.. SkinLayoutRegions.Sections.Select(TileFor)];

    /// <summary>The section tile that shows a layout region.</summary>
    internal static string TileFor(string region) => region switch
    {
        SkinLayoutRegions.Inputs => "InputsTile",
        SkinLayoutRegions.Headphones => "HeadphonesTile",
        SkinLayoutRegions.Monitor => "MonitorTile",
        SkinLayoutRegions.Applications => "ApplicationsTile",
        SkinLayoutRegions.Submixer => "SubmixerTile",
        _ => throw new ArgumentException($"\"{region}\" is not a section.", nameof(region)),
    };

    /// <summary>The layout region a section tile shows, or null for a name that is not one.</summary>
    internal static string? RegionFor(string tile) =>
        SkinLayoutRegions.Sections.FirstOrDefault(region => TileFor(region) == tile);

    private IReadOnlyList<LayoutSlot>? _slots;

    /// <summary>The tree's places in placement order.</summary>
    internal IReadOnlyList<LayoutSlot> Slots => _slots ??= LayoutSlot.Of(Tree);

    /// <summary>The section tiles in placement order: down the classic column, or through the skin's tree.</summary>
    internal IReadOnlyList<string> Order => SectionsIn(Placement);

    /// <summary>The section tiles where the skin puts them, which Reset returns to.</summary>
    internal IReadOnlyList<string> SkinOrder => SectionsIn(SkinPlacement);

    private string[] SectionsIn(IReadOnlyDictionary<string, string> placement) =>
        [.. Slots.Select(s => placement[s.Id]).Where(SkinLayoutRegions.IsSection).Select(TileFor)];

    /// <summary>Every place with the region the skin itself puts there.</summary>
    internal IReadOnlyDictionary<string, string> SkinPlacement =>
        Slots.ToDictionary(s => s.Id, s => s.SkinRegion, StringComparer.Ordinal);

    /// <summary>True when the tree places the header; otherwise the header stays above it.</summary>
    internal bool HeaderInTree => Slots.Any(s => s.SkinRegion == SkinLayoutRegions.Header);

    /// <summary>The sections the skin hides, whatever the user did.</summary>
    internal IEnumerable<string> SkinHidden => SkinLayoutRegions.Sections
        .Where(r => Layout?.OptionsFor(r).Visibility == SkinRegionVisibility.Hidden);

    /// <summary>The sections out of the window now: the skin hides them and the user has not shown them.</summary>
    internal IReadOnlyList<string> Hidden => [.. SkinHidden.Where(r => !Shown.Contains(r, StringComparer.Ordinal))];

    /// <summary>A section's options from the skin, or the defaults.</summary>
    internal SkinRegionOptions OptionsFor(string region) => Layout?.OptionsFor(region) ?? SkinRegionOptions.Default;

    /// <summary>The header's order and overflow from the skin, or the usual row.</summary>
    internal SkinHeaderOptions Header => Layout?.Header ?? SkinHeaderOptions.Default;

    /// <summary>The place a region is in now.</summary>
    internal LayoutSlot SlotOf(string region) => Slots.First(s => Placement[s.Id] == region);

    /// <summary>The orientation of the stack a region is in now, or null in a grid or a dock.</summary>
    internal SkinLayoutOrientation? OrientationOf(string region) =>
        (SlotOf(region).Container as SkinLayoutStack)?.Orientation;

    /// <summary>True when the window would look the same: the tree, the places, the options and what is hidden.</summary>
    internal bool SameStructure(SectionArrangement other) =>
        (Layout?.Description() ?? Tree.Description()) == (other.Layout?.Description() ?? other.Tree.Description())
        && Slots.All(s => other.Placement.TryGetValue(s.Id, out string? r) && r == Placement[s.Id])
        && Hidden.SequenceEqual(other.Hidden, StringComparer.Ordinal);

    /// <summary>True when both start from the same skin, version and skin layout, so moves made in one hold in the other.</summary>
    internal bool SameOrigin(SectionArrangement other) =>
        Skin == other.Skin && Version == other.Version
        && (Layout?.Description() ?? "") == (other.Layout?.Description() ?? "")
        && SkinOrder.SequenceEqual(other.SkinOrder, StringComparer.Ordinal);

    /// <summary>
    /// The arrangement for the skin in force, in this precedence:
    ///
    /// - a skin with no layout: the saved <see cref="UiSettings.SectionOrder"/>,
    ///   made whole, down the classic column, exactly as before skins had
    ///   layouts;
    /// - a skin with a layout: the user's arrangement for that skin and layout
    ///   version over the skin's own placement and visibility. The legacy
    ///   order is not read. An arrangement that names a place or a region the
    ///   tree no longer has, or that would put a section in two places, is
    ///   not used at all and <paramref name="dropped"/> says so, so the window
    ///   can forget it and tell the user once.
    /// </summary>
    internal static SectionArrangement Resolve(string skin, SkinLayout? layout, UiSettings settings, out bool dropped)
    {
        dropped = false;
        if (layout is null)
        {
            SkinLayoutContainer classic = SkinLayoutDefaults.Classic.Main;
            string[] order = MainWindow.CompleteSectionOrder(settings.SectionOrder);
            IReadOnlyList<LayoutSlot> slots = LayoutSlot.Of(classic);
            var placement = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < slots.Count; i++) placement[slots[i].Id] = RegionFor(order[i])!;
            return new(classic, placement, [], null, null, null);
        }

        var skinned = new SectionArrangement(layout.Main, LayoutSlot.Of(layout.Main)
            .ToDictionary(s => s.Id, s => s.SkinRegion, StringComparer.Ordinal), [], layout, skin, layout.Version);
        if (settings.SkinArrangementFor(skin, layout.Version) is not { } saved) return skinned;
        if (skinned.WithSaved(saved) is { } arranged) return arranged;
        dropped = true;
        return skinned;
    }

    /// <summary>Resolve without caring whether a saved arrangement was dropped.</summary>
    internal static SectionArrangement Resolve(string skin, SkinLayout? layout, UiSettings settings) =>
        Resolve(skin, layout, settings, out _);

    /// <summary>
    /// The saved moves on top of the skin's placement, or null when they no
    /// longer fit the tree: a place or a region it does not have, the header
    /// moved, a section in two places, or a shown section the skin does not hide.
    /// </summary>
    private SectionArrangement? WithSaved(SkinArrangement saved)
    {
        var placement = new Dictionary<string, string>(SkinPlacement, StringComparer.Ordinal);
        foreach ((string slot, string region) in saved.Slots)
        {
            if (!placement.TryGetValue(slot, out string? there) || there == SkinLayoutRegions.Header
                || !SkinLayoutRegions.IsSection(region))
                return null;
            placement[slot] = region;
        }
        if (placement.Values.Distinct(StringComparer.Ordinal).Count() != placement.Count) return null;
        string[] hidden = [.. SkinHidden];
        if (saved.Shown.Any(r => !hidden.Contains(r, StringComparer.Ordinal))) return null;
        return this with { Placement = placement, Shown = [.. saved.Shown] };
    }

    /// <summary>This arrangement with two sections trading places.</summary>
    internal SectionArrangement Swap(string first, string second)
    {
        if (first == second || !SkinLayoutRegions.IsSection(first) || !SkinLayoutRegions.IsSection(second)) return this;
        string a = SlotOf(first).Id, b = SlotOf(second).Id;
        var placement = new Dictionary<string, string>(Placement, StringComparer.Ordinal) { [a] = second, [b] = first };
        return this with { Placement = placement };
    }

    /// <summary>
    /// What a drop of <paramref name="source"/> on <paramref name="target"/>
    /// does. Without a skin layout a section always lands before or after
    /// the other, as it always has. Under a skin layout it does so only
    /// between neighbours in one stack, where landing beside the other is the
    /// same as trading places with it; anywhere else the two trade places.
    /// </summary>
    internal SectionDrop DropFor(string source, string target)
    {
        if (Layout is null) return SectionDrop.Place;
        LayoutSlot from = SlotOf(source), to = SlotOf(target);
        return from.Container is SkinLayoutStack && ReferenceEquals(from.Container, to.Container)
            && Math.Abs(from.Index - to.Index) == 1 ? SectionDrop.Place : SectionDrop.Swap;
    }

    /// <summary>
    /// This arrangement after <paramref name="source"/> is dropped on
    /// <paramref name="target"/>, on the side <paramref name="after"/> says
    /// when the drop places rather than swaps (see <see cref="DropFor"/>).
    /// </summary>
    internal SectionArrangement Move(string source, string target, bool after)
    {
        if (source == target) return this;
        if (Layout is null)
        {
            string[] order = MainWindow.PlaceSection(Order, TileFor(source), TileFor(target), after);
            var placement = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < Slots.Count; i++) placement[Slots[i].Id] = RegionFor(order[i])!;
            return this with { Placement = placement };
        }
        if (DropFor(source, target) == SectionDrop.Swap) return Swap(source, target);
        // Neighbours in a stack: landing on the side the source is already on changes nothing.
        bool sourceFirst = SlotOf(source).Index < SlotOf(target).Index;
        return sourceFirst == after ? Swap(source, target) : this;
    }

    /// <summary>
    /// The section an arrow key moves <paramref name="region"/> next to, in
    /// the same container, or null when there is none that way.
    /// <paramref name="dx"/> and <paramref name="dy"/> are -1, 0 or 1. In a
    /// stack or a dock, left and up are the previous child and right and down
    /// the next; in a grid, the nearest cell that way that shares a row or a
    /// column with this one. Only sections <paramref name="usable"/> accepts count.
    /// </summary>
    internal string? Neighbour(string region, int dx, int dy, Func<string, bool> usable)
    {
        LayoutSlot from = SlotOf(region);
        LayoutSlot[] siblings = [.. Slots.Where(s => ReferenceEquals(s.Container, from.Container) && s.Id != from.Id
            && SkinLayoutRegions.IsSection(Placement[s.Id]) && usable(Placement[s.Id]))];
        LayoutSlot? next;
        if (from.Container is SkinLayoutGrid)
        {
            SkinLayoutChild a = from.Child;
            bool SharesRows(SkinLayoutChild b) => b.Row < a.Row + a.RowSpan && a.Row < b.Row + b.RowSpan;
            bool SharesColumns(SkinLayoutChild b) => b.Column < a.Column + a.ColumnSpan && a.Column < b.Column + b.ColumnSpan;
            next = (dx, dy) switch
            {
                (< 0, _) => siblings.Where(s => SharesRows(s.Child) && s.Child.Column + s.Child.ColumnSpan <= a.Column)
                    .OrderByDescending(s => s.Child.Column + s.Child.ColumnSpan).ThenBy(s => s.Child.Row).FirstOrDefault(),
                (> 0, _) => siblings.Where(s => SharesRows(s.Child) && s.Child.Column >= a.Column + a.ColumnSpan)
                    .OrderBy(s => s.Child.Column).ThenBy(s => s.Child.Row).FirstOrDefault(),
                (_, < 0) => siblings.Where(s => SharesColumns(s.Child) && s.Child.Row + s.Child.RowSpan <= a.Row)
                    .OrderByDescending(s => s.Child.Row + s.Child.RowSpan).ThenBy(s => s.Child.Column).FirstOrDefault(),
                (_, > 0) => siblings.Where(s => SharesColumns(s.Child) && s.Child.Row >= a.Row + a.RowSpan)
                    .OrderBy(s => s.Child.Row).ThenBy(s => s.Child.Column).FirstOrDefault(),
                _ => null,
            };
        }
        else
        {
            int step = dx + dy < 0 ? -1 : 1;
            next = step < 0
                ? siblings.Where(s => s.Index < from.Index).OrderByDescending(s => s.Index).FirstOrDefault()
                : siblings.Where(s => s.Index > from.Index).OrderBy(s => s.Index).FirstOrDefault();
        }
        return next is null ? null : Placement[next.Id];
    }

    /// <summary>This arrangement with a section the skin hides shown again.</summary>
    internal SectionArrangement Show(string region) =>
        Hidden.Contains(region, StringComparer.Ordinal) ? this with { Shown = [.. Shown, region] } : this;

    /// <summary>The skin's own placement and visibility, which Reset returns to.</summary>
    internal SectionArrangement Restored() => this with { Placement = SkinPlacement, Shown = [] };

    /// <summary>
    /// The settings with this arrangement saved where it belongs: the
    /// skin's own entry when the skin sets a layout, holding only the places
    /// whose section differs from the skin's and the sections shown, or the
    /// legacy order otherwise.
    /// </summary>
    internal UiSettings Save(UiSettings settings)
    {
        if (Skin is not { } skin) return settings with { SectionOrder = [.. Order] };
        IReadOnlyDictionary<string, string> own = SkinPlacement;
        Dictionary<string, string> moved = Placement.Where(p => own[p.Key] != p.Value)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return settings.WithSkinArrangement(skin, Version!.Value, moved, Shown);
    }

    /// <summary>
    /// The settings after Reset: the skin's own entry removed, so its
    /// placement and visibility come back, or the legacy order set to the usual one.
    /// </summary>
    internal UiSettings Reset(UiSettings settings) =>
        Skin is { } skin
            ? settings.WithSkinArrangement(skin, Version!.Value, null, null)
            : settings with { SectionOrder = [.. Tiles] };
}
